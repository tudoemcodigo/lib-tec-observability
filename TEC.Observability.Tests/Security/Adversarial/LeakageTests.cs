using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using TEC.Observability.Configuration;
using TEC.Observability.DependencyInjection;
using TEC.Observability.HealthChecks;
using TEC.Observability.Tracing;

namespace TEC.Observability.Tests.Security.Adversarial;

/// <summary>
/// Vazamento de dados: um marcador secreto é injetado onde um cliente ou uma dependência consegue colocá-lo (query string,
/// cabeçalhos, URL de sondagem, mensagem de exceção de verificação, configuração) e o teste varre tudo o que sai do serviço —
/// spans exportados (tags, nome, eventos), logs exportados (mensagem, atributos, escopos, exceção), arquivo de logs, JSON e
/// cabeçalhos de health, cabeçalhos de saída e mensagens de validação — garantindo que ele nunca aparece.
/// </summary>
public class LeakageTests
{
    private static readonly string Secret = "SEGREDO" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    // ---------- Query string ----------

    [Test]
    [Arguments("Otlp")]
    [Arguments("LogFile")]
    public async Task Query_string_token_does_not_reach_spans(string provider)
    {
        var (spans, _, _) = await CallWithSecretInQueryAsync(provider);

        await Assert.That(SpansWith(spans, "/consulta-vazamento")).IsNotEmpty();
        await Assert.That(Leaks(spans)).IsEmpty();
    }

    /// <remarks>
    /// Os logs de requisição do próprio ASP.NET Core (<c>Microsoft.AspNetCore.Hosting.Diagnostics</c>, nível Information:
    /// <c>Request starting ... ?token=...</c>) levam a URL com a query string; com o nível padrão de log eles chegam aos logs
    /// exportados (OTLP, Azure, arquivo).
    /// </remarks>
    [Test]
    [Arguments("Otlp")]
    [Arguments("LogFile")]
    public async Task Query_string_token_does_not_reach_exported_logs(string provider)
    {
        var (_, logs, file) = await CallWithSecretInQueryAsync(provider);

        await Assert.That(Leaks(logs)).IsEmpty();
        await Assert.That(file).DoesNotContain(Secret);
    }

    [Test]
    public async Task Request_logs_keep_url_with_only_values_redacted()
    {
        var (_, logs, _) = await CallWithSecretInQueryAsync("Otlp");
        var messages = logs.ToArray().OfType<LogRecord>().Select(l => l.FormattedMessage ?? string.Empty).ToArray();

        await Assert.That(messages.Any(m => m.Contains("/consulta-vazamento?token=Redacted&pagina=Redacted", StringComparison.Ordinal))).IsTrue()
            .Because("o log de requisição do ASP.NET Core continua com a rota e os nomes dos parâmetros");
        // .NET 8: logger da biblioteca (api_key=Redacted); .NET 9+: o próprio HttpClient já troca a query por "*"
        await Assert.That(messages.Any(m => m.Contains("/parceiro?api_key=Redacted", StringComparison.Ordinal) || m.Contains("/parceiro?*", StringComparison.Ordinal))).IsTrue()
            .Because("o log da chamada de saída continua com a URL");
    }

