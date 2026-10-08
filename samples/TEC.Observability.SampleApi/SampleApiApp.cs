using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TEC.Observability.DependencyInjection;
using TEC.Observability.HealthChecks;
using TEC.Observability.Tracing;

namespace TEC.Observability.SampleApi;

/// <summary>
/// API de exemplo com o TEC.Observability ligado como num serviço real. Alvo do gerador de carga, dos testes de carga e dos
/// benchmarks de pipeline.
/// </summary>
/// <remarks>
/// Configuração (appsettings, variáveis de ambiente ou linha de comando): a seção <c>Observability</c> do componente (padrão
/// <c>Provider: Contagem</c>, que conta a telemetria sem enviá-la) e <c>Exemplo:LatenciaDependenciaMs</c> (padrão 20), a
/// latência da dependência simulada do readiness.
/// </remarks>
public static class SampleApiApp
{
    /// <summary><c>Observability:ServiceName</c> padrão do exemplo (também o nome do <see cref="ActivitySource"/> e do <see cref="Meter"/>).</summary>
    public const string ServiceName = "tec-observability-exemplo";

    /// <summary>Nome do <see cref="HttpClient"/> da chamada de saída para o próprio serviço (<c>/interno/eco</c>).</summary>
    public const string InternalClient = "interno";

    /// <summary>Valores padrão, com prioridade menor que appsettings, variáveis de ambiente e linha de comando.</summary>
    private static readonly Dictionary<string, string?> Defaults = new()
    {
        ["Observability:ServiceName"] = ServiceName,
        ["Observability:ServiceVersion"] = "1.0.0",
        ["Observability:Environment"] = "exemplo",
        ["Observability:Provider"] = CountingTelemetryExporter.ProviderName,
        ["Observability:IgnoredPaths:0"] = "/health",
        ["Observability:IgnoredPaths:1"] = "/swagger",
        ["Observability:IgnoredPaths:2"] = "/diagnostico",
        ["Observability:BaggageAllowedHosts:0"] = "127.0.0.1",
        ["Observability:BaggageAllowedHosts:1"] = "localhost",
        ["Observability:HealthChecks:CacheDuration"] = "00:00:01",
        ["Observability:LogFile:FolderPath"] = "logs",
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        ["Logging:LogLevel:System.Net.Http"] = "Warning",
        ["Exemplo:LatenciaDependenciaMs"] = "20",
    };

    /// <summary>Cria a aplicação já configurada (sem iniciá-la).</summary>
    /// <param name="args">Argumentos da linha de comando.</param>
    /// <param name="configure">Ajustes extras no builder, antes do registro da observabilidade (ex.: porta e configuração em testes).</param>
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        ((IConfigurationBuilder)builder.Configuration).Sources.Insert(0, new MemoryConfigurationSource { InitialData = Defaults });
        configure?.Invoke(builder);

        var counters = new TelemetryCounters();
        builder.Services.AddSingleton(counters);
        builder.Services.AddSingleton(_ => new ActivitySource(ServiceName)); // por fábrica: o container descarta no fim
        builder.Services.AddSingleton<SampleMetrics>();
        builder.Services.AddHttpClient(InternalClient, client => client.Timeout = TimeSpan.FromSeconds(10));

        var observability = builder.Services.AddTecObservability(builder.Configuration)
            .AddExporter(new CountingTelemetryExporter());
        observability.HealthChecks.AddCheck<SimulatedDependencyHealthCheck>("dependencia-simulada", tags: [HealthCheckTags.Ready]);

