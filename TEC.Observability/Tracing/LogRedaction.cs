using System.Collections;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using TEC.Observability.Internal;

namespace TEC.Observability.Tracing;

/// <summary>
/// Redige dados sensíveis dos logs exportados, antes de qualquer exportador (OTLP, console, arquivo, Azure Monitor ou exportador
/// próprio): query string e credenciais de URLs (o mesmo formato dos spans, <c>?token=Redacted</c>), atributos com nome
/// sensível (senha, segredo, token, chave de API, connection string, autorização, cookie) e, dentro de textos, pares
/// <c>chave=valor</c> sensíveis (ex.: <c>Password=...</c> de uma connection string) e credenciais <c>Bearer</c>/<c>Basic</c>.
/// </summary>
/// <remarks>
/// <para>
/// Os logs de requisição do próprio ASP.NET Core (<c>Microsoft.AspNetCore.Hosting.Diagnostics</c>, nível <c>Information</c>:
/// <c>Request starting HTTP/1.1 GET https://host/rota?token=...</c>) trazem a query string no atributo <c>QueryString</c> e na
/// mensagem; os do <c>IHttpClientFactory</c> no .NET 8 trazem a URL completa da chamada de saída no atributo <c>Uri</c>. Com o
/// nível de log padrão, chegavam ao backend sem redação.
/// </para>
/// <para>
/// Regras: o atributo <c>QueryString</c>, valores <see cref="Uri"/> absolutos, atributos de nome sensível
/// (<see cref="SensitiveKeys"/>) e textos com dados sensíveis (<see cref="SensitiveText"/>) viram o formato redigido. A mensagem
/// formatada é refeita a partir do modelo (<c>{OriginalFormat}</c>) com os valores redigidos — ou, sem modelo, cada valor
/// original é trocado pelo redigido — e depois varrida como texto livre.
/// </para>
/// <para>
/// Exceção: quando a mensagem, o stack trace ou uma exceção interna tem dado sensível, <see cref="LogRecord.Exception"/> é
/// trocada por uma cópia redigida (<see cref="RedactedException"/>, com o tipo original no texto e em
/// <see cref="RedactedException.OriginalTypeName"/>); sem nada a redigir, a exceção original segue, com o seu tipo.
/// </para>
/// <para>
/// Escopos (<c>BeginScope</c>): o <see cref="LogRecord"/> só permite lê-los (<see cref="LogRecord.ForEachScope{TState}"/>), sem
/// API para reescrevê-los, então os exportadores que incluem escopos (OTLP, Azure Monitor, console, exportador próprio) recebem os
/// valores como foram registrados. O arquivo de logs (<c>LogFile</c>) redige os escopos ao gravar. Não coloque segredo nem dado
/// pessoal em escopo de log.
/// </para>
/// <para>
/// Custo: sem nada a redigir, nenhuma alocação — só a conferência do nome de cada atributo e buscas vetorizadas nos textos.
/// </para>
/// </remarks>
internal sealed class LogRedactionProcessor : BaseProcessor<LogRecord>
{
    /// <summary>Atributo do ASP.NET Core com a query string da requisição (logs de início e fim de requisição).</summary>
    internal const string QueryStringAttribute = "QueryString";

    /// <summary>Atributo com o modelo da mensagem (<c>"Pedido {PedidoId} criado"</c>), acrescentado pelo <c>ILogger</c>.</summary>
    internal const string OriginalFormatAttribute = "{OriginalFormat}";

    /// <summary>Valores redigidos mais curtos que isso não são procurados na mensagem sem modelo (trocariam trechos sem relação).</summary>
    private const int MinReplacementLength = 3;

    public override void OnEnd(LogRecord data) => Redact(data);

