using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TEC.Observability.LoadGenerator;

/// <summary>Cenário de carga: uma requisição HTTP e o critério de sucesso.</summary>
/// <param name="Name">Nome exibido no relatório.</param>
/// <param name="Weight">Peso relativo na mistura de requisições (0 desliga o cenário).</param>
/// <param name="CreateRequest">Monta a requisição (o <see cref="Random"/> é exclusivo do worker).</param>
/// <param name="ExpectedStatus">Status HTTP esperado (padrão 200).</param>
public sealed record LoadScenario(string Name, int Weight, Func<Random, HttpRequestMessage> CreateRequest, int ExpectedStatus = 200);

/// <summary>Configuração de uma execução de carga.</summary>
public sealed record LoadOptions
{
    /// <summary>Requisições em paralelo (workers).</summary>
    public int Concurrency { get; init; } = 32;

    /// <summary>Duração da medição (sem contar o aquecimento).</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Aquecimento: requisições feitas mas fora das estatísticas (JIT, pools de conexão, caches).</summary>
    public TimeSpan WarmUp { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Semente dos sorteios (cenários e dados), para execuções reproduzíveis.</summary>
    public int Seed { get; init; } = 2026;

    /// <summary>Cenários e pesos.</summary>
    public required IReadOnlyList<LoadScenario> Scenarios { get; init; }
}

/// <summary>Estatísticas de latência (milissegundos).</summary>
public sealed record LatencyStats(double Mean, double P50, double P95, double P99, double Max)
{
    internal static LatencyStats From(List<double> samples)
    {
        if (samples.Count == 0)
            return new LatencyStats(0, 0, 0, 0, 0);

        samples.Sort();
        return new LatencyStats(samples.Average(), Percentile(samples, 0.50), Percentile(samples, 0.95), Percentile(samples, 0.99), samples[^1]);
    }

    // Nearest-rank: o menor valor que cobre a fração pedida das amostras
    private static double Percentile(List<double> sorted, double fraction) =>
        sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];
}

/// <summary>Resultado de um cenário.</summary>
public sealed record ScenarioReport(string Name, long Requests, long Errors, LatencyStats Latency);