        var app = builder.Build();
        app.UseTecObservability();
        MapEndpoints(app);
        return app;
    }

    private static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/pedidos/{id:int}", (int id, ActivitySource source, SampleMetrics metrics, ILogger<Order> logger) =>
        {
            using var activity = source.StartActivity("ConsultarPedido");
            activity?.SetTag("pedido.id", id);
            metrics.Consulted.Add(1);
            OrderLog.Queried(logger, id);
            return Results.Ok(new OrderResponse(id, CorrelationId.Current, Activity.Current?.TraceId.ToString()));
        });

        app.MapPost("/pedidos", (NewOrder order, ActivitySource source, SampleMetrics metrics, ILogger<Order> logger) =>
        {
            if (order.Items is < 1 or > 100 || string.IsNullOrWhiteSpace(order.Customer))
            {
                OrderLog.Rejected(logger, order.Items);
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["itens"] = ["Informe de 1 a 100 itens e o cliente."] });
            }

            using var activity = source.StartActivity("CriarPedido");
            var id = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, int.MaxValue);
            activity?.SetTag("pedido.id", id);
            activity?.SetTag("pedido.itens", order.Items);
            metrics.Created.Add(1);
            metrics.Items.Record(order.Items);
            OrderLog.Created(logger, id, order.Items);
            return Results.Created($"/pedidos/{id}", new OrderResponse(id, CorrelationId.Current, Activity.Current?.TraceId.ToString()));
        });

        // Chamada de saída para o próprio serviço: exercita a instrumentação do HttpClient e a propagação do traceparent e do Baggage.
        // O destino vem do endereço local da conexão (decidido pelo servidor), nunca do cabeçalho Host, que o cliente escolhe:
        // com o Host, qualquer cliente faria o serviço chamar um endereço arbitrário (SSRF).
        app.MapGet("/pedidos/{id:int}/externo", async (int id, HttpContext context, IHttpClientFactory factory) =>
        {
            if (context.Connection.LocalIpAddress is not { } address)
                return Results.Problem("Endereço local indisponível.", statusCode: StatusCodes.Status500InternalServerError);

            var client = factory.CreateClient(InternalClient);
            var host = address.IsIPv4MappedToIPv6 ? address.MapToIPv4().ToString()
                : address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
            var self = new Uri($"{context.Request.Scheme}://{host}:{context.Connection.LocalPort}/interno/eco");
            var echo = await client.GetFromJsonAsync<EchoResponse>(self, context.RequestAborted);
            return Results.Ok(new ExternalResponse(id, CorrelationId.Current, echo));
        });

        app.MapGet("/interno/eco", (HttpRequest request) =>
            Results.Ok(new EchoResponse(request.Headers["baggage"].ToString(), request.Headers["traceparent"].ToString())));

        // A query string traz um "token": a redação da biblioteca precisa tirá-lo dos spans
        app.MapGet("/busca", (string? q) => Results.Ok(new { resultados = (q ?? string.Empty).Length }));

        app.MapGet("/erro", (ILogger<Order> logger) =>
        {
            try
            {
                throw new InvalidOperationException("Falha simulada no processamento do pedido.");
            }
            catch (InvalidOperationException ex)
            {
                OrderLog.Failed(logger, ex);
                return Results.Problem("Falha simulada.", statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        app.MapGet("/diagnostico/contadores", (TelemetryCounters counters) => Results.Ok(counters.Snapshot()));
    }

    /// <summary>Categoria dos logs de negócio.</summary>
    public sealed class Order;
}

/// <summary>Corpo do <c>POST /pedidos</c>.</summary>
public sealed record NewOrder([property: JsonPropertyName("cliente")] string? Customer, [property: JsonPropertyName("itens")] int Items);

/// <summary>Resposta das rotas de pedido: o correlation id e o trace vistos pelo servidor.</summary>
public sealed record OrderResponse(int Id, string? CorrelationId, string? TraceId);

/// <summary>Cabeçalhos recebidos por <c>/interno/eco</c>.</summary>
public sealed record EchoResponse(string? Baggage, string? Traceparent);

/// <summary>Resposta de <c>/pedidos/{id}/externo</c>.</summary>
public sealed record ExternalResponse(int Id, string? CorrelationId, [property: JsonPropertyName("eco")] EchoResponse? Echo);

/// <summary>Métricas de negócio do exemplo.</summary>
public sealed class SampleMetrics : IDisposable
{
    private readonly Meter _meter = new(SampleApiApp.ServiceName);

    public SampleMetrics()
    {
        Consulted = _meter.CreateCounter<long>("pedidos.consultados");
        Created = _meter.CreateCounter<long>("pedidos.criados");
        Items = _meter.CreateHistogram<int>("pedidos.itens");
    }

    public Counter<long> Consulted { get; }
    public Counter<long> Created { get; }
    public Histogram<int> Items { get; }

    public void Dispose() => _meter.Dispose();
}

/// <summary>Dependência do readiness com latência configurável (<c>Exemplo:LatenciaDependenciaMs</c>), como um banco.</summary>
internal sealed class SimulatedDependencyHealthCheck(IConfiguration configuration, TelemetryCounters counters) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        counters.ReadinessExecution();
        var latency = configuration.GetValue("Exemplo:LatenciaDependenciaMs", 20);
        if (latency > 0)
            await Task.Delay(latency, cancellationToken);
        return HealthCheckResult.Healthy("Dependência simulada acessível.",
            new Dictionary<string, object> { ["latenciaMs"] = latency.ToString(CultureInfo.InvariantCulture) });
    }
}

internal static partial class OrderLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Pedido {PedidoId} consultado.")]
    public static partial void Queried(ILogger logger, int pedidoId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Pedido {PedidoId} criado com {Itens} itens.")]
    public static partial void Created(ILogger logger, int pedidoId, int itens);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Pedido recusado: {Itens} itens.")]
    public static partial void Rejected(ILogger logger, int itens);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Falha ao processar o pedido.")]
    public static partial void Failed(ILogger logger, Exception exception);
}