    internal static void Redact(LogRecord record)
    {
        if (record.Exception is { } exception && RedactedException.RedactIfNeeded(exception) is var safe && !ReferenceEquals(safe, exception))
            record.Exception = safe;

        KeyValuePair<string, object?>[]? redacted = null;
        List<(string Original, string Redacted)>? replacements = null;
        string? template = null;

        if (record.Attributes is { Count: > 0 } attributes)
        {
            for (var i = 0; i < attributes.Count; i++)
            {
                var (key, value) = attributes[i];
                if (key == OriginalFormatAttribute)
                {
                    template = value as string;
                    continue;
                }

                if (!TryRedact(key, value, ref replacements, out var newValue))
                    continue;

                redacted ??= [.. attributes];
                redacted[i] = new KeyValuePair<string, object?>(key, newValue);
            }

            if (redacted is not null)
                record.Attributes = redacted;
        }

        if (record.FormattedMessage is not { } message)
            return;

        if (redacted is not null)
        {
            message = template is not null && TryFormat(template, redacted, out var rendered)
                ? rendered
                : Replace(message, replacements);
        }

        var final = SensitiveText.Redact(message);
        if (!ReferenceEquals(final, record.FormattedMessage))
            record.FormattedMessage = final;
    }

    private static bool TryRedact(string key, object? value, ref List<(string, string)>? replacements, out object? newValue)
    {
        newValue = null;
        switch (value)
        {
            case null or bool:
                return false;

            case var _ when SensitiveKeys.IsSensitive(key):
                if (value is string { Length: 0 } || Equals(value, SensitiveText.RedactedValue))
                    return false;
                newValue = SensitiveText.RedactedValue;
                if (Convert.ToString(value, CultureInfo.InvariantCulture) is { Length: >= MinReplacementLength } original)
                    (replacements ??= []).Add((original, SensitiveText.RedactedValue));
                return true;

            case string query when key == QueryStringAttribute && TelemetryRedaction.HasQuery(query):
                var redactedQuery = TelemetryRedaction.RedactQuery(query);
                if (redactedQuery == query)
                    return false;
                newValue = redactedQuery;
                (replacements ??= []).Add((query, redactedQuery));
                return true;

            case Uri uri when SensitiveText.NeedsRedaction(uri):
                var redactedUrl = SensitiveText.RedactUrl(uri);
                newValue = redactedUrl;
                AddUrlReplacements(ref replacements, uri, uri.OriginalString, redactedUrl);
                return true;

            case string text:
                var redactedText = SensitiveText.Redact(text);
                if (ReferenceEquals(redactedText, text))
                    return false;
                newValue = redactedText;
                (replacements ??= []).Add((text, redactedText));
                return true;

            default:
                return false;
        }
    }

    /// <summary>A mensagem pode ter a URL como veio, na forma canônica (<c>ToString</c>) ou escapada (<c>AbsoluteUri</c>).</summary>
    private static void AddUrlReplacements(ref List<(string, string)>? replacements, Uri uri, string original, string redacted)
    {
        replacements ??= [];
        foreach (var form in (ReadOnlySpan<string>)[original, uri.ToString(), uri.AbsoluteUri])
        {
            if (form.Length > 0 && !replacements.Exists(r => r.Item1 == form))
                replacements.Add((form, redacted));
        }
    }

    private static string Replace(string message, List<(string Original, string Redacted)>? replacements)
    {
        if (replacements is null)
            return message;

        // As mais longas primeiro: a URL inteira antes de um pedaço dela (a query sozinha).
        foreach (var (original, redacted) in replacements.OrderByDescending(r => r.Original.Length))
            message = message.Replace(original, redacted, StringComparison.Ordinal);
        return message;
    }

