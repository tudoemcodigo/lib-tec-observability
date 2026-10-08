using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using TEC.Observability.Exporters;
using TEC.Observability.HealthChecks;
using TEC.Observability.Tracing;

namespace TEC.Observability.Tests.Security.Adversarial;

/// <summary>
/// Negação de serviço: entradas adversariais (cabeçalhos gigantes ou repetidos, Baggage enorme, documento de descoberta sem fim,
/// aninhado ou gotejando, query strings degeneradas, mensagens de log com milhões de quebras de linha, rajadas de sondas contra
/// uma dependência travada) precisam falhar rápido, com leitura e memória limitadas, sem derrubar a requisição.
/// </summary>
/// <remarks>
/// Os limites de tempo são folgados (máquinas de CI lentas): pegam laços sem fim e crescimento quadrático/exponencial, não
/// pequenas regressões de desempenho (essas ficam com o TEC.Observability.Benchmarks). A classe roda isolada, sem nenhum
/// outro teste em paralelo: mede tempo de parede, e centenas de testes simultâneos num runner de 2 vCPUs atrasavam a
/// rajada de 2 mil sondas além do limite sem nenhuma regressão.
/// </remarks>
[NotInParallel]
public class DosResistanceTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromSeconds(5);
    private static readonly Uri Sso = new("https://sso.exemplo.com/.well-known/openid-configuration");

    // ---------- Correlation id e Baggage ----------

    [Test]
    [Arguments(1)]
    [Arguments(10_000)]
    public async Task Huge_or_repeated_correlation_id_is_replaced_cheaply(int repetitions)
    {
        await using var app = await TestApp.StartAsync(endpoints: a => a.MapGet("/eco", () => CorrelationId.Current ?? string.Empty));
        var value = repetitions == 1 ? new string('a', 1024 * 1024) : "pedido-42";

        using var request = new HttpRequestMessage(HttpMethod.Get, "/eco");
        for (var i = 0; i < repetitions; i++)
            request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, value);

        var watch = Stopwatch.StartNew();
        using var response = await app.GetTestClient().SendAsync(request);
        var echoed = response.Headers.GetValues(CorrelationId.HeaderName).Single();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(echoed.Length).IsEqualTo(32).Because("valor gigante ou repetido (\"a,a,...\") é trocado pelo TraceId");
        await Assert.That(watch.Elapsed).IsLessThan(Fast);
    }

    [Test]
    [Arguments("None")]
    [Arguments("Otlp")]
    public async Task Huge_hostile_baggage_is_not_forwarded_to_called_services(string provider)
    {
        await using var server = new RawHttpServer();
        await using var app = await TestApp.StartAsync(
            endpoints: a => a.MapGet("/chamar", async (IHttpClientFactory http) =>
            {
                using var client = http.CreateClient();
                (await client.GetAsync($"http://127.0.0.1:{server.Port}/destino")).Dispose();
                return "ok";
            }),
            settings: [("Provider", provider), ("Otlp:Endpoint", "http://localhost:4317"), ("BaggageAllowedHosts:0", "127.0.0.1")]);

        // ~30 KB: cabe no limite padrão de cabeçalhos do Kestrel (32 KB), então chega de verdade à aplicação
        var baggage = string.Join(',', Enumerable.Range(0, 1_500).Select(i => $"item{i}=valor{i}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/chamar");
        request.Headers.Add(CorrelationId.HeaderName, "pedido-42");
        request.Headers.TryAddWithoutValidation("baggage", baggage);

        var watch = Stopwatch.StartNew();
        using var response = await app.GetTestClient().SendAsync(request);
        var outgoing = server.Requests.Single(r => r.StartsWith("GET /destino ", StringComparison.Ordinal));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        // Com None não há instrumentação do OpenTelemetry no HttpClient: o Baggage da biblioteca não é injetado (só o hostil importa)
        if (provider != "None")
            await Assert.That(outgoing).Contains("correlation.id=pedido-42");
        await Assert.That(outgoing).DoesNotContain("item0=");
        await Assert.That(outgoing.Length).IsLessThan(4 * 1024);
        await Assert.That(watch.Elapsed).IsLessThan(Fast);
    }

    // ---------- Documento de descoberta OpenID (resposta de terceiro) ----------

    [Test]
    public async Task Endless_unsized_discovery_document_stops_at_limit()
    {
        var stream = new EndlessStream("""{"issuer":"https://sso.exemplo.com","lixo":" """u8.ToArray(), "x"u8.ToArray(), 64L * 1024 * 1024);
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        await using var app = await TestApp.StartAsync(o => o.AddSsoHealthCheck("SSO", Sso, h => h.Retries = 0), handler);

        var watch = Stopwatch.StartNew();
        using var response = await app.GetTestClient().GetAsync("/health/ready");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(stream.HitHardLimit).IsFalse();
        await Assert.That(stream.BytesRead).IsLessThanOrEqualTo(HttpEndpointHealthCheck.MaxDiscoveryDocumentBytes + 128L * 1024);
        await Assert.That(watch.Elapsed).IsLessThan(Fast);
    }

    [Test]
    public async Task Deeply_nested_discovery_document_is_rejected_quickly()
    {
        const int Depth = 100_000; // 200 KB: dentro do limite de tamanho, muito além da profundidade aceita pelo leitor de JSON
        var json = new string('[', Depth) + new string(']', Depth);
        var handler = FakeHttpHandler.Status(HttpStatusCode.OK, json);
        await using var app = await TestApp.StartAsync(o => o.AddSsoHealthCheck("SSO", Sso, h => h.Retries = 0), handler);

        var watch = Stopwatch.StartNew();
        using var response = await app.GetTestClient().GetAsync("/health/ready");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(watch.Elapsed).IsLessThan(Fast);
    }

    [Test]
    public async Task Dripping_discovery_document_respects_probe_timeout()
    {
        // "Slowloris" na resposta: cabeçalhos rápidos e um byte a cada 200 ms no corpo
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DripStream(TimeSpan.FromMilliseconds(200))) });
        await using var app = await TestApp.StartAsync(o => o.AddSsoHealthCheck("SSO", Sso, h =>
        {
            h.Timeout = TimeSpan.FromSeconds(1);
            h.Retries = 0;
        }), handler);

        var watch = Stopwatch.StartNew();
        using var response = await app.GetTestClient().GetAsync("/health/ready");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(4));
    }

    // ---------- Rajada de sondas contra dependência travada ----------

    [Test]
    public async Task Probe_burst_against_hung_dependency_responds_in_time_with_one_execution()
    {
        var executions = 0;
        var never = new TaskCompletionSource<HealthCheckResult>();
        await using var app = await TestApp.StartAsync(
            o => o.HealthChecks.AddAsyncCheck("Travada", _ =>
            {
                Interlocked.Increment(ref executions);
                return never.Task; // ignora o cancelamento: nunca termina
            }, [HealthCheckTags.Ready]),
            settings: [("HealthChecks:Timeout", "00:00:01")]);
        var client = app.GetTestClient();

        var watch = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, 2_000).Select(_ => client.GetAsync("/health/ready")));
        var elapsed = watch.Elapsed;

        await Assert.That(responses.All(r => r.StatusCode == HttpStatusCode.ServiceUnavailable)).IsTrue();
        await Assert.That(executions).IsEqualTo(1);
        // Prazo global (1 s) + 500 ms de folga para abandonar a verificação + agendamento de 2 mil requisições
        await Assert.That(elapsed).IsLessThan(Fast);
        foreach (var response in responses)
            response.Dispose();
    }

    // ---------- Redação: entradas degeneradas em tempo linear ----------

    [Test]
    public async Task Redaction_of_degenerate_query_and_path_is_linear()
    {
        string[] queries =
        [
            "?" + new string('=', 4 * 1024 * 1024),
            "?" + new string('&', 4 * 1024 * 1024),
            "?" + string.Concat(Enumerable.Repeat("a=", 2 * 1024 * 1024)),
            "?" + string.Concat(Enumerable.Repeat("a=&", 1024 * 1024)),
            "?a=" + new string('x', 4 * 1024 * 1024),
        ];

        var watch = Stopwatch.StartNew();
        foreach (var query in queries)
            _ = TelemetryRedaction.RedactQuery(query);
        _ = TelemetryRedaction.RedactSecretStorePath(new string('/', 4 * 1024 * 1024));
        _ = TelemetryRedaction.RedactSecretStorePath("/secrets/" + new string('/', 4 * 1024 * 1024));

        await Assert.That(watch.Elapsed).IsLessThan(Fast);
    }

    [Test]
    public async Task Request_with_thousands_of_parameters_has_query_redacted_quickly()
    {
        var exported = new List<Activity>();
        await using var app = await TestApp.StartAsync(
            services: s => s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported)),
            endpoints: a => a.MapGet("/muitos-parametros", () => "ok"),
            settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317")]);
        // ~45 mil caracteres: abaixo do limite de tamanho do Uri no .NET 8 (65.519)
        var query = "?" + string.Join('&', Enumerable.Range(0, 3_000).Select(i => $"p{i}=segredo{i}"));

        var watch = Stopwatch.StartNew();
        using var response = await app.GetTestClient().GetAsync("/muitos-parametros" + query);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(watch.Elapsed).IsLessThan(Fast);

        var server = await WaitForSpanAsync(exported, "/muitos-parametros");
        var redacted = (string)server.GetTagItem("url.query")!;
        await Assert.That(redacted).DoesNotContain("segredo");
        await Assert.That(redacted).StartsWith("?p0=Redacted&p1=Redacted");
    }

    // ---------- Arquivo de logs ----------

    [Test]
    public async Task Log_message_with_millions_of_line_breaks_is_truncated_in_linear_time_without_forging_records()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tec-obs-dos-" + Guid.NewGuid().ToString("N"));
        try
        {
            var message = string.Concat(Enumerable.Repeat("\r\n2026-01-01 00:00:00.000 +00:00 [CRT] Forjado: x", 200_000)) + new string('\n', 1_000_000);
            var watch = Stopwatch.StartNew();
            await using (var app = await TestApp.StartAsync(settings: [("Provider", "LogFile"), ("LogFile:FolderPath", folder), ("LogFile:MaxFileSizeBytes", "0")]))
            {
                var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Dos");
#pragma warning disable CA2254 // a mensagem hostil é exatamente o que está sendo testado
                logger.LogWarning(message);
#pragma warning restore CA2254
                app.Services.GetRequiredService<LoggerProvider>().ForceFlush(30_000);
            }

            var lines = Directory.GetFiles(folder, "*.txt").SelectMany(File.ReadLines).ToArray();
            await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(20));
            await Assert.That(lines.Any(l => l.StartsWith("2026-01-01 00:00:00.000", StringComparison.Ordinal))).IsFalse();
            // A mensagem é cortada em LogFileRecordExporter.MaxMessageLength: o arquivo não cresce com o tamanho da entrada.
            var forged = lines.Count(l => l.StartsWith("    2026-01-01 00:00:00.000 +00:00 [CRT] Forjado", StringComparison.Ordinal));
            await Assert.That(forged).IsGreaterThan(0).And.IsLessThanOrEqualTo(LogFileRecordExporter.MaxMessageLength / 40);
            await Assert.That(lines.Any(l => l.Contains("[truncado]", StringComparison.Ordinal))).IsTrue();
            await Assert.That(lines.Sum(l => (long)l.Length)).IsLessThan(LogFileRecordExporter.MaxMessageLength * 2L);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<Activity> WaitForSpanAsync(List<Activity> exported, string path)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            // Cópia antes de procurar: o exportador em memória adiciona spans (de qualquer host do processo) sem lock na lista
            if (exported.ToArray().OfType<Activity>().FirstOrDefault(a => a.Kind == ActivityKind.Server && a.GetTagItem("url.path") as string == path) is { } span)
                return span;
            await Task.Delay(50);
        }

        throw new InvalidOperationException($"Span de {path} não exportado.");
    }

    /// <summary>Corpo que entrega um byte por vez, com espera entre eles (respeitando o cancelamento).</summary>
    private sealed class DripStream(TimeSpan delay) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(delay, cancellationToken);
            buffer.Span[0] = (byte)' ';
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