    [Test]
    public async Task Url_in_service_log_is_redacted_and_other_attributes_remain()
    {
        var logs = new List<LogRecord>();
        await using var app = await TestApp.StartAsync(services: s => s.ConfigureOpenTelemetryLoggerProvider(l => l.AddInMemoryExporter(logs)),
            settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317")]);
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Servico");

        LeakageLog.Calling(logger, new Uri($"https://usuario:{Secret}@api.exemplo.com/v1/pedidos?chave={Secret}&pagina=2"),
            $"https://api.exemplo.com/v2?chave={Secret}", "texto livre ?a=1");
        app.Services.GetRequiredService<LoggerProvider>().ForceFlush(5_000);

        var record = logs.ToArray().OfType<LogRecord>().Single(l => l.CategoryName == "Servico");
        var attributes = record.Attributes!.ToDictionary(a => a.Key, a => a.Value);
        await Assert.That(attributes["Destino"]).IsEqualTo("https://api.exemplo.com/v1/pedidos?chave=Redacted&pagina=Redacted");
        await Assert.That(attributes["Alternativo"]).IsEqualTo("https://api.exemplo.com/v2?chave=Redacted");
        await Assert.That(attributes["Observacao"]).IsEqualTo("texto livre ?a=1");
        await Assert.That(record.FormattedMessage).DoesNotContain(Secret);
        await Assert.That(record.FormattedMessage).Contains("https://api.exemplo.com/v1/pedidos?chave=Redacted&pagina=Redacted");
    }

    /// <summary>Requisição com segredo na query string, que chama um parceiro com chave na query; devolve spans, logs e o arquivo de logs.</summary>
    private static async Task<(List<Activity> Spans, List<LogRecord> Logs, string File)> CallWithSecretInQueryAsync(string provider)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tec-obs-vazamento-" + Guid.NewGuid().ToString("N"));
        var spans = new List<Activity>();
        var logs = new List<LogRecord>();
        await using var outgoing = new RawHttpServer();
        try
        {
            await using (var app = await TestApp.StartAsync(
                services: s => s
                    .ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(spans))
                    .ConfigureOpenTelemetryLoggerProvider(l => l.AddInMemoryExporter(logs)),
                endpoints: a => a.MapGet("/consulta-vazamento", async (IHttpClientFactory http) =>
                {
                    using var client = http.CreateClient();
                    (await client.GetAsync($"http://127.0.0.1:{outgoing.Port}/parceiro?api_key={Secret}")).Dispose();
                    return "ok";
                }),
                settings: [("Provider", provider), ("Otlp:Endpoint", "http://localhost:4317"), ("LogFile:FolderPath", folder)]))
            {
                using var response = await app.GetTestClient().GetAsync($"/consulta-vazamento?token={Secret}&pagina=2");
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException($"HTTP {(int)response.StatusCode}");
                await WaitForAsync(() => SpansWith(spans, "/consulta-vazamento").Count > 0);
                app.Services.GetRequiredService<LoggerProvider>().ForceFlush(10_000);
            }

            var file = Directory.Exists(folder) ? string.Concat(Directory.GetFiles(folder, "*.txt").Select(File.ReadAllText)) : string.Empty;
            return (spans, logs, file);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    // ---------- Correlation id inseguro ----------

    [Test]
    public async Task Unsafe_correlation_id_with_secret_is_not_echoed_anywhere()
    {
        var spans = new List<Activity>();
        var logs = new List<LogRecord>();
        await using var app = await TestApp.StartAsync(
            services: s => s
                .ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(spans))
                .ConfigureOpenTelemetryLoggerProvider(l => l.AddInMemoryExporter(logs)),
            endpoints: a => a.MapGet("/registrar", (ILogger<LeakageTests> logger) =>
            {
                LeakageLog.Processed(logger);
                return CorrelationId.Current ?? string.Empty;
            }),
            settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317")]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/registrar");
        request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, $"<{Secret}> ou 1=1");
        using var response = await app.GetTestClient().SendAsync(request);
        await WaitForAsync(() => SpansWith(spans, "/registrar").Count > 0);

