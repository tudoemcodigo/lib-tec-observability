using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using TEC.Observability.Configuration;
using TEC.Observability.Internal;

namespace TEC.Observability.Tracing;

/// <summary>Nomes usados na correlação de requisições.</summary>
public static class CorrelationId
{
    /// <summary>Cabeçalho HTTP de entrada e de saída.</summary>
    public const string HeaderName = "X-Correlation-ID";

    /// <summary>Nome do atributo no span, no Baggage e no escopo de log.</summary>
    public const string AttributeName = "correlation.id";

    /// <summary>Correlation id da requisição atual, ou <c>null</c> fora de uma requisição.</summary>
    public static string? Current => Baggage.GetBaggage(AttributeName);
}

/// <summary>
/// Liga o <c>X-Correlation-ID</c> ao trace: o valor recebido (ou o TraceId, se não vier) vira atributo do span, escopo de log,
/// cabeçalho de resposta e item de Baggage — que segue, junto com o <c>traceparent</c> (W3C), para os serviços chamados cujos
/// hosts estão em <see cref="ObservabilityOptions.BaggageAllowedHosts"/>.
/// </summary>
internal sealed class CorrelationIdMiddleware
{
    private const int MaxLength = 128;

    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;
    private readonly bool _allowInboundBaggage;
    private readonly BaggageHostPolicy _baggagePolicy;

    public CorrelationIdMiddleware(RequestDelegate next, IOptions<ObservabilityOptions> options, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _allowInboundBaggage = options.Value.AllowInboundBaggage;
        _baggagePolicy = new BaggageHostPolicy(options.Value.BaggageAllowedHosts ?? []);
        BaggageHostPolicy.Default = _baggagePolicy;

        // Com Provider None não há TracerProvider (que é quem instala o filtro no registro), mas o HttpClient continua
        // propagando o Baggage do Activity: o filtro por host precisa existir antes da primeira requisição.
        HostFilteringPropagator.Install();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Resolve(context);

        if (!_allowInboundBaggage)
            DiscardInboundBaggage();

        BaggageHostPolicy.Current = _baggagePolicy;

        Activity.Current?.SetTag(CorrelationId.AttributeName, correlationId);
        Baggage.SetBaggage(CorrelationId.AttributeName, correlationId);

        context.Response.OnStarting(static state =>
        {
            var (response, id) = ((HttpResponse, string))state;
            response.Headers[CorrelationId.HeaderName] = id;
            return Task.CompletedTask;
        }, (context.Response, correlationId));

        using (_logger.BeginScope(new CorrelationScope(correlationId)))
        {
            await _next(context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// O cabeçalho <c>baggage</c> vem do cliente e, sem isto, seguiria para todos os serviços chamados. É retirado dos dois lugares
    /// de onde sai a propagação: o Baggage do OpenTelemetry e o do <see cref="Activity"/> (usado quando a exportação está desligada).
    /// </summary>
    private static void DiscardInboundBaggage()
    {
        Baggage.ClearBaggage();

        if (Activity.Current is { } activity)
        {
            foreach (var item in activity.Baggage.ToArray())
                activity.SetBaggage(item.Key, null);
        }
    }

    private static string Resolve(HttpContext context)
    {
        // O cabeçalho explícito vence; sem ele, vale o Baggage propagado pelo serviço chamador.
        var header = context.Request.Headers[CorrelationId.HeaderName].ToString();
        if (IsSafe(header))
            return header;

        var baggage = Baggage.GetBaggage(CorrelationId.AttributeName);
        if (IsSafe(baggage))
            return baggage!;

        return Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
    }

    /// <summary>O valor vem do cliente e vai para log e cabeçalho de resposta: só aceita caracteres que não permitem injeção.</summary>
    internal static bool IsSafe(string? value) => AsciiToken.IsValid(value, MaxLength, "-_.:");
}

/// <summary>
/// Escopo de log com o correlation id: um único item, sem o dicionário que seria alocado a cada requisição. Os provedores de
/// log leem os itens (<c>correlation.id</c>) como de qualquer escopo estruturado.
/// </summary>
internal sealed class CorrelationScope(string correlationId) : IReadOnlyList<KeyValuePair<string, object>>
{
    public int Count => 1;

    public KeyValuePair<string, object> this[int index] => index == 0
        ? new KeyValuePair<string, object>(CorrelationId.AttributeName, correlationId)
        : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        yield return this[0];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"{CorrelationId.AttributeName}:{correlationId}";
}