/// <summary>Resultado da execução.</summary>
public sealed record LoadReport(
    TimeSpan Duration,
    int Concurrency,
    long Requests,
    long Errors,
    LatencyStats Latency,
    IReadOnlyList<ScenarioReport> Scenarios,
    IReadOnlyDictionary<string, long> ErrorsByKind)
{
    /// <summary>Requisições por segundo na janela medida.</summary>
    public double RequestsPerSecond => Duration.TotalSeconds > 0 ? Requests / Duration.TotalSeconds : 0;

    /// <summary>Fração de requisições com erro (0 a 1).</summary>
    public double ErrorRate => Requests > 0 ? (double)Errors / Requests : 0;

    /// <summary>Relatório em texto (console, saída de teste e resumo do CI).</summary>
    public string ToText()
    {
        var ci = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine(ci, $"Duração: {Duration.TotalSeconds:F1} s · concorrência: {Concurrency} · requisições: {Requests} · RPS: {RequestsPerSecond:F0} · erros: {Errors} ({ErrorRate:P2})");
        text.AppendLine(ci, $"Latência (ms): média {Latency.Mean:F1} · p50 {Latency.P50:F1} · p95 {Latency.P95:F1} · p99 {Latency.P99:F1} · máx {Latency.Max:F1}");
        text.AppendLine("| Cenário | Requisições | Erros | p50 (ms) | p95 (ms) | p99 (ms) | máx (ms) |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var s in Scenarios)
            text.AppendLine(ci, $"| {s.Name} | {s.Requests} | {s.Errors} | {s.Latency.P50:F1} | {s.Latency.P95:F1} | {s.Latency.P99:F1} | {s.Latency.Max:F1} |");
        foreach (var (kind, count) in ErrorsByKind.OrderByDescending(e => e.Value))
            text.AppendLine(ci, $"Erro '{kind}': {count}");
        return text.ToString();
    }
}

/// <summary>
/// Gerador de carga em malha fechada: cada worker envia uma requisição, espera a resposta inteira e envia a próxima.
/// </summary>
public static class LoadRunner
{
    /// <summary>Executa a carga e devolve as estatísticas da janela medida (após o aquecimento).</summary>
    /// <param name="client">Cliente com <see cref="HttpClient.BaseAddress"/> apontando para a API.</param>
    /// <param name="options">Configuração da execução.</param>
    /// <param name="cancellationToken">Interrompe a execução (o relatório parcial é devolvido).</param>
    public static async Task<LoadReport> RunAsync(HttpClient client, LoadOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Concurrency, 1);

        var scenarios = options.Scenarios.Where(s => s.Weight > 0).ToArray();
        if (scenarios.Length == 0)
            throw new ArgumentException("Informe ao menos um cenário com peso maior que zero.", nameof(options));

        int[] cumulative = new int[scenarios.Length];
        for (int i = 0, sum = 0; i < scenarios.Length; i++)
            cumulative[i] = sum += scenarios[i].Weight;

        long start = Stopwatch.GetTimestamp();
        long measureFrom = start + (long)(options.WarmUp.TotalSeconds * Stopwatch.Frequency);
        long stopAt = measureFrom + (long)(options.Duration.TotalSeconds * Stopwatch.Frequency);

        var workers = Enumerable.Range(0, options.Concurrency)
            .Select(id => Task.Run(() => RunWorkerAsync(client, scenarios, cumulative, new Random(options.Seed + id), measureFrom, stopAt, cancellationToken)))
            .ToArray();
        var results = await Task.WhenAll(workers).ConfigureAwait(false);

        double measured = Stopwatch.GetElapsedTime(measureFrom, Math.Min(Stopwatch.GetTimestamp(), stopAt)).TotalSeconds;
        return BuildReport(scenarios, results, TimeSpan.FromSeconds(Math.Max(measured, 0)), options.Concurrency);
    }

    private static async Task<WorkerResult> RunWorkerAsync(HttpClient client, LoadScenario[] scenarios, int[] cumulative, Random random,
        long measureFrom, long stopAt, CancellationToken cancellationToken)
    {
        var result = new WorkerResult(scenarios.Length);
        while (!cancellationToken.IsCancellationRequested)
        {
            long begin = Stopwatch.GetTimestamp();
            if (begin >= stopAt)
                break;

            int index = Array.BinarySearch(cumulative, random.Next(cumulative[^1]) + 1);
            if (index < 0)
                index = ~index;
            var scenario = scenarios[index];

            string? error = null;
            try
            {
                using var request = scenario.CreateRequest(random);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode != scenario.ExpectedStatus)
                    error = $"HTTP {(int)response.StatusCode}";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                error = ex.GetType().Name;
            }

            long end = Stopwatch.GetTimestamp();
            if (begin >= measureFrom && end <= stopAt)
                result.Record(index, Stopwatch.GetElapsedTime(begin, end).TotalMilliseconds, error);
        }

        return result;
    }

    private static LoadReport BuildReport(LoadScenario[] scenarios, WorkerResult[] results, TimeSpan duration, int concurrency)
    {
        var all = new List<double>();
        var perScenario = new List<ScenarioReport>();
        for (int i = 0; i < scenarios.Length; i++)
        {
            var samples = results.SelectMany(r => r.Latencies[i]).ToList();
            all.AddRange(samples);
            perScenario.Add(new ScenarioReport(scenarios[i].Name, samples.Count, results.Sum(r => r.Errors[i]), LatencyStats.From(samples)));
        }

        var errorsByKind = results.SelectMany(r => r.ErrorsByKind)
            .GroupBy(e => e.Key)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Value));

        return new LoadReport(duration, concurrency, all.Count, perScenario.Sum(s => s.Errors), LatencyStats.From(all), perScenario, errorsByKind);
    }

    // Estado de um worker: sem compartilhamento entre threads (as listas só são lidas no fim)
    private sealed class WorkerResult(int scenarioCount)
    {
        public List<double>[] Latencies { get; } = [.. Enumerable.Range(0, scenarioCount).Select(_ => new List<double>())];
        public long[] Errors { get; } = new long[scenarioCount];
        public Dictionary<string, long> ErrorsByKind { get; } = [];

        public void Record(int scenario, double milliseconds, string? error)
        {
            Latencies[scenario].Add(milliseconds);
            if (error is null)
                return;

            Errors[scenario]++;
            ErrorsByKind[error] = ErrorsByKind.GetValueOrDefault(error) + 1;
        }
    }
}
