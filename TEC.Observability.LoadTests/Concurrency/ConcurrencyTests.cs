using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using TEC.Observability.LoadTests.Infrastructure;
using TEC.Observability.SampleApi;
using TEC.Observability.Tracing;

namespace TEC.Observability.LoadTests.Concurrency;

/// <summary>
/// O estado por requisição da biblioteca (correlation id, Baggage, política de hosts) vive em <see cref="AsyncLocal{T}"/> e o
/// estado compartilhado (cache de health, arquivo de logs) é disputado por muitas threads: cada teste dispara requisições
/// simultâneas e confere que nenhuma recebe o contexto de outra e que nada se perde ou se mistura.
/// </summary>
[Category(TestCategories.LoadCi)]
[NotInParallel(LoadSettings.Exclusive)]
public partial class ConcurrencyTests
{
    private const int Workers = 64;

    // Registra a primeira divergência (para a mensagem) e conta todas
    private sealed class Divergences
    {
        private readonly ConcurrentQueue<string> _samples = new();
        private int _count;

        public int Count => _count;

        public void Add(string description)
        {
            if (Interlocked.Increment(ref _count) <= 5)
                _samples.Enqueue(description);
        }

        public override string ToString() => string.Join(Environment.NewLine, _samples);
    }

    [Test]
    public async Task CorrelationId_ConcurrentRequests_NeverLeakBetweenRequests()
    {
        await using var host = await SampleApiHost.StartAsync();
        using var client = host.CreateClient(Workers);

        var divergences = new Divergences();
        await Parallel.ForAsync(0, 4_000, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            var correlationId = $"req-{i}";
            bool external = i % 4 == 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, external ? $"/pedidos/{i}/externo" : $"/pedidos/{i}");
            request.Headers.Add(CorrelationId.HeaderName, correlationId);
            // Baggage enviado pelo cliente: com AllowInboundBaggage desligado (padrão) não pode seguir para o serviço chamado
            if (i % 3 == 0)
                request.Headers.Add("baggage", $"usuario.id=intruso-{i},correlation.id=forjado-{i}");

            using var response = await client.SendAsync(request, ct);
            var echoed = response.Headers.TryGetValues(CorrelationId.HeaderName, out var values) ? values.Single() : null;
            if (response.StatusCode != HttpStatusCode.OK || echoed != correlationId)
            {
                divergences.Add($"{i}: HTTP {(int)response.StatusCode}, cabeçalho '{echoed}'");
                return;
            }

            if (external)
            {
                var body = await response.Content.ReadFromJsonAsync<ExternalResponse>(ct);
                var baggage = body?.Echo?.Baggage ?? string.Empty;
                if (body?.CorrelationId != correlationId || !baggage.Contains($"correlation.id={correlationId}", StringComparison.Ordinal)
                    || baggage.Contains("intruso", StringComparison.Ordinal) || string.IsNullOrEmpty(body.Echo?.Traceparent))
                {
                    divergences.Add($"{i}: correlation '{body?.CorrelationId}', baggage '{baggage}', traceparent '{body?.Echo?.Traceparent}'");
                }
            }
            else
            {
                var body = await response.Content.ReadFromJsonAsync<OrderResponse>(ct);
                if (body?.CorrelationId != correlationId || body.Id != i)
                    divergences.Add($"{i}: corpo {body}");
            }
        });

        await Assert.That(divergences.Count).IsEqualTo(0).Because(divergences.ToString());
    }

    [Test]
    public async Task Tracing_ConcurrentRequests_EveryServerSpanCarriesItsOwnCorrelationId()
    {
        const int Requests = 3_000;
        var exported = new List<Activity>();
        await using var host = await SampleApiHost.StartAsync(builder =>
            builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddInMemoryExporter(exported)));
        using var client = host.CreateClient(Workers);

        await Parallel.ForAsync(0, Requests, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (i, ct) =>
        {
            // Cada requisição como a de um cliente independente: sem o Activity do próprio teste (o TUnit cria um por teste),
            // que viraria o pai de todas e colocaria as 3 mil no mesmo trace
            Activity.Current = null;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/pedidos/{i}");
            request.Headers.Add(CorrelationId.HeaderName, $"span-{i}");
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
        });
        await host.SettledCountersAsync();

        // Cópia com ToArray: o exportador em memória adiciona spans em outra thread sem lock na lista.
        var spans = exported.ToArray().OfType<Activity>().ToArray();

        var servers = spans.Where(s => s.Kind == ActivityKind.Server).ToArray();
        var business = spans.Where(s => s.DisplayName == "ConsultarPedido").ToDictionary(s => s.ParentSpanId);
        var divergences = new Divergences();
        foreach (var server in servers)
        {
            var path = server.GetTagItem("url.path") as string ?? string.Empty;
            var expected = "span-" + path[(path.LastIndexOf('/') + 1)..];
            if (server.GetTagItem(CorrelationId.AttributeName) as string != expected)
                divergences.Add($"{path}: correlation.id '{server.GetTagItem(CorrelationId.AttributeName)}'");
            if (!business.TryGetValue(server.SpanId, out var child) || child.TraceId != server.TraceId
                || !Equals(child.GetTagItem("pedido.id"), int.Parse(path[(path.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture)))
            {
                divergences.Add($"{path}: span de negócio ausente ou em outro trace");
            }
        }

        await Assert.That(servers.Length).IsEqualTo(Requests);
        await Assert.That(servers.Select(s => s.TraceId).Distinct().Count()).IsEqualTo(Requests);
        await Assert.That(divergences.Count).IsEqualTo(0).Because(divergences.ToString());
    }

    [Test]
    public async Task HealthReadiness_ConcurrentProbes_ShareExecutions()
    {
        // Sem cache (CacheDuration 0) e com a dependência levando 200 ms: só a execução única segura a rajada
        const int LatencyMs = 200;
        await using var host = await SampleApiHost.StartAsync(settings:
        [
            ("Observability:HealthChecks:CacheDuration", "00:00:00"),
            ("Exemplo:LatenciaDependenciaMs", LatencyMs.ToString(CultureInfo.InvariantCulture)),
        ]);
        using var client = host.CreateClient(Workers);
        var before = host.Counters.Snapshot();

        long requests = 0, failures = 0;
        var watch = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Task.WhenAll(Enumerable.Range(0, Workers).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var response = await client.GetAsync("/health/ready");
                Interlocked.Increment(ref requests);
                if (response.StatusCode != HttpStatusCode.OK)
                    Interlocked.Increment(ref failures);
            }
        })));
        var elapsed = watch.Elapsed;

        var executions = host.Counters.Snapshot().Minus(before).ReadinessExecutions;
        var maxExecutions = (long)(elapsed.TotalMilliseconds / LatencyMs) + 3;
        LoadSettings.Report("Rajada no readiness sem cache",
            $"{requests} requisições de {Workers} sondas em {elapsed.TotalSeconds:F1} s · {executions} execuções da dependência (máximo esperado {maxExecutions}){Environment.NewLine}");

        await Assert.That(failures).IsEqualTo(0);
        await Assert.That(executions).IsLessThanOrEqualTo(maxExecutions);
        await Assert.That(requests).IsGreaterThan(executions * 10);
    }

    [Test]
    public async Task LogFile_ConcurrentLogging_NeverBlocksAndWritesWellFormedLines()
    {
        const int PerWorker = 2_000;
        var folder = Path.Combine(Path.GetTempPath(), "tec-obs-carga-" + Guid.NewGuid().ToString("N"));
        try
        {
            TimeSpan logging;
            await using (var host = await SampleApiHost.StartAsync(settings:
            [
                ("Observability:Provider", "LogFile"),
                ("Observability:LogFile:FolderPath", folder),
                ("Observability:LogFile:RollingInterval", "None"),
                ("Observability:LogFile:MaxFileSizeBytes", "262144"),
                ("Observability:LogFile:RetainedFileCount", "0"),
            ]))
            {
                var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Carga.Concorrencia");
                var watch = Stopwatch.StartNew();
                await Parallel.ForAsync(0, Workers, new ParallelOptions { MaxDegreeOfParallelism = Workers }, (worker, _) =>
                {
                    for (int n = 0; n < PerWorker; n++)
                    {
                        // A cada 10 registros, uma mensagem que tenta forjar uma linha de log nova
                        if (n % 10 == 0)
                            LoadLog.Forged(logger, worker, n, "\r\n2026-01-01 00:00:00.000 +00:00 [CRT] Forjado: linha falsa");
                        else
                            LoadLog.Record(logger, worker, n);
                    }
                    return ValueTask.CompletedTask;
                });
                logging = watch.Elapsed;
                host.Services.GetRequiredService<LoggerProvider>().ForceFlush(10_000);
            }

            var files = Directory.GetFiles(folder, "*.txt");
            var lines = files.SelectMany(File.ReadLines).ToArray();
            var records = lines.Where(l => !l.StartsWith("    ", StringComparison.Ordinal)).ToArray();
            var malformed = records.Where(l => !RecordLine().IsMatch(l)).Take(3).ToArray();
            var ids = records.Select(l => RecordId().Match(l)).Where(m => m.Success).Select(m => m.Value).ToArray();

            LoadSettings.Report("Arquivo de logs com 64 threads registrando ao mesmo tempo",
                $"{Workers * PerWorker:N0} registros emitidos em {logging.TotalMilliseconds:F0} ms · {ids.Length:N0} gravados " +
                $"({Workers * PerWorker - ids.Length:N0} descartados pela fila do processador em lote) · {files.Length} arquivos{Environment.NewLine}");

            // Registrar log nunca espera o disco: a fila do processador em lote descarta o excedente em vez de bloquear
            await Assert.That(logging).IsLessThan(TimeSpan.FromSeconds(10));
            await Assert.That(ids.Length).IsGreaterThan(0);
            await Assert.That(malformed).IsEmpty().Because(string.Join(Environment.NewLine, malformed));
            await Assert.That(ids.Distinct().Count()).IsEqualTo(ids.Length).Because("nenhum registro pode ser gravado duas vezes");
            await Assert.That(lines.Any(l => l.StartsWith("2026-01-01 00:00:00.000", StringComparison.Ordinal))).IsFalse()
                .Because("quebra de linha na mensagem não pode forjar um registro");
            await Assert.That(files.All(f => new FileInfo(f).Length <= 262_144)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} [+-]\d{2}:\d{2} \[(TRC|DBG|INF|WRN|ERR|CRT)\] \S+")]
    private static partial Regex RecordLine();

    [GeneratedRegex(@"registro w\d+-n\d+")]
    private static partial Regex RecordId();
}

internal static partial class LoadLog
{
    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "registro w{Worker}-n{Numero}")]
    public static partial void Record(ILogger logger, int worker, int numero);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning, Message = "registro w{Worker}-n{Numero} com texto do usuário: {Texto}")]
    public static partial void Forged(ILogger logger, int worker, int numero, string texto);
}
