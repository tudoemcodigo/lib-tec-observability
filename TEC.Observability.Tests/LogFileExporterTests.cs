using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using TEC.Observability.Configuration;
using TEC.Observability.DependencyInjection;
using TEC.Observability.Exporters;

namespace TEC.Observability.Tests;

/// <summary>Exportador <c>LogFile</c> (logs em .txt) e combinação de vários destinos no <c>Provider</c>.</summary>
public class LogFileExporterTests
{
    /// <summary>Relógio controlado pelo teste, no fuso UTC (horário do arquivo previsível).</summary>
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Pasta temporária apagada no fim do teste.</summary>
    private sealed class TempDirectory : IDisposable
    {
        public string FullPath { get; } = Path.Combine(Path.GetTempPath(), "tec-obs-" + Guid.NewGuid().ToString("N"));

        public string[] Files() =>
            Directory.Exists(FullPath) ? Directory.GetFiles(FullPath).Select(f => Path.GetFileName(f)).Order().ToArray() : [];

        public string Read(string fileName) => File.ReadAllText(Path.Combine(FullPath, fileName));

        public void Dispose()
        {
            if (Directory.Exists(FullPath))
                Directory.Delete(FullPath, recursive: true);
        }
    }

    private static readonly DateTimeOffset Today = new(2026, 10, 5, 14, 3, 22, TimeSpan.Zero);

