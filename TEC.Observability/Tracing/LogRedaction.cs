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
/// mensagem. Com o nível de log padrão, chegavam ao backend sem redação.
/// </para>
/// <para>
/// Regras: o atributo <c>QueryString</c>, valores <see cref="Uri"/> absolutos, atributos de nome sensível
/// (<see cref="SensitiveKeys"/>) e textos com dados sensíveis (<see cref="SensitiveText"/>) viram o formato redigido. Objetos
/// (<c>record</c>, tipo anônimo, DTO) e coleções têm o texto de cada valor varrido (<see cref="SensitiveValue"/>) e, com algo a
/// redigir, viram o texto redigido (ou um <c>string[]</c> com os itens redigidos). A mensagem
/// formatada é refeita a partir do modelo (<c>{OriginalFormat}</c>) com os valores redigidos — ou, sem modelo, cada valor
/// original é trocado pelo redigido — e depois varrida como texto livre.
/// </para>
/// <para>
/// Exceção: quando a mensagem, o stack trace ou uma exceção interna tem dado sensível, <see cref="LogRecord.Exception"/> é
/// trocada por uma cópia redigida (<see cref="RedactedException"/>, com o tipo original no texto e em
/// <see cref="RedactedException.OriginalTypeName"/>); sem nada a redigir, a exceção original segue, com o seu tipo.
/// </para>
/// <para>
/// Escopos (<c>BeginScope</c>): o <see cref="LogRecord"/> não tem API para reescrevê-los, então o <c>IncludeScopes</c> do
/// OpenTelemetry fica desligado e os escopos não seguem como foram registrados. Com <c>Observability:IncludeScopes</c>, este
/// processador lê os escopos ativos (<see cref="LogScopeCapture"/>) e acrescenta aos atributos só as chaves permitidas
/// (<see cref="LogScopePolicy"/>), com os valores redigidos — é assim que os exportadores (OTLP, Azure Monitor, console, arquivo,
/// exportador próprio) os recebem.
/// </para>
/// <para>
/// Custo: sem nada a redigir, nenhuma alocação nos atributos de texto e escalares — só a conferência do nome de cada atributo e
/// buscas vetorizadas nos textos. Objetos e coleções que não são <c>string[]</c> custam um <c>ToString</c> por valor (o que o
/// exportador também faz).
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

    /// <summary>Teto de itens de escopo acrescentados a um log (escopos aninhados sem limite não viram atributos sem limite).</summary>
    internal const int MaxScopeAttributes = 64;

    private readonly LogScopePolicy? _scopePolicy;
    private readonly LogScopeCapture? _scopeCapture;

    /// <summary>Só redação, sem escopos.</summary>
    public LogRedactionProcessor()
    {
    }

    /// <summary>Redação e, com <paramref name="scopePolicy"/>, os escopos permitidos como atributos redigidos.</summary>
    internal LogRedactionProcessor(LogScopePolicy? scopePolicy, LogScopeCapture? scopeCapture)
    {
        _scopePolicy = scopePolicy;
        _scopeCapture = scopeCapture;
    }

    public override void OnEnd(LogRecord data)
    {
        Redact(data);
        // OnEnd roda na mesma chamada do ILogger.Log: os escopos ativos são os do log.
        if (_scopePolicy is not null && _scopeCapture?.Scopes is { } scopes)
            AppendScopes(data, scopes, _scopePolicy);
    }

    /// <summary>
    /// Acrescenta aos atributos do registro os itens dos escopos ativos com chave permitida, do escopo mais externo para o mais
    /// interno. Uma chave que já existe nos atributos (ou num escopo anterior) não é repetida; nulos e escopos sem chave saem.
    /// </summary>
    internal static void AppendScopes(LogRecord record, IExternalScopeProvider scopes, LogScopePolicy policy)
    {
        var collector = new ScopeCollector(record.Attributes, policy);
        scopes.ForEachScope(static (scope, state) => state.Add(scope), collector);
        if (collector.Items is { Count: > 0 } items)
            record.Attributes = [.. record.Attributes ?? [], .. items];
    }

    /// <summary>Valor de escopo redigido: nome sensível vira <c>Redacted</c>; textos, objetos e coleções passam pela redação de texto livre.</summary>
    internal static object RedactScopeValue(string key, object value)
    {
        if (value is bool)
            return value;
        if (SensitiveKeys.IsSensitive(key))
            return SensitiveText.RedactedValue;
        if (value is string text)
            return SensitiveText.Redact(text);
        return SensitiveValue.TryRedact(value, out var redacted) ? redacted : value;
    }

    private sealed class ScopeCollector(IReadOnlyList<KeyValuePair<string, object?>>? attributes, LogScopePolicy policy)
    {
        public List<KeyValuePair<string, object?>>? Items { get; private set; }

        public void Add(object? scope)
        {
            if (scope is not IEnumerable<KeyValuePair<string, object?>> pairs)
                return;

            try
            {
                foreach (var (key, value) in pairs)
                {
                    if (Items?.Count >= MaxScopeAttributes)
                        return;
                    if (value is null || key is null || !policy.Allows(key) || Contains(attributes, key) || Contains(Items, key))
                        continue;
                    (Items ??= []).Add(new KeyValuePair<string, object?>(key, RedactScopeValue(key, value)));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Escopo que falha ao ser enumerado (estado do chamador) não derruba o log: os itens já lidos ficam.
            }
        }

        private static bool Contains(IReadOnlyList<KeyValuePair<string, object?>>? list, string key)
        {
            if (list is null)
                return false;
            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].Key, key, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }

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
                    if (template is not null)
                    {
                        var safeTemplate = SensitiveText.Redact(template);
                        if (!ReferenceEquals(safeTemplate, template))
                        {
                            redacted ??= [.. attributes];
                            redacted[i] = new KeyValuePair<string, object?>(key, safeTemplate);
                            record.Body = safeTemplate;
                        }
                    }
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

        if (record.Body is { } currentBody)
        {
            var safeBody = SensitiveText.Redact(currentBody);
            if (!ReferenceEquals(safeBody, currentBody))
                record.Body = safeBody;
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

            // Objetos (record, tipo anônimo, DTO) e coleções: os exportadores gravam o ToString ou cada item.
            case not Uri when SensitiveValue.TryRedact(value, out var redactedValue):
                newValue = redactedValue;
                if (Convert.ToString(FormatArgument(value), CultureInfo.InvariantCulture) is { Length: >= MinReplacementLength } shown)
                    (replacements ??= []).Add((shown, Convert.ToString(FormatArgument(redactedValue), CultureInfo.InvariantCulture)!));
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

/// <summary>
/// <c>ILoggerFactory</c> que envolve o da aplicação (opção <c>RedactAllLoggerProviders</c>): todo logger criado entrega a
/// <b>todos</b> os provedores (console, Debug, EventLog, OpenTelemetry...) a mensagem formatada já redigida e a exceção trocada
/// pela cópia redigida. Envolve a fábrica, e não cada provedor, para os filtros por provedor (<c>Logging:Console:LogLevel</c>)
/// continuarem casando com o tipo original do provedor.
/// </summary>
internal sealed class RedactingLoggerFactory(ILoggerFactory inner, bool ownsInner) : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new RedactingLogger(inner.CreateLogger(categoryName));

    public void AddProvider(ILoggerProvider provider) => inner.AddProvider(provider);

    public void Dispose()
    {
        if (ownsInner)
            inner.Dispose();
    }

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            // Sem provedor interessado, nada é formatado (nem a closure abaixo é alocada).
            if (!inner.IsEnabled(logLevel))
                return;

            var safeException = exception is null ? null : RedactedException.RedactIfNeeded(exception);
            inner.Log(logLevel, eventId, state, safeException, (s, e) => SensitiveText.Redact(formatter(s, e)));
        }
    }
}

