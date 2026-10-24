using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using TEC.Observability.Configuration;
using TEC.Observability.DependencyInjection;
using TEC.Observability.HealthChecks;

namespace TEC.Observability.Benchmarks;

/// <summary>JSON dos endpoints de health e resposta reaproveitada pelo cache (o caminho de toda sondagem dentro da janela).</summary>
[MemoryDiagnoser]
public class HealthBenchmarks
{
    private static readonly DateTimeOffset Timestamp = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    private HealthReport _report = null!;
    private ObservabilityOptions _withDetails = null!;
    private ObservabilityOptions _withoutDetails = null!;
    private IHost _host = null!;
    private HealthReportCache _cache = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var entries = Enumerable.Range(0, 10).ToDictionary(
            i => $"Dependencia{i}",
            i => new HealthReportEntry(i == 3 ? HealthStatus.Degraded : HealthStatus.Healthy, "Dependência acessível.", TimeSpan.FromMilliseconds(12.5 + i),
                i == 7 ? new TimeoutException("Tempo limite excedido.") : null,
                new Dictionary<string, object> { ["statusCode"] = 200, ["latencia"] = TimeSpan.FromMilliseconds(i) },
                ["ready", "http"]));
        _report = new HealthReport(entries, HealthStatus.Degraded, TimeSpan.FromMilliseconds(42));
        _withDetails = new ObservabilityOptions { ServiceName = "pedidos-api", ServiceVersion = "1.4.2", Environment = "producao" };
        _withDetails.HealthChecks.ExposeDetails = true;
        _withoutDetails = new ObservabilityOptions { ServiceName = "pedidos-api", ServiceVersion = "1.4.2", Environment = "producao" };
        _withoutDetails.HealthChecks.ExposeDetails = false;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Production });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:ServiceName"] = "pedidos-api",
            ["Observability:ServiceVersion"] = "1.4.2",
            ["Observability:Environment"] = "producao",
            ["Observability:HealthChecks:CacheDuration"] = "00:01:00",
        });
        builder.Services.AddTecObservability(builder.Configuration);
        _host = builder.Build();
        await _host.StartAsync();
        _cache = _host.Services.GetRequiredService<HealthReportCache>();
        await _cache.GetAsync(HealthCheckTags.Live, CancellationToken.None);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Benchmark]
    public byte[] Serialize_WithDetails_10Checks() => HealthCheckResponseWriter.Serialize(_report, _withDetails, Timestamp);

    [Benchmark]
    public byte[] Serialize_WithoutDetails() => HealthCheckResponseWriter.Serialize(_report, _withoutDetails, Timestamp);

    /// <summary>Sondagem dentro da janela de cache: devolve o relatório já pronto, sem executar verificação.</summary>
    [Benchmark]
    public async Task<HealthStatus> Cache_Hit() => (await _cache.GetAsync(HealthCheckTags.Live, CancellationToken.None)).Report.Status;
}