        await Assert.That(response.Headers.GetValues(CorrelationId.HeaderName).Single()).DoesNotContain(Secret);
        await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain(Secret);
        await Assert.That(Leaks(spans)).IsEmpty();
        await Assert.That(Leaks(logs)).IsEmpty();
    }

    // ---------- Health checks ----------

    [Test]
    [Arguments("Production")]
    [Arguments("Development")]
    public async Task Check_exception_with_secret_does_not_appear_in_health_json(string environment)
    {
        await using var app = await TestApp.StartAsync(
            o => o.HealthChecks
                .AddCheck("LancaExcecao", () => throw new InvalidOperationException($"Falha ao conectar com a senha {Secret}"), [HealthCheckTags.Ready])
                .AddCheck("DescricaoIgualAMensagem", () =>
                {
                    var ex = new InvalidOperationException($"Token {Secret} expirado");
                    return HealthCheckResult.Unhealthy(ex.Message, ex);
                }, [HealthCheckTags.Ready]),
            environment: environment);

        using var ready = await app.GetTestClient().GetAsync("/health/ready");
        using var live = await app.GetTestClient().GetAsync("/health/live");

        await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(await ready.Content.ReadAsStringAsync()).DoesNotContain(Secret);
        await Assert.That(await live.Content.ReadAsStringAsync()).DoesNotContain(Secret);
        await Assert.That(string.Concat(ready.Headers.Concat(ready.Content.Headers).SelectMany(h => h.Value))).DoesNotContain(Secret);
    }

    [Test]
    public async Task Probe_url_with_key_does_not_appear_in_logs_or_json_when_dependency_fails()
    {
        var logs = new CapturingLoggerProvider();
        // Porta 1 em loopback: conexão recusada de verdade, pelo SocketsHttpHandler real das sondagens
        var probe = new Uri($"http://127.0.0.1:1/status?api_key={Secret}");
        await using var app = await TestApp.StartAsync(
            o => o.AddExternalServiceHealthCheck("Parceiro", probe, h => h.Retries = 1),
            servicesBefore: s => s.AddLogging(l => l.AddProvider(logs).SetMinimumLevel(LogLevel.Trace)));

        using var response = await app.GetTestClient().GetAsync("/health/ready");
        var body = await response.Content.ReadAsStringAsync();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body).DoesNotContain(Secret);
        await Assert.That(logs.Entries.Any(e => e.Contains("Parceiro", StringComparison.Ordinal))).IsTrue().Because("a falha é registrada");
        await Assert.That(logs.Entries.Where(e => e.Contains(Secret, StringComparison.Ordinal))).IsEmpty();
    }

    // ---------- Configuração ----------

    [Test]
    [Arguments("Otlp:Endpoint", "https://usuario:{0}@coletor.exemplo.com:4318")]
    [Arguments("Otlp:Headers:x-api-key", "{0}\nquebra")]
    [Arguments("Otlp:Headers:x-api-key", "{0}")]
    [Arguments("LogFile:FileName", "../../{0}")]
    [Arguments("BaggageAllowedHosts:0", "host {0}")]
    public async Task Validation_message_never_echoes_received_value(string key, string template)
    {
        var value = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Secret);
        var services = new ServiceCollection();
        services.AddTecObservability(TestApp.Configuration(("Provider", key.StartsWith("LogFile", StringComparison.Ordinal) ? "LogFile" : "Otlp"),
            ("Otlp:Endpoint", key == "Otlp:Endpoint" ? value : "http://coletor:4318"), (key, value)));
        await using var provider = services.BuildServiceProvider();

        string message;
        try
        {
            _ = provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
            message = string.Empty;
        }
        catch (OptionsValidationException ex)
        {
            message = ex.ToString();
        }

        await Assert.That(message).IsNotEmpty().Because("a configuração com o valor hostil precisa ser recusada");
        await Assert.That(message).DoesNotContain(Secret);
    }

    // ---------- Apoio ----------

    // As listas dos exportadores em memória são lidas por cópia (ToArray): a instrumentação escuta o processo inteiro e o
    // exportador continua recebendo spans e logs de outros hosts, sem lock na lista. OfType descarta o null que a cópia pode trazer
    // (o List.Add incrementa o tamanho antes de gravar o item).
    private static List<Activity> SpansWith(List<Activity> spans, string path) =>
        [.. spans.ToArray().OfType<Activity>().Where(s => s.Kind == ActivityKind.Server && s.GetTagItem("url.path") as string == path)];

    /// <summary>Onde o segredo aparece nos spans: tags, nome de exibição, eventos e atributos de eventos.</summary>
    private static List<string> Leaks(List<Activity> spans)
    {
        var leaks = new List<string>();
        foreach (var span in spans.ToArray().OfType<Activity>())
        {
            if (span.DisplayName.Contains(Secret, StringComparison.Ordinal))
                leaks.Add($"{span.DisplayName}: nome");
            leaks.AddRange(span.TagObjects.Where(t => Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture)?.Contains(Secret, StringComparison.Ordinal) == true)
                .Select(t => $"{span.DisplayName}: {t.Key}"));
            leaks.AddRange(span.Events.Where(e => e.Name.Contains(Secret, StringComparison.Ordinal)
                || e.Tags.Any(t => Convert.ToString(t.Value, System.Globalization.CultureInfo.InvariantCulture)?.Contains(Secret, StringComparison.Ordinal) == true))
                .Select(e => $"{span.DisplayName}: evento {e.Name}"));
        }

        return leaks;
    }

    /// <summary>Onde o segredo aparece nos logs: mensagem, corpo, atributos, escopos e exceção.</summary>
    private static List<string> Leaks(List<LogRecord> logs)
    {
        var snapshot = logs.ToArray().OfType<LogRecord>();

        var leaks = new List<string>();
        foreach (var record in snapshot)
        {
            var texts = new List<string?> { record.FormattedMessage, record.Body, record.Exception?.ToString() };
            texts.AddRange(record.Attributes?.Select(a => Convert.ToString(a.Value, System.Globalization.CultureInfo.InvariantCulture)) ?? []);
            record.ForEachScope((scope, list) =>
            {
                foreach (var item in scope)
                    list.Add(Convert.ToString(item.Value, System.Globalization.CultureInfo.InvariantCulture));
            }, texts);
            if (texts.Any(t => t?.Contains(Secret, StringComparison.Ordinal) == true))
                leaks.Add($"{record.CategoryName}: {record.FormattedMessage ?? record.Body}");
        }

        return leaks;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
            await Task.Delay(50);
    }

    /// <summary>Guarda cada log (mensagem, escopos não incluídos, exceção completa) para varredura.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        public IReadOnlyCollection<string> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void Dispose() { }

        private sealed class Logger(string category, ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}

internal static partial class LeakageLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Requisição processada.")]
    public static partial void Processed(ILogger logger);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Chamando {Destino} (alternativo {Alternativo}): {Observacao}")]
    public static partial void Calling(ILogger logger, Uri destino, string alternativo, string observacao);
}