/// <summary>
/// Chaves de escopo exportadas (<c>Observability:AllowedScopeKeys</c>), sem diferenciar maiúsculas; <c>*</c> permite todas. O
/// <c>correlation.id</c> da biblioteca sempre entra; chave vazia e <c>{OriginalFormat}</c> nunca.
/// </summary>
internal sealed class LogScopePolicy
{
    private readonly bool _any;
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase) { CorrelationId.AttributeName };

    public LogScopePolicy(IEnumerable<string?> keys)
    {
        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;
            if (key.Trim() == "*")
                _any = true;
            else
                _keys.Add(key.Trim());
        }
    }

    public bool Allows(string key) =>
        key.Length > 0 && key != LogRedactionProcessor.OriginalFormatAttribute && (_any || _keys.Contains(key));
}

/// <summary>
/// Provedor de log que não grava nada: só recebe do <c>ILoggerFactory</c> o <see cref="IExternalScopeProvider"/> (os escopos
/// ativos de <c>BeginScope</c>), que o <see cref="LogRedactionProcessor"/> lê para exportar os escopos permitidos. Fica fora de
/// todos os filtros de log (nível <c>None</c>), então nenhuma mensagem passa por ele.
/// </summary>
internal sealed class LogScopeCapture : ILoggerProvider, ISupportExternalScope
{
    private volatile IExternalScopeProvider? _scopes;

    /// <summary>Escopos do <c>ILoggerFactory</c>, ou <c>null</c> antes de a fábrica registrar este provedor.</summary>
    public IExternalScopeProvider? Scopes => _scopes;

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public ILogger CreateLogger(string categoryName) => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose()
    {
    }
}
