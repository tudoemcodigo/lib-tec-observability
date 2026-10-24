using System.Diagnostics;
using System.Globalization;
using System.Text;
using TEC.Observability.LoadGenerator;
using TEC.Observability.LoadTests.Infrastructure;

namespace TEC.Observability.LoadTests.Soak;

/// <summary>
/// Soak: a mistura completa de requisições na API de exemplo por minutos, verificando que memória, handles e vazão ficam
/// estáveis (sem vazamento de <c>Activity</c>, Baggage, escopos de log, conexões ou timers dos health checks) e que nenhum
/// segredo chega à telemetria.
/// Duração em TEC_CARGA_SOAK_SEGUNDOS (padrão 120 s).
/// </summary>
[Explicit]
[Category(TestCategories.LoadHeavy)]
[NotInParallel(LoadSettings.Exclusive)]
public class SoakTests
{
    private sealed record Sample(double Seconds, long Requests, long Errors, double Rps, long RetainedBytes, int Handles, long Spans);

    [Test]
    public async Task MixedWorkload_MemoryHandlesAndThroughputStayStable()
    {
        var duration = TimeSpan.FromSeconds(LoadSettings.SoakSeconds);
        var slice = TimeSpan.FromSeconds(Math.Clamp(LoadSettings.SoakSeconds / 24.0, 2, 30));
        int concurrency = Math.Max(Environment.ProcessorCount, 16);

        await using var host = await SampleApiHost.StartAsync();
        using var client = host.CreateClient(concurrency);

        // Aquecimento fora das amostras: JIT, pools de conexão, primeiras execuções dos health checks
        await LoadRunner.RunAsync(client, new LoadOptions { Concurrency = concurrency, WarmUp = TimeSpan.Zero, Duration = TimeSpan.FromSeconds(3), Scenarios = SampleScenarios.All });

        var samples = new List<Sample>();
        var watch = Stopwatch.StartNew();
        long requests = 0, errors = 0;
        int seed = 1;
        while (watch.Elapsed < duration)
        {
            var report = await LoadRunner.RunAsync(client, new LoadOptions
            {
                Concurrency = concurrency,
                WarmUp = TimeSpan.Zero,
                Duration = slice,
                Seed = seed++ * 1_000,
                Scenarios = SampleScenarios.All,
            });
            requests += report.Requests;
            errors += report.Errors;
            var counters = host.Counters.Snapshot();
            samples.Add(new Sample(watch.Elapsed.TotalSeconds, requests, errors, report.RequestsPerSecond, MemoryProbe.RetainedBytes(),
                MemoryProbe.HandleCount(), counters.SpansEnded));
        }

        var final = await host.SettledCountersAsync();
        await Assert.That(samples.Count).IsGreaterThanOrEqualTo(8).Because("o soak precisa de amostras suficientes (aumente TEC_CARGA_SOAK_SEGUNDOS)");

        // Descarta o primeiro quarto (aquecimento) e compara o início com o fim da janela estável
        int skip = samples.Count / 4;
        int third = Math.Max((samples.Count - skip) / 3, 1);
        var first = samples.Skip(skip).Take(third).ToList();
        var last = samples.TakeLast(third).ToList();
        double firstThroughput = first.Average(s => s.Rps);
        double lastThroughput = last.Average(s => s.Rps);
        long firstMemory = Median(first.Select(s => s.RetainedBytes));
        long lastMemory = Median(last.Select(s => s.RetainedBytes));

        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"Duração: {duration.TotalSeconds:F0} s · conexões: {concurrency} · requisições: {requests:N0} · erros: {errors:N0}");
        text.AppendLine("| t (s) | requisições | RPS | memória retida | handles | spans exportados |");
        text.AppendLine("|---:|---:|---:|---:|---:|---:|");
        foreach (var s in samples)
            text.AppendLine(CultureInfo.InvariantCulture, $"| {s.Seconds:F0} | {s.Requests:N0} | {s.Rps:N0} | {MemoryProbe.Megabytes(s.RetainedBytes)} | {s.Handles} | {s.Spans:N0} |");
        text.AppendLine(CultureInfo.InvariantCulture, $"Vazão: {firstThroughput:N0} → {lastThroughput:N0} req/s · memória (mediana): {MemoryProbe.Megabytes(firstMemory)} → {MemoryProbe.Megabytes(lastMemory)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Spans exportados: {final.SpansEnded:N0} · vazamentos: {final.LeakedSpans} spans, {final.LeakedLogs} logs");
        LoadSettings.Report("Soak na API de exemplo (carga mista)", text.ToString());

        await Assert.That(errors).IsEqualTo(0).Because(text.ToString());
        await Assert.That(lastMemory - firstMemory).IsLessThan(Math.Max(16L * 1024 * 1024, firstMemory / 5)).Because(text.ToString());
        await Assert.That(last[^1].Handles - first[0].Handles).IsLessThan(200).Because(text.ToString());
        await Assert.That(lastThroughput).IsGreaterThan(firstThroughput * 0.6).Because(text.ToString());
        await Assert.That(final.LeakedSpans + final.LeakedLogs).IsEqualTo(0);
    }

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }
}