    /// <summary>
    /// Refaz a mensagem a partir do modelo, com as mesmas regras do <c>ILogger</c> (<c>{Nome}</c>, <c>{Nome,alinhamento}</c>,
    /// <c>{Nome:formato}</c>, <c>{{</c>/<c>}}</c>, cultura invariante, <c>(null)</c> para nulo e coleções separadas por vírgula).
    /// <c>false</c> quando o modelo não casa com os atributos (aí vale a troca de valores na mensagem original).
    /// </summary>
    internal static bool TryFormat(string template, IReadOnlyList<KeyValuePair<string, object?>> attributes, out string message)
    {
        message = string.Empty;
        var builder = new StringBuilder(template.Length + 32);
        var i = 0;
        try
        {
            while (i < template.Length)
            {
                var c = template[i];
                if (c is '{' or '}' && i + 1 < template.Length && template[i + 1] == c)
                {
                    builder.Append(c);
                    i += 2;
                    continue;
                }

                if (c == '}')
                {
                    builder.Append(c);
                    i++;
                    continue;
                }

                if (c != '{')
                {
                    var next = template.AsSpan(i).IndexOfAny('{', '}');
                    var length = next < 0 ? template.Length - i : next;
                    builder.Append(template, i, length);
                    i += length;
                    continue;
                }

                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                    return false;

                var hole = template.AsSpan(i + 1, close - i - 1);
                var separator = hole.IndexOfAny(',', ':');
                var name = separator < 0 ? hole : hole[..separator];
                if (!TryFind(attributes, name, out var value))
                    return false;

                var argument = FormatArgument(value);
                if (separator < 0)
                    builder.Append(Convert.ToString(argument, CultureInfo.InvariantCulture));
                else
                    builder.AppendFormat(CultureInfo.InvariantCulture, string.Concat("{0", hole[separator..], "}"), argument);
                i = close + 1;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        message = builder.ToString();
        return true;
    }

    private static bool TryFind(IReadOnlyList<KeyValuePair<string, object?>> attributes, ReadOnlySpan<char> name, out object? value)
    {
        for (var i = 0; i < attributes.Count; i++)
        {
            if (name.Equals(attributes[i].Key, StringComparison.Ordinal))
            {
                value = attributes[i].Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    /// <summary>Como o <c>ILogger</c> formata um argumento: nulo vira <c>(null)</c> e coleções viram itens separados por vírgula.</summary>
    private static object FormatArgument(object? value)
    {
        const string Null = "(null)";
        switch (value)
        {
            case null:
                return Null;
            case string text:
                return text;
            case IEnumerable items:
                var builder = new StringBuilder();
                var first = true;
                foreach (var item in items)
                {
                    if (!first)
                        builder.Append(", ");
                    builder.Append(item is null ? Null : Convert.ToString(item, CultureInfo.InvariantCulture));
                    first = false;
                }

                return builder.ToString();
            default:
                return value;
        }
    }
}

#if !NET9_0_OR_GREATER
/// <summary>
/// No .NET 8, os logs padrão do <c>IHttpClientFactory</c> trazem a URL completa da chamada de saída, inclusive no escopo
/// <c>HTTP {Method} {Uri}</c> de todos os logs feitos durante a chamada — e o escopo não pode ser reescrito pelo
/// <see cref="LogRedactionProcessor"/>. Este logger substitui o padrão por mensagens equivalentes com a URL redigida (o .NET 9
/// ou superior já redige a query string nesses logs, e nada muda lá).
/// </summary>
/// <remarks>
/// Mensagens dos dois loggers padrão, nas mesmas categorias por cliente: <c>System.Net.Http.HttpClient.{nome}.LogicalHandler</c>
/// (início, fim e falha da chamada, por fora de todo o pipeline) e <c>System.Net.Http.HttpClient.{nome}.ClientHandler</c> (cada
/// tentativa, por dentro de retries). Os filtros de <c>Logging:LogLevel:System.Net.Http.HttpClient.{nome}</c> continuam valendo.
/// Um cliente que configurar o próprio logging (<c>RemoveAllLoggers</c>, <c>AddLogger</c>) substitui este.
/// </remarks>
internal sealed partial class RedactingHttpClientLogger : Microsoft.Extensions.Http.Logging.IHttpClientLogger
{
    internal const string CategoryPrefix = "System.Net.Http.HttpClient";

    /// <summary>
    /// Nome do cliente cujo pipeline está sendo montado. O <c>IHttpClientFactory</c> roda as ações do builder e, logo depois, as
    /// de logging, na mesma thread e de forma síncrona: <see cref="CaptureClientName"/> grava o nome e a fábrica do logger o lê.
    /// </summary>
    [ThreadStatic]
    private static string? _buildingClientName;

    private readonly ILogger _logger;
    private readonly bool _logical;

    private RedactingHttpClientLogger(ILoggerFactory loggerFactory, string? clientName, bool logical)
    {
        var name = string.IsNullOrEmpty(clientName) ? "Default" : clientName;
        _logger = loggerFactory.CreateLogger($"{CategoryPrefix}.{name}.{(logical ? "LogicalHandler" : "ClientHandler")}");
        _logical = logical;
    }

    /// <summary>Ação do builder de handlers que guarda o nome do cliente para os loggers criados em seguida.</summary>
    internal static void CaptureClientName(Microsoft.Extensions.Http.HttpMessageHandlerBuilder builder) => _buildingClientName = builder.Name;

    /// <summary>Logger da chamada inteira (por fora do pipeline), como o <c>LogicalHandler</c> padrão.</summary>
    internal static RedactingHttpClientLogger Logical(ILoggerFactory loggerFactory) => new(loggerFactory, _buildingClientName, logical: true);

    /// <summary>Logger de cada tentativa (por dentro do pipeline), como o <c>ClientHandler</c> padrão.</summary>
    internal static RedactingHttpClientLogger Client(ILoggerFactory loggerFactory) => new(loggerFactory, _buildingClientName, logical: false);

    public object? LogRequestStart(HttpRequestMessage request)
    {
        if (_logical)
            RequestPipelineStart(_logger, request.Method, Url(request));
        else
            ClientLog.RequestStart(_logger, request.Method, Url(request));
        return null;
    }

    public void LogRequestStop(object? context, HttpRequestMessage request, HttpResponseMessage response, TimeSpan elapsed)
    {
        if (_logical)
            RequestPipelineEnd(_logger, elapsed.TotalMilliseconds, (int)response.StatusCode);
        else
            ClientLog.RequestEnd(_logger, elapsed.TotalMilliseconds, (int)response.StatusCode);
    }

    public void LogRequestFailed(object? context, HttpRequestMessage request, HttpResponseMessage? response, Exception exception, TimeSpan elapsed)
    {
        if (_logical)
            RequestPipelineFailed(_logger, exception, request.Method, Url(request), elapsed.TotalMilliseconds);
        else
            ClientLog.RequestFailed(_logger, exception, request.Method, Url(request), elapsed.TotalMilliseconds);
    }

    private static string? Url(HttpRequestMessage request) => request.RequestUri switch
    {
        null => null,
        { IsAbsoluteUri: true } uri when SensitiveText.NeedsRedaction(uri) => SensitiveText.RedactUrl(uri),
        { IsAbsoluteUri: true } uri => uri.GetLeftPart(UriPartial.Path) + uri.Query,
        var uri => uri.OriginalString.Split('?', 2)[0],
    };

    [LoggerMessage(EventId = 100, EventName = "RequestPipelineStart", Level = LogLevel.Information, Message = "Start processing HTTP request {HttpMethod} {Uri}")]
    private static partial void RequestPipelineStart(ILogger logger, HttpMethod httpMethod, string? uri);

    [LoggerMessage(EventId = 101, EventName = "RequestPipelineEnd", Level = LogLevel.Information, Message = "End processing HTTP request after {ElapsedMilliseconds}ms - {StatusCode}")]
    private static partial void RequestPipelineEnd(ILogger logger, double elapsedMilliseconds, int statusCode);

    [LoggerMessage(EventId = 102, EventName = "RequestPipelineFailed", Level = LogLevel.Information, Message = "HTTP request {HttpMethod} {Uri} failed after {ElapsedMilliseconds}ms")]
    private static partial void RequestPipelineFailed(ILogger logger, Exception exception, HttpMethod httpMethod, string? uri, double elapsedMilliseconds);

    /// <summary>Mensagens do <c>ClientHandler</c> padrão: mesmos ids de evento do <c>LogicalHandler</c>, em outra categoria.</summary>
    private static partial class ClientLog
    {
        [LoggerMessage(EventId = 100, EventName = "RequestStart", Level = LogLevel.Information, Message = "Sending HTTP request {HttpMethod} {Uri}")]
        internal static partial void RequestStart(ILogger logger, HttpMethod httpMethod, string? uri);

        [LoggerMessage(EventId = 101, EventName = "RequestEnd", Level = LogLevel.Information, Message = "Received HTTP response headers after {ElapsedMilliseconds}ms - {StatusCode}")]
        internal static partial void RequestEnd(ILogger logger, double elapsedMilliseconds, int statusCode);

        [LoggerMessage(EventId = 102, EventName = "RequestFailed", Level = LogLevel.Information, Message = "HTTP request {HttpMethod} {Uri} failed after {ElapsedMilliseconds}ms")]
        internal static partial void RequestFailed(ILogger logger, Exception exception, HttpMethod httpMethod, string? uri, double elapsedMilliseconds);
    }
}
#endif