    private static ServiceProvider Build(TempDirectory directory, TimeProvider? time = null, Action<ObservabilityBuilder>? observability = null,
        params (string Key, string? Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton(time ?? new ManualTime(Today));
        var builder = services.AddTecObservability(TestApp.Configuration(
            [("Provider", "LogFile"), ("ServiceName", "pedidos-api"), ("LogFile:FolderPath", directory.FullPath), .. settings]));
        observability?.Invoke(builder);
        return services.BuildServiceProvider();
    }

    private static void Flush(IServiceProvider provider) => provider.GetRequiredService<LoggerProvider>().ForceFlush(5_000);

    [Test]
    public async Task Each_log_becomes_a_line_with_date_level_category_trace_and_scopes()
    {
        using var directory = new TempDirectory();
        using var source = new ActivitySource("pedidos-api");
        string traceId;
        await using (var provider = Build(directory))
        {
            _ = provider.GetRequiredService<TracerProvider>();
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Pedidos.Servico");
            using (var activity = source.StartActivity("Criar"))
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = "pedido-42" }))
            {
                traceId = activity!.TraceId.ToHexString();
                logger.LogInformation(new EventId(1001), "Pedido {Id} criado", 42);
            }
        }

        await Assert.That(directory.Files()).IsEquivalentTo(["pedidos-api-20261005.txt"]);
        var content = directory.Read("pedidos-api-20261005.txt");
        // Horário do registro (relógio real), no fuso do TimeProvider.
        await Assert.That(System.Text.RegularExpressions.Regex.IsMatch(content, @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \+00:00 ")).IsTrue();
        await Assert.That(content).Contains(" [INF] Pedidos.Servico[1001]: Pedido 42 criado | trace_id=" + traceId);
        await Assert.That(content).Contains("| CorrelationId=pedido-42");
        await Assert.That(content).EndsWith("\n");
    }

    [Test]
    public async Task Line_breaks_and_exception_are_indented_and_do_not_forge_another_record()
    {
        using var directory = new TempDirectory();
        await using (var provider = Build(directory))
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Teste");
            using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = "c-1" }))
                logger.LogError(new InvalidOperationException("falhou"), "Entrada: {Valor}", "a\r\n2026-01-01 00:00:00.000 +00:00 [CRT] forjado\n\nfim");
        }

        var lines = directory.Read("pedidos-api-20261005.txt").Split('\n');
        await Assert.That(lines[0]).Contains("[ERR] Teste: Entrada: a");
        await Assert.That(lines[0]).EndsWith("| CorrelationId=c-1");
        await Assert.That(lines[1]).IsEqualTo("    2026-01-01 00:00:00.000 +00:00 [CRT] forjado");
        await Assert.That(lines[2]).IsEqualTo("    ");
        await Assert.That(lines[3]).IsEqualTo("    fim");
        await Assert.That(lines[4]).StartsWith("    System.InvalidOperationException: falhou");
        await Assert.That(lines.Count(l => l.Length > 0 && !l.StartsWith(' '))).IsEqualTo(1);
    }

    [Test]
    public async Task File_minimum_level_filters_records()
    {
        using var directory = new TempDirectory();
        await using (var provider = Build(directory, settings: ("LogFile:MinimumLevel", "Warning")))
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Teste");
            logger.LogInformation("informativo");
            logger.LogWarning("aviso");
        }

        var content = directory.Read("pedidos-api-20261005.txt");
        await Assert.That(content).DoesNotContain("informativo");
        await Assert.That(content).Contains("[WRN] Teste: aviso");
    }

    [Test]
    public async Task Full_file_continues_in_new_numbered_file()
    {
        using var directory = new TempDirectory();
        await using (var provider = Build(directory, settings: [("LogFile:MaxFileSizeBytes", "1024"), ("LogFile:IncludeScopes", "false")]))
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Teste");
            for (var i = 0; i < 3; i++)
            {
                logger.LogInformation("{Texto}", new string('x', 700));
                Flush(provider);
            }
        }

        await Assert.That(directory.Files())
            .IsEquivalentTo(["pedidos-api-20261005.txt", "pedidos-api-20261005_001.txt", "pedidos-api-20261005_002.txt"]);
    }

    [Test]
    public async Task Day_change_opens_another_file()
    {
        using var directory = new TempDirectory();
        var time = new ManualTime(Today);
        await using (var provider = Build(directory, time))
        {
            var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger("Teste");
            logger.LogInformation("hoje");
            Flush(provider);
            time.Now = Today.AddDays(1);
            logger.LogInformation("amanha");
        }

        await Assert.That(directory.Files()).IsEquivalentTo(["pedidos-api-20261005.txt", "pedidos-api-20261006.txt"]);
    }

    [Test]
    public async Task Retention_deletes_oldest_service_files_and_keeps_other_service_files()
    {
        using var directory = new TempDirectory();
        Directory.CreateDirectory(directory.FullPath);
        string[] existing = ["pedidos-api-20261001.txt", "pedidos-api-20261002.txt", "pedidos-api-20261003_001.txt", "pedidos-api-v2-20261001.txt", "outro.txt"];
        for (var i = 0; i < existing.Length; i++)
        {
            var path = Path.Combine(directory.FullPath, existing[i]);
            File.WriteAllText(path, "antigo");
            File.SetLastWriteTimeUtc(path, Today.UtcDateTime.AddDays(-10 + i));
        }

        await using (var provider = Build(directory, settings: ("LogFile:RetainedFileCount", "2")))
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("Teste").LogInformation("novo");

        await Assert.That(directory.Files())
            .IsEquivalentTo(["outro.txt", "pedidos-api-20261003_001.txt", "pedidos-api-20261005.txt", "pedidos-api-v2-20261001.txt"]);
    }

    [Test]
    [Arguments("pedidos-api.txt", true)]
    [Arguments("pedidos-api_001.txt", true)]
    [Arguments("pedidos-api-20261005.txt", true)]
    [Arguments("pedidos-api-2026100514_003.txt", true)]
    [Arguments("pedidos-api-v2-20261005.txt", false)]
    [Arguments("pedidos-api-20261005.txt.bak", false)]
    [Arguments("pedidos-api-.txt", false)]
    public async Task Retention_only_considers_files_with_own_name(string fileName, bool own)
    {
        var exporter = new LogFileRecordExporter(new LogFileExporterSettings(), "pedidos-api", TimeProvider.System);

        await Assert.That(exporter.IsOwnFile(fileName)).IsEqualTo(own);
    }

    [Test]
    [Arguments("pedidos/api", "pedidos_api")]
    [Arguments("..", "log")]
    [Arguments(" servico* ", "servico_")]
    public async Task ServiceName_becomes_safe_file_name(string serviceName, string expected) =>
        await Assert.That(LogFileRecordExporter.SafeFileName(serviceName)).IsEqualTo(expected);

    [Test]
    [Arguments("LogFile:FileName", "../fora")]
    [Arguments("LogFile:FileName", "..")]
    [Arguments("LogFile:MaxFileSizeBytes", "10")]
    [Arguments("LogFile:RetainedFileCount", "-1")]
    [Arguments("LogFile:FolderPath", " ")]
    public async Task Invalid_file_options_are_rejected(string key, string value)
    {
        using var directory = new TempDirectory();
        await using var provider = Build(directory, settings: (key, value));

        await Assert.That(() => provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value).Throws<OptionsValidationException>();
    }

    // ---------- Vários destinos ----------

    [Test]
    [Arguments("Console, LogFile")]
    [Arguments("console;logfile")]
    [Arguments(" LogFile ,Console, logfile ")]
    public async Task Multiple_destinations_are_enabled_together(string provider)
    {
        using var directory = new TempDirectory();
        await using var serviceProvider = Build(directory, settings: ("Provider", provider));

        var options = serviceProvider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
        var active = serviceProvider.GetRequiredService<TelemetryExporterRegistry>().Active.Select(e => e.Name);

        await Assert.That(active).IsEquivalentTo([ObservabilityProvider.Console, ObservabilityProvider.LogFile]);
        await Assert.That(ObservabilityProvider.Parse(options.Provider)).Count().IsEqualTo(2);
        await Assert.That(serviceProvider.GetService<TracerProvider>()).IsNotNull();
    }

    [Test]
    public async Task Custom_and_builtin_exporters_work_together()
    {
        using var directory = new TempDirectory();
        using var source = new ActivitySource("pedidos-api");
        var spans = new List<Activity>();
        await using (var provider = Build(directory, observability: o => o.AddExporter(new SpanMemoryExporter(spans)),
            settings: ("Provider", "Memoria, LogFile")))
        {
            var tracer = provider.GetRequiredService<TracerProvider>();
            using (source.StartActivity("Operacao"))
                provider.GetRequiredService<ILoggerFactory>().CreateLogger("Teste").LogInformation("dentro do span");
            tracer.ForceFlush(5_000);
        }

        await Assert.That(spans.Any(a => a.OperationName == "Operacao")).IsTrue();
        await Assert.That(directory.Read("pedidos-api-20261005.txt")).Contains("dentro do span | trace_id=");
    }

    [Test]
    public async Task Destination_without_exporter_in_list_fails_at_startup()
    {
        using var directory = new TempDirectory();
        await using var provider = Build(directory, settings: ("Provider", "LogFile, Datadog"));

        var exception = await Assert.That(() => provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value)
            .Throws<OptionsValidationException>();
        await Assert.That(exception!.Message).Contains("Provider 'Datadog' não tem exportador registrado");
    }

    [Test]
    public async Task None_combined_with_another_destination_is_rejected_at_registration() =>
        await Assert.That(() => new ServiceCollection().AddTecObservability(TestApp.Configuration(("Provider", "None, Console"))))
            .Throws<InvalidOperationException>();

    /// <summary>Exportador próprio de teste: spans em memória.</summary>
    private sealed class SpanMemoryExporter(List<Activity> spans) : ITelemetryExporter
    {
        public string Name => "Memoria";

        public void Configure(TelemetryExporterContext context) =>
            context.AddStandardInstrumentation().OpenTelemetry.WithTracing(tracing => tracing.AddInMemoryExporter(spans));
    }
}
