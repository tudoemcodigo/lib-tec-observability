using System.Globalization;
using TEC.Observability.LoadGenerator;
using TEC.Observability.LoadTests.Infrastructure;
using TEC.Observability.SampleApi;

namespace TEC.Observability.LoadTests.Api;

/// <summary>
/// Carga HTTP na API de exemplo hospedada em processo (Kestrel real em 127.0.0.1), com o gerador TEC.Observability.LoadGenerator.
/// Além de erros e latência, confere o que a telemetria produziu sob carga: sondagens de health sem span, todo span de servidor
/// com correlation id, nenhum segredo da query string nos spans ou logs e nenhum span sem fim.
/// </summary>
/// <remarks>
/// Para medir uma API publicada em outro servidor, use o gerador pela linha de comando (veja samples/README.md).
/// </remarks>
[NotInParallel(LoadSettings.Exclusive)]
public class SampleApiLoadTests
{
    [Test]
    [Category(TestCategories.LoadCi)]
    public async Task SmokeLoad_AllScenarios_WithoutErrorsOrTelemetryLeaks()
    {
        await using var host = await SampleApiHost.StartAsync();
        using var client = host.CreateClient(16);
        var before = host.Counters.Snapshot();

        var report = await LoadRunner.RunAsync(client, new LoadOptions
        {
            Concurrency = 16,
            WarmUp = TimeSpan.FromSeconds(1),
            Duration = TimeSpan.FromSeconds(3),
            Scenarios = SampleScenarios.All,
        });
        var telemetry = (await host.SettledCountersAsync()).Minus(before);
        LoadSettings.Report("API de exemplo — fumaça (CI)", report.ToText() + Describe(telemetry));

        await Assert.That(report.Requests).IsGreaterThan(100);
        await Assert.That(report.Errors).IsEqualTo(0).Because(report.ToText());
        await Assert.That(report.Scenarios.All(s => s.Requests > 0)).IsTrue().Because("todos os cenários devem ser exercitados");
        await AssertTelemetryAsync(report, telemetry);
    }

    [Test]
    [Explicit]
    [Category(TestCategories.LoadHeavy)]
    public async Task SustainedLoad_ErrorRateLatencyAndTelemetryWithinLimits()
    {
        await using var host = await SampleApiHost.StartAsync();
        int concurrency = LoadSettings.ApiConcurrency;
        using var client = host.CreateClient(concurrency);

        long memoryBefore = MemoryProbe.RetainedBytes();
        var before = host.Counters.Snapshot();
        var report = await LoadRunner.RunAsync(client, new LoadOptions
        {
            Concurrency = concurrency,
            WarmUp = TimeSpan.FromSeconds(10),
            Duration = TimeSpan.FromSeconds(LoadSettings.ApiSeconds),
            Scenarios = SampleScenarios.All,
        });
        var telemetry = (await host.SettledCountersAsync()).Minus(before);
        long memoryAfter = MemoryProbe.RetainedBytes();

        LoadSettings.Report("API de exemplo — carga sustentada",
            report.ToText() + Describe(telemetry)
            + $"Memória retida (cliente + servidor): {MemoryProbe.Megabytes(memoryBefore)} → {MemoryProbe.Megabytes(memoryAfter)}{Environment.NewLine}");

        // Limites generosos: o objetivo é pegar regressões grosseiras (erros, travamentos, vazamento), não medir a máquina
        await Assert.That(report.ErrorRate).IsLessThanOrEqualTo(0.001).Because(report.ToText());
        await Assert.That(report.Latency.P99).IsLessThan(2_000);
        await Assert.That(memoryAfter - memoryBefore).IsLessThan(128L * 1024 * 1024);
        await AssertTelemetryAsync(report, telemetry);
    }

