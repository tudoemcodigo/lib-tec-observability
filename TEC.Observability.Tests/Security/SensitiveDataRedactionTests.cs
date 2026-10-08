using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using TEC.Observability.Configuration;
using TEC.Observability.DependencyInjection;
using TEC.Observability.Exporters;
using TEC.Observability.Internal;
using TEC.Observability.Tracing;

namespace TEC.Observability.Tests.Security;

/// <summary>
/// Redação de dados sensíveis além da query string: nomes de atributo sensíveis (senha, token, connection string...), dados
/// sensíveis dentro de textos livres, atributos de span, arquivo de logs (caracteres de controle, escopos, exceção), JSON de
/// health e o relógio injetável do cache de health.
/// </summary>
public class SensitiveDataRedactionTests
{
    private static readonly string Secret = "SEGREDO" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    // ---------- Nomes sensíveis ----------

    [Test]
    [Arguments("password", true)]
    [Arguments("DbPassword", true)]
    [Arguments("access_token", true)]
    [Arguments("http.request.header.authorization", true)]
    [Arguments("db.connection_string", true)]
    [Arguments("X-Api-Key", true)]
    [Arguments("client-secret", true)]
    [Arguments("pwd", true)]
    [Arguments("Set-Cookie", true)]
    [Arguments("TokenCount", false)]
    [Arguments("PasswordPolicy", false)]
    [Arguments("keyword", false)]
    [Arguments("cwd", false)]
    [Arguments("user.id", false)]
    [Arguments("", false)]
    public async Task Sensitive_attribute_names_are_detected(string key, bool sensitive) =>
        await Assert.That(SensitiveKeys.IsSensitive(key)).IsEqualTo(sensitive);

    // ---------- Texto livre ----------

    [Test]
    [Arguments("falha em https://api.x.com/v1?token=abc123 agora", "falha em https://api.x.com/v1?token=Redacted agora")]
    [Arguments("Server=db;User Id=sa;Password=p@ss;Encrypt=true", "Server=db;User Id=sa;Password=Redacted;Encrypt=true")]
    [Arguments("{\"user\":\"joao\",\"password\":\"abc\"}", "{\"user\":\"joao\",\"password\":\"Redacted\"}")]
    [Arguments("Authorization: Bearer eyJhbGciOi.abc.def", "Authorization: Bearer Redacted")]
    [Arguments("cabeçalho Basic dXNlcjpwYXNz recebido", "cabeçalho Basic Redacted recebido")]
    [Arguments("ver https://user:pw@host.com/p#access_token=xyz.", "ver https://host.com/p.")]
    [Arguments("AccountKey=abc==;EndpointSuffix=core.windows.net", "AccountKey=Redacted;EndpointSuffix=core.windows.net")]
    [Arguments("api_key=XYZ&page=2", "api_key=Redacted&page=2")]
    [Arguments("já redigido: https://h.com/x?a=Redacted e Password=Redacted", "já redigido: https://h.com/x?a=Redacted e Password=Redacted")]
    public async Task Sensitive_data_inside_text_is_redacted(string text, string expected) =>
        await Assert.That(SensitiveText.Redact(text)).IsEqualTo(expected);

    [Test]
    [Arguments("Pedido 42 criado às 10:30, total=15")]
    [Arguments("rota https://api.exemplo.com/v1/pedidos sem query")]
    [Arguments("")]
    public async Task Text_without_sensitive_data_is_returned_as_the_same_instance(string text) =>
        await Assert.That(ReferenceEquals(SensitiveText.Redact(text), text)).IsTrue();

