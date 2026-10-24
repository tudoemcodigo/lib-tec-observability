using System.Diagnostics;
using System.Diagnostics.Metrics;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Observability.SampleApi;

namespace TEC.Observability.Benchmarks;

/// <summary>
/// Custo da biblioteca por requisição: o mesmo endpoint de negócio (span, métrica e log) servido em memória (TestServer, sem
/// sockets) sem o TEC.Observability, com <c>Provider: None</c>, com o pipeline completo contando a telemetria (<c>Contagem</c>,
/// sem rede) e gravando os logs em arquivo (<c>LogFile</c>). A diferença para <c>SemObservabilidade</c> é o que a biblioteca
/// acrescenta; cada valor do parâmetro roda num processo separado (a instrumentação escuta o processo inteiro).
/// </summary>
[MemoryDiagnoser]
public class PipelineBenchmarks
{
    [Params("SemObservabilidade", "None", "Contagem", "LogFile")]
    public string Provider { get; set; } = "None";

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _folder = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "tec-obs-bench-" + Guid.NewGuid().ToString("N"));
        _app = Provider == "SemObservabilidade" ? CreatePlainApp() : SampleApiApp.Create([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration["Observability:Provider"] = Provider;
            builder.Configuration["Observability:LogFile:FolderPath"] = _folder;
            // Janela longa: o benchmark de readiness mede a resposta reaproveitada, não a dependência simulada (20 ms) a cada segundo
            builder.Configuration["Observability:HealthChecks:CacheDuration"] = "00:01:00";
        });
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Benchmark]
    public async Task<int> BusinessRequest()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/pedidos/42");
        request.Headers.Add("X-Correlation-ID", "pedido-42");
        using var response = await _client.SendAsync(request);
        return (int)response.StatusCode;
    }

    [Benchmark]
    public async Task<int> RequestWithQueryString()
    {
        using var response = await _client.GetAsync("/busca?token=segredo-123&q=pedido&pagina=2");
        return (int)response.StatusCode;
    }

    /// <summary>Readiness dentro da janela de cache (sem o TEC.Observability: o endpoint nativo do ASP.NET Core, sem verificações).</summary>
    [Benchmark]
    public async Task<int> ReadinessProbe()
    {
        using var response = await _client.GetAsync("/health/ready");
        return (int)response.StatusCode;
    }

    /// <summary>O mesmo endpoint de negócio da API de exemplo, sem a biblioteca: span, métrica e log nativos do .NET.</summary>
    private static WebApplication CreatePlainApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddHealthChecks();
        var app = builder.Build();

        var source = new ActivitySource(SampleApiApp.ServiceName);
        var meter = new Meter(SampleApiApp.ServiceName);
        var consulted = meter.CreateCounter<long>("pedidos.consultados");
        app.MapGet("/pedidos/{id:int}", (int id, ILogger<PipelineBenchmarks> logger) =>
        {
            using var activity = source.StartActivity("ConsultarPedido");
            activity?.SetTag("pedido.id", id);
            consulted.Add(1);
            BenchmarkLog.Queried(logger, id);
            return Results.Ok(new OrderResponse(id, null, Activity.Current?.TraceId.ToString()));
        });
        app.MapGet("/busca", (string? q) => Results.Ok(new { resultados = (q ?? string.Empty).Length }));
        app.MapHealthChecks("/health/ready");
        return app;
    }
}

/// <summary>
/// Custo de registrar um log na thread da aplicação: com <c>None</c> (sem pipeline de logs do OpenTelemetry nem outro provedor:
/// o piso do <c>ILogger</c>), com o pipeline contando (<c>Contagem</c>, inclusive a redação de URLs) e com o arquivo (<c>LogFile</c>,
/// gravação em lote fora da thread que registra).
/// </summary>
[MemoryDiagnoser]
public class LoggingBenchmarks
{
    [Params("None", "Contagem", "LogFile")]
    public string Provider { get; set; } = "None";

    private WebApplication _app = null!;
    private ILogger _logger = null!;
    private string _folder = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _folder = Path.Combine(Path.GetTempPath(), "tec-obs-bench-" + Guid.NewGuid().ToString("N"));
        _app = SampleApiApp.Create([], builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration["Observability:Provider"] = Provider;
            builder.Configuration["Observability:LogFile:FolderPath"] = _folder;
        });
        await _app.StartAsync();
        _logger = _app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Benchmark.Pedidos");
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    [Benchmark]
    public void LogInformation() => BenchmarkLog.Queried(_logger, 42);

    [Benchmark]
    public void LogInformation_WithScope()
    {
        using (_logger.BeginScope(new Dictionary<string, object> { ["correlation.id"] = "pedido-42" }))
            BenchmarkLog.Queried(_logger, 42);
    }
}

internal static partial class BenchmarkLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Pedido {PedidoId} consultado.")]
    public static partial void Queried(ILogger logger, int pedidoId);
}