    [Test]
    [Explicit]
    [Category(TestCategories.LoadHeavy)]
    public async Task HealthStorm_ProbesShareExecutionsAndBusinessKeepsResponding()
    {
        // Rajada de sondas (orquestrador, balanceador, monitoração externa) disputando com o tráfego de negócio. Com o cache de 1 s
        // do exemplo, a dependência roda no máximo uma vez por segundo, qualquer que seja o número de sondas.
        await using var host = await SampleApiHost.StartAsync(settings: [("Exemplo:LatenciaDependenciaMs", "50")]);
        int concurrency = LoadSettings.HealthConcurrency;
        using var client = host.CreateClient(concurrency);
        var before = host.Counters.Snapshot();

        var report = await LoadRunner.RunAsync(client, new LoadOptions
        {
            Concurrency = concurrency,
            WarmUp = TimeSpan.FromSeconds(3),
            Duration = TimeSpan.FromSeconds(Math.Min(LoadSettings.ApiSeconds, 30)),
            Scenarios = SampleScenarios.Parse("ready:45,live:45,consultar:10"),
        });
        var telemetry = (await host.SettledCountersAsync()).Minus(before);
        double seconds = report.Duration.TotalSeconds + 3; // + aquecimento
        LoadSettings.Report($"Rajada de {concurrency} sondas nos health checks",
            report.ToText() + Describe(telemetry) + $"Execuções da dependência: {telemetry.ReadinessExecutions} em ~{seconds:F0} s{Environment.NewLine}");

        await Assert.That(report.Errors).IsEqualTo(0).Because(report.ToText());
        await Assert.That(telemetry.ReadinessExecutions).IsLessThanOrEqualTo((long)Math.Ceiling(seconds) + 5);
        await Assert.That(telemetry.HealthSpans).IsEqualTo(0);
        await Assert.That(report.Scenarios.Single(s => s.Name == "consultar").Requests).IsGreaterThan(0);
        await Assert.That(report.Latency.P99).IsLessThan(2_000);
    }

    [Test]
    [Explicit]
    [Category(TestCategories.LoadHeavy)]
    public async Task HangingDependency_ReadinessAnswers503WithinTimeoutAndBusinessIsUnaffected()
    {
        // Dependência que demora 30 s (banco travado): o readiness responde 503 no prazo global (1 s) e o tráfego de negócio segue
        await using var host = await SampleApiHost.StartAsync(settings:
        [
            ("Exemplo:LatenciaDependenciaMs", "30000"),
            ("Observability:HealthChecks:Timeout", "00:00:01"),
        ]);
        using var client = host.CreateClient(64);
        var scenarios = SampleScenarios.Parse("ready:20,consultar:60,criar:20")
            .Select(s => s.Name == "ready" ? s with { ExpectedStatus = 503 } : s)
            .ToArray();

        var report = await LoadRunner.RunAsync(client, new LoadOptions
        {
            Concurrency = 64,
            WarmUp = TimeSpan.FromSeconds(2),
            Duration = TimeSpan.FromSeconds(Math.Min(LoadSettings.ApiSeconds, 20)),
            Scenarios = scenarios,
        });
        LoadSettings.Report("Dependência travada: readiness em 503 e negócio respondendo", report.ToText());

        var ready = report.Scenarios.Single(s => s.Name == "ready");
        var business = report.Scenarios.Single(s => s.Name == "consultar");
        await Assert.That(report.Errors).IsEqualTo(0).Because(report.ToText());
        // Prazo global de 1 s + 500 ms de folga para abandonar a verificação + agendamento sob carga
        await Assert.That(ready.Latency.Max).IsLessThan(3_000);
        await Assert.That(business.Latency.P99).IsLessThan(1_000);
    }

    private static async Task AssertTelemetryAsync(LoadReport report, TelemetrySnapshot telemetry)
    {
        long business = report.Scenarios.Where(s => s.Name is not ("live" or "ready")).Sum(s => s.Requests);
        await Assert.That(telemetry.HealthSpans).IsEqualTo(0).Because("sondagens de health não geram trace");
        await Assert.That(telemetry.ServerSpansWithoutCorrelation).IsEqualTo(0).Because("todo span de servidor leva o correlation id");
        await Assert.That(telemetry.LeakedSpans).IsEqualTo(0).Because("a query string é redigida nos spans");
        await Assert.That(telemetry.LeakedLogs).IsEqualTo(0).Because("nenhum log repete o segredo da query string");
        await Assert.That(telemetry.ServerSpans).IsGreaterThanOrEqualTo(business);
    }

    private static string Describe(TelemetrySnapshot t) => string.Create(CultureInfo.InvariantCulture,
        $"Telemetria: {t.ServerSpans:N0} spans de servidor ({t.ServerSpansWithoutCorrelation} sem correlation id) · {t.BusinessSpans:N0} de negócio · " +
        $"{t.ClientSpans:N0} de saída · {t.HealthSpans} de health · {t.Logs:N0} logs ({t.LogsWithTrace:N0} com trace) · " +
        $"vazamentos: {t.LeakedSpans} spans, {t.LeakedLogs} logs · execuções do readiness: {t.ReadinessExecutions:N0}{Environment.NewLine}");
}