    [Test]
    public async Task Text_redaction_is_linear_for_hostile_input()
    {
        var hostile = string.Concat(Enumerable.Repeat("password=", 200_000)) + string.Concat(Enumerable.Repeat("http://", 200_000))
            + string.Concat(Enumerable.Repeat("Bearer ", 200_000));
        var watch = Stopwatch.StartNew();

        _ = SensitiveText.Redact(hostile);

        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    // ---------- Logs exportados ----------

    [Test]
    public async Task Structured_log_with_sensitive_attribute_is_redacted_in_attributes_and_message()
    {
        var record = await LogAsync(logger =>
            logger.LogInformation("Conectando {User} com {Password} (tentativa {Attempt,3})", "joao", Secret, 3));

        var attributes = record.Attributes!.ToDictionary(a => a.Key, a => a.Value);
        await Assert.That(attributes["Password"]).IsEqualTo(SensitiveText.RedactedValue);
        await Assert.That(attributes["User"]).IsEqualTo("joao");
        await Assert.That(record.FormattedMessage).IsEqualTo("Conectando joao com Redacted (tentativa   3)");
    }

    [Test]
    public async Task Free_text_log_message_with_connection_string_and_token_is_redacted()
    {
#pragma warning disable CA2254 // mensagem montada com dado sensível: exatamente o que o teste cobre
        var record = await LogAsync(logger =>
            logger.LogWarning($"Falha ao conectar em Server=db;Password={Secret}; com Authorization: Bearer {Secret}"));
#pragma warning restore CA2254

        await Assert.That(record.FormattedMessage).DoesNotContain(Secret);
        await Assert.That(record.FormattedMessage).Contains("Password=Redacted;");
    }

    [Test]
    public async Task Text_attribute_with_embedded_url_is_redacted()
    {
        var record = await LogAsync(logger => logger.LogInformation("Detalhe: {Detail}", $"chamando https://api.exemplo.com/v1?chave={Secret} agora"));

        var attributes = record.Attributes!.ToDictionary(a => a.Key, a => a.Value);
        await Assert.That(attributes["Detail"]).IsEqualTo("chamando https://api.exemplo.com/v1?chave=Redacted agora");
        await Assert.That(record.FormattedMessage).DoesNotContain(Secret);
    }

    [Test]
    public async Task Log_template_is_rendered_like_ILogger()
    {
        KeyValuePair<string, object?>[] attributes =
        [
            new("A", "ab"), new("B", 1.5), new("C", null), new("L", new[] { 1, 2 }), new("{OriginalFormat}", "x"),
        ];

        var ok = LogRedactionProcessor.TryFormat("{A,5}|{B:0.00}|{{x}}|{C}|{L}", attributes, out var message);

        await Assert.That(ok).IsTrue();
        await Assert.That(message).IsEqualTo("   ab|1.50|{x}|(null)|1, 2");
        await Assert.That(LogRedactionProcessor.TryFormat("{Missing}", attributes, out _)).IsFalse();
    }

    [Test]
    public async Task Exported_log_exception_with_sensitive_data_is_replaced_by_a_redacted_copy()
    {
        var record = await LogAsync(logger => logger.LogError(
            new InvalidOperationException($"Falha em Server=db;Password={Secret};", new ArgumentException($"chamando https://h.com/x?token={Secret}")),
            "Falhou"));

        var exception = record.Exception;
        await Assert.That(exception).IsTypeOf<RedactedException>();
        await Assert.That(((RedactedException)exception!).OriginalTypeName).IsEqualTo(typeof(InvalidOperationException).FullName);
        await Assert.That(exception.Message).IsEqualTo("Falha em Server=db;Password=Redacted;");
        await Assert.That(exception.ToString()).StartsWith("System.InvalidOperationException");
        await Assert.That(exception.ToString()).DoesNotContain(Secret);
        await Assert.That(exception.InnerException!.Message).IsEqualTo("chamando https://h.com/x?token=Redacted");
    }

    [Test]
    public async Task Thrown_exception_stack_trace_is_redacted_and_clean_exception_is_kept()
    {
        Exception thrown;
        try
        {
            throw new InvalidOperationException($"Bearer {Secret}");
        }
        catch (InvalidOperationException ex)
        {
            thrown = ex;
        }

        var redacted = RedactedException.RedactIfNeeded(thrown);
        var clean = new InvalidOperationException("sem dado sensível");

        await Assert.That(redacted.ToString()).DoesNotContain(Secret);
        await Assert.That(redacted.StackTrace).IsNotNull();
        await Assert.That(ReferenceEquals(RedactedException.RedactIfNeeded(clean), clean)).IsTrue();
    }

    // ---------- Spans ----------

    [Test]
    public async Task Span_text_values_and_status_description_are_redacted()
    {
        using var source = new ActivitySource("teste-redacao-valor-" + Guid.NewGuid().ToString("N"));
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("processar")!;
        var clean = "pedido 42";
        activity.SetTag("db.statement.note", $"conectando com Server=x;Password={Secret}");
        activity.SetTag("pedido.descricao", clean);
        activity.SetStatus(ActivityStatusCode.Error, $"falha em https://h.com/x?token={Secret}");

        SpanRedactionProcessor.Redact(activity);

        await Assert.That(activity.GetTagItem("db.statement.note")).IsEqualTo("conectando com Server=x;Password=Redacted");
        await Assert.That(ReferenceEquals(activity.GetTagItem("pedido.descricao"), clean)).IsTrue();
        await Assert.That(activity.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(activity.StatusDescription).IsEqualTo("falha em https://h.com/x?token=Redacted");
    }

    [Test]
    public async Task Sensitive_span_attributes_and_outgoing_url_query_are_redacted()
    {
        using var source = new ActivitySource("teste-redacao-span-" + Guid.NewGuid().ToString("N"));
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        using var activity = source.StartActivity("GET", ActivityKind.Client)!;
        activity.SetTag("http.request.header.authorization", "Bearer " + Secret);
        activity.SetTag("db.connection_string", "Server=x;Password=" + Secret);
        activity.SetTag("user.id", "42");
        activity.SetTag("cache.hit", true);
        activity.SetTag("url.full", $"https://api.exemplo.com/v1?api_key={Secret}#frag");

        SpanRedactionProcessor.Redact(activity);

        await Assert.That(activity.GetTagItem("http.request.header.authorization")).IsEqualTo(SensitiveText.RedactedValue);
        await Assert.That(activity.GetTagItem("db.connection_string")).IsEqualTo(SensitiveText.RedactedValue);
        await Assert.That(activity.GetTagItem("user.id")).IsEqualTo("42");
        await Assert.That(activity.GetTagItem("cache.hit") is true).IsTrue();
        await Assert.That(activity.GetTagItem("url.full")).IsEqualTo("https://api.exemplo.com/v1?api_key=Redacted");
    }

    // ---------- Arquivo de logs ----------

    [Test]
    public async Task Log_file_escapes_control_characters_and_unicode_line_separators()
    {
        var escape = ((char)0x1B).ToString();
        var lineSeparator = ((char)0x2028).ToString();
        var lines = await LogToFileAsync(logger =>
        {
#pragma warning disable CA2254 // mensagem hostil é o que está sendo testado
            logger.LogWarning("cor " + escape + "[31mvermelha" + lineSeparator + "2026-01-01 00:00:00.000 +00:00 [CRT] FORJADO");
#pragma warning restore CA2254
        });

        await Assert.That(lines.Any(l => l.Contains(escape, StringComparison.Ordinal))).IsFalse();
        await Assert.That(lines.Any(l => l.Contains(string.Concat("\\", "u001B[31mvermelha"), StringComparison.Ordinal))).IsTrue();
        await Assert.That(lines.Any(l => l.StartsWith("2026-01-01", StringComparison.Ordinal))).IsFalse();
        await Assert.That(lines.Any(l => l.StartsWith("    2026-01-01 00:00:00.000 +00:00 [CRT] FORJADO", StringComparison.Ordinal))).IsTrue();
    }

    [Test]
    public async Task Log_file_redacts_sensitive_scope_values_and_exception_text()
    {
        var lines = await LogToFileAsync(logger =>
        {
            using (logger.BeginScope(new Dictionary<string, object> { ["ApiKey"] = Secret, ["Url"] = $"https://h.com/x?t={Secret}" }))
                logger.LogError(new InvalidOperationException($"conexão Server=db;Password={Secret};"), "Falhou");
        });

        var text = string.Join('\n', lines);
        await Assert.That(text).DoesNotContain(Secret);
        await Assert.That(text).Contains("ApiKey=Redacted");
        await Assert.That(text).Contains("Url=https://h.com/x?t=Redacted");
        await Assert.That(text).Contains("Password=Redacted;");
    }

    [Test]
    public async Task Log_file_large_batch_is_written_in_blocks_under_the_buffer_ceiling()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tec-obs-lote-" + Guid.NewGuid().ToString("N"));
        const int Records = 3_000;
        var exporter = new LogFileRecordExporter(new LogFileExporterSettings { FolderPath = folder, MaxFileSizeBytes = 0 }, "lote", TimeProvider.System);
        try
        {
            await using (var app = await TestApp.StartAsync(
                services: s => s.ConfigureOpenTelemetryLoggerProvider(l => l.AddProcessor(new BatchLogRecordExportProcessor(exporter,
                    maxQueueSize: Records * 2, scheduledDelayMilliseconds: 600_000, exporterTimeoutMilliseconds: 60_000, maxExportBatchSize: Records * 2))),
                settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317")]))
            {
                var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Lote");
                var payload = new string('x', 1_000);
                for (var i = 0; i < Records; i++)
                    logger.LogInformation("linha-grande {Index} {Payload}", i, payload);
                app.Services.GetRequiredService<LoggerProvider>().ForceFlush(60_000);
            }

            var lines = Directory.GetFiles(folder, "*.txt").SelectMany(File.ReadLines).ToArray();
            await Assert.That(lines.Count(l => l.Contains("linha-grande", StringComparison.Ordinal))).IsEqualTo(Records);
            await Assert.That(exporter.PeakBufferLength).IsLessThanOrEqualTo(LogFileRecordExporter.MaxBufferedChars + LogFileRecordExporter.MaxRecordLength
                + LogFileRecordExporter.MaxDetailLength);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    // ---------- Health ----------

    [Test]
    public async Task Health_json_hides_sensitive_data_values_and_limits_third_party_text()
    {
        await using var app = await TestApp.StartAsync(observability: o => o.HealthChecks.AddCheck("terceiro",
            () => HealthCheckResult.Degraded($"falha em https://dep.com/x?token={Secret}", data: new Dictionary<string, object>
            {
                ["password"] = Secret,
                ["info"] = new string('a', 5_000),
            }), [HealthChecks.HealthCheckTags.Ready]));

        var json = await app.GetTestClient().GetStringAsync("/health/ready");
        using var document = JsonDocument.Parse(json);
        var check = document.RootElement.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "terceiro");

        await Assert.That(json).DoesNotContain(Secret);
        await Assert.That(check.GetProperty("data").GetProperty("password").GetString()).IsEqualTo(SensitiveText.RedactedValue);
        await Assert.That(check.GetProperty("data").GetProperty("info").GetString()!.Length).IsLessThanOrEqualTo(HealthChecks.HealthCheckResponseWriter.MaxTextLength + 1);
        await Assert.That(check.GetProperty("description").GetString()).IsEqualTo("falha em https://dep.com/x?token=Redacted");
    }

    [Test]
    public async Task Health_cache_expiry_follows_the_injected_time_provider()
    {
        var time = new ManualTimeProvider();
        var executions = 0;
        await using var app = await TestApp.StartAsync(
            observability: o => o.HealthChecks.AddCheck("contador", () =>
            {
                Interlocked.Increment(ref executions);
                return HealthCheckResult.Healthy();
            }, [HealthChecks.HealthCheckTags.Ready]),
            services: s => s.AddSingleton<TimeProvider>(time),
            settings: [("HealthChecks:CacheDuration", "00:00:30")]);
        var client = app.GetTestClient();

        (await client.GetAsync("/health/ready")).Dispose();
        (await client.GetAsync("/health/ready")).Dispose();
        await Assert.That(executions).IsEqualTo(1);

        time.Advance(TimeSpan.FromSeconds(31));
        (await client.GetAsync("/health/ready")).Dispose();
        await Assert.That(executions).IsEqualTo(2);
    }

    [Test]
    public async Task Blank_http_health_check_tag_is_rejected_at_registration()
    {
        var builder = new ServiceCollection().AddTecObservability(TestApp.Configuration());

        await Assert.That(() => builder.AddExternalServiceHealthCheck("api", new Uri("https://api.exemplo.com/health"), o => o.Tags.Add(" ")))
            .Throws<ArgumentException>();
    }

    private static async Task<LogRecord> LogAsync(Action<ILogger> log)
    {
        var logs = new List<LogRecord>();
        await using var app = await TestApp.StartAsync(services: s => s.ConfigureOpenTelemetryLoggerProvider(l => l.AddInMemoryExporter(logs)),
            settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317")]);
        var category = "Redacao" + Guid.NewGuid().ToString("N");
        log(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category));
        app.Services.GetRequiredService<LoggerProvider>().ForceFlush(5_000);
        return logs.ToArray().Single(l => l.CategoryName == category);
    }

    private static async Task<string[]> LogToFileAsync(Action<ILogger> log)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tec-obs-redacao-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var app = await TestApp.StartAsync(settings: [("Provider", "LogFile"), ("LogFile:FolderPath", folder)]))
            {
                log(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Redacao"));
                app.Services.GetRequiredService<LoggerProvider>().ForceFlush(10_000);
            }

            return [.. Directory.GetFiles(folder, "*.txt").SelectMany(File.ReadLines)];
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>Relógio controlado pelo teste (hora e contador de alta resolução andam juntos).</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks = DateTimeOffset.UtcNow.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
