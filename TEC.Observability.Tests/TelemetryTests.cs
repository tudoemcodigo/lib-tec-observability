using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TEC.Observability.DependencyInjection;
using TEC.Observability.Tracing;

namespace TEC.Observability.Tests;

/// <summary>Correlation id e pipeline de tracing de ponta a ponta (exportador em memória).</summary>
public class TelemetryTests
{
    // ---------- Correlation id ----------

    [Test]
    public async Task Received_correlation_id_is_returned_and_available_in_request()
    {
        await using var app = await TestApp.StartAsync(endpoints: a => a.MapGet("/eco", () => CorrelationId.Current));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/eco");
        request.Headers.Add(CorrelationId.HeaderName, "pedido-42");
        using var response = await app.GetTestClient().SendAsync(request);

        await Assert.That(response.Headers.GetValues(CorrelationId.HeaderName).Single()).IsEqualTo("pedido-42");
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("pedido-42");
    }

    [Test]
    public async Task Missing_correlation_id_is_generated()
    {
        await using var app = await TestApp.StartAsync(endpoints: a => a.MapGet("/eco", () => CorrelationId.Current));

        using var response = await app.GetTestClient().GetAsync("/eco");

        await Assert.That(response.Headers.GetValues(CorrelationId.HeaderName).Single()).IsNotEmpty();
    }

    [Test]
    public async Task Unsafe_correlation_id_is_replaced()
    {
        await using var app = await TestApp.StartAsync(endpoints: a => a.MapGet("/eco", () => CorrelationId.Current));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/eco");
        request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, "<script>alert(1)</script>");
        using var response = await app.GetTestClient().SendAsync(request);

        await Assert.That(response.Headers.GetValues(CorrelationId.HeaderName).Single()).DoesNotContain("<");
    }

    // ---------- Tracing ----------

    [Test]
    public async Task Business_request_generates_spans_with_correlation_id_and_health_check_does_not()
    {
        var serviceName = $"pedidos-{Guid.NewGuid():N}";
        using var source = new ActivitySource(serviceName);
        var exported = new List<Activity>();

        await using var app = await TestApp.StartAsync(
            services: s => s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported)),
            endpoints: a => a.MapGet("/pedidos", () =>
            {
                using var activity = source.StartActivity("ProcessarPedido");
                activity?.SetTag("pedido.id", 42);
                return "ok";
            }),
            settings: [("ServiceName", serviceName), ("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317")]);

        var client = app.GetTestClient();
        (await client.GetAsync("/health/live")).Dispose();
        (await client.GetAsync("/health/ready")).Dispose();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/pedidos");
        request.Headers.Add(CorrelationId.HeaderName, "pedido-42");
        (await client.SendAsync(request)).Dispose();

        var spans = await WaitForAsync(exported, list => list.Any(a => a.Kind == ActivityKind.Server && UrlPath(a) == "/pedidos"));
        var server = spans.Single(a => a.Kind == ActivityKind.Server && UrlPath(a) == "/pedidos");
        var business = spans.Single(a => a.OperationName == "ProcessarPedido");

        await Assert.That(server.GetTagItem(CorrelationId.AttributeName)).IsEqualTo("pedido-42");
        await Assert.That(business.TraceId).IsEqualTo(server.TraceId);
        await Assert.That(business.ParentSpanId).IsEqualTo(server.SpanId);
        await Assert.That(spans.Any(a => UrlPath(a)?.StartsWith("/health", StringComparison.Ordinal) == true)).IsFalse();

        var resource = app.Services.GetRequiredService<TracerProvider>().GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);
        await Assert.That(resource["service.name"]).IsEqualTo(serviceName);
        await Assert.That(resource["service.version"]).IsEqualTo("1.2.3");
        await Assert.That(resource["deployment.environment"]).IsEqualTo("test");
        // service.instance.id é por processo (GUID); a máquina vai em host.name.
        await Assert.That(Guid.TryParse(resource["service.instance.id"] as string, out _)).IsTrue();
        await Assert.That(resource["host.name"]).IsEqualTo(Environment.MachineName);
    }

    // ---------- Bibliotecas TEC (TEC.Cqrs, TEC.Vault...) ----------

    [Test]
    public async Task TEC_sources_and_meters_are_exported_by_default()
    {
        var name = $"TEC.Cqrs.Teste{Guid.NewGuid():N}";
        using var source = new ActivitySource(name);
        using var meter = new System.Diagnostics.Metrics.Meter(name);
        var spans = new List<Activity>();
        var metrics = new List<OpenTelemetry.Metrics.Metric>();

        await using var app = await TestApp.StartAsync(
            services: s => s
                .ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(spans))
                .ConfigureOpenTelemetryMeterProvider(m => m.AddInMemoryExporter(metrics)),
            settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317")]);

        using (source.StartActivity("ExecutarComando"))
        {
        }
        meter.CreateCounter<long>("tec.comandos").Add(1);
        app.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000);
        app.Services.GetRequiredService<OpenTelemetry.Metrics.MeterProvider>().ForceFlush(5_000);

        await Assert.That(TestApp.Snapshot(spans).Any(a => a.Source.Name == name)).IsTrue();
        await Assert.That(TestApp.Snapshot(metrics).Any(m => m.MeterName == name)).IsTrue();
    }

    [Test]
    public async Task With_Provider_None_sources_join_another_library_pipeline()
    {
        // Simula o .NET Aspire ServiceDefaults montando o OpenTelemetry; a biblioteca fica com Provider None.
        var name = $"TEC.Vault.Teste{Guid.NewGuid():N}";
        using var source = new ActivitySource(name);
        var spans = new List<Activity>();

        await using var app = await TestApp.StartAsync(
            servicesBefore: s => s.AddOpenTelemetry().WithTracing(t => t.AddInMemoryExporter(spans)));

        using (source.StartActivity("LerSegredo"))
        {
        }
        app.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000);

        await Assert.That(TestApp.Snapshot(spans).Any(a => a.Source.Name == name)).IsTrue();
    }

    // ---------- OTLP: variáveis padrão e configuração tardia ----------

    [Test]
    public async Task Otlp_without_own_configuration_uses_OTEL_EXPORTER_OTLP_variables()
    {
        var collector = new CapturingHandler();
        var name = $"otlp-env-{Guid.NewGuid():N}";
        using var source = new ActivitySource(name);

        await using var app = await TestApp.StartAsync(
            services: s => s.Configure<OpenTelemetry.Exporter.OtlpExporterOptions>(o => o.HttpClientFactory = () => new HttpClient(collector, disposeHandler: false)),
            rootSettings: new Dictionary<string, string?>
            {
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "https://coletor-aspire.exemplo.com:4318",
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
                ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-api-key=chave-do-ambiente",
            },
            settings: [("ServiceName", name), ("Provider", "Otlp")]);

        using (source.StartActivity("Operacao"))
        {
        }
        app.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000);

        var traces = collector.Requests.Single(r => r.Uri.AbsolutePath == "/v1/traces");
        await Assert.That(traces.Uri.Host).IsEqualTo("coletor-aspire.exemplo.com");
        await Assert.That(traces.ApiKey).IsEqualTo("chave-do-ambiente");
    }

    [Test]
    public async Task Otlp_headers_from_provider_added_after_registration_are_used()
    {
        var collector = new CapturingHandler();
        var name = $"otlp-cofre-{Guid.NewGuid():N}";
        using var source = new ActivitySource(name);

        await using var app = await TestApp.StartAsync(
            services: s => s.Configure<OpenTelemetry.Exporter.OtlpExporterOptions>(o => o.HttpClientFactory = () => new HttpClient(collector, disposeHandler: false)),
            lateSettings: new Dictionary<string, string?> { ["Observability:Otlp:Headers:x-api-key"] = "segredo-do-cofre" },
            settings: [("ServiceName", name), ("Provider", "Otlp"), ("Otlp:Protocol", "HttpProtobuf"), ("Otlp:Endpoint", "https://collector.exemplo.com:4318")]);

        using (source.StartActivity("Operacao"))
        {
        }
        app.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000);

        var traces = collector.Requests.Single(r => r.Uri.AbsolutePath == "/v1/traces");
        await Assert.That(traces.ApiKey).IsEqualTo("segredo-do-cofre");
    }

    [Test]
    public async Task Otlp_http_export_sends_metrics_and_logs_on_signal_paths()
    {
        var collector = new CapturingHandler();
        var name = $"otlp-sinais-{Guid.NewGuid():N}";
        using var meter = new System.Diagnostics.Metrics.Meter(name);

        await using var app = await TestApp.StartAsync(
            services: s => s.Configure<OpenTelemetry.Exporter.OtlpExporterOptions>(o => o.HttpClientFactory = () => new HttpClient(collector, disposeHandler: false)),
            settings:
            [
                ("ServiceName", name), ("Provider", "Otlp"), ("Otlp:Protocol", "HttpProtobuf"),
                ("Otlp:Endpoint", "https://collector.exemplo.com:4318/base"), ("Otlp:Headers:x-api-key", "chave-dos-sinais"),
            ]);

        meter.CreateCounter<long>("pedidos.criados").Add(1);
        app.Services.GetRequiredService<ILogger<TelemetryTests>>()
            .LogWarning("Pedido {PedidoId} recusado", 42);
        app.Services.GetRequiredService<MeterProvider>().ForceFlush(10_000);
        app.Services.GetRequiredService<LoggerProvider>().ForceFlush(10_000);

        var metrics = collector.Requests.Where(r => r.Uri.AbsolutePath == "/base/v1/metrics").ToList();
        var logs = collector.Requests.Where(r => r.Uri.AbsolutePath == "/base/v1/logs").ToList();
        await Assert.That(metrics).IsNotEmpty();
        await Assert.That(logs).IsNotEmpty();
        await Assert.That(metrics.Concat(logs).All(r => r.Uri.Authority == "collector.exemplo.com:4318" && r.ApiKey == "chave-dos-sinais")).IsTrue();
    }

    // ---------- Fontes e medidores adicionais ----------

    [Test]
    public async Task AddActivitySource_and_AddMeter_include_sources_and_meters_beyond_defaults()
    {
        var suffix = Guid.NewGuid().ToString("N");
        using var registeredSource = new ActivitySource($"Pedidos.Integracao.{suffix}");
        using var unregisteredSource = new ActivitySource($"Outra.Fonte.{suffix}");
        using var registeredMeter = new System.Diagnostics.Metrics.Meter($"Pedidos.Metricas.{suffix}");
        using var unregisteredMeter = new System.Diagnostics.Metrics.Meter($"Outro.Medidor.{suffix}");
        var spans = new List<Activity>();
        var metrics = new List<Metric>();

        await using var app = await TestApp.StartAsync(
            o => o.AddActivitySource(registeredSource.Name).AddMeter(registeredMeter.Name),
            services: s => s
                .ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(spans))
                .ConfigureOpenTelemetryMeterProvider(m => m.AddInMemoryExporter(metrics)),
            settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317")]);

        using (registeredSource.StartActivity("Registrada"))
        {
        }
        using (unregisteredSource.StartActivity("NaoRegistrada"))
        {
        }
        registeredMeter.CreateCounter<long>("registrado").Add(1);
        unregisteredMeter.CreateCounter<long>("nao.registrado").Add(1);
        app.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000);
        app.Services.GetRequiredService<MeterProvider>().ForceFlush(5_000);

        await Assert.That(TestApp.Snapshot(spans).Any(a => a.Source.Name == registeredSource.Name)).IsTrue();
        await Assert.That(TestApp.Snapshot(spans).Any(a => a.Source.Name == unregisteredSource.Name)).IsFalse();
        await Assert.That(TestApp.Snapshot(metrics).Any(m => m.MeterName == registeredMeter.Name)).IsTrue();
        await Assert.That(TestApp.Snapshot(metrics).Any(m => m.MeterName == unregisteredMeter.Name)).IsFalse();
    }

    [Test]
    public async Task AddActivitySource_and_AddMeter_reject_null_list()
    {
        var builder = new ServiceCollection().AddTecObservability(TestApp.Configuration());

        await Assert.That(() => builder.AddActivitySource(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => builder.AddMeter(null!)).Throws<ArgumentNullException>();
    }

    private static string? UrlPath(Activity activity) => activity.GetTagItem("url.path") as string;

    private static async Task<List<Activity>> WaitForAsync(List<Activity> exported, Func<List<Activity>, bool> condition)
    {
        // O span da requisição é encerrado pelo servidor depois que a resposta já chegou ao cliente. Cópia com ToArray: o exportador
        // em memória adiciona spans em outra thread sem lock na lista (copiar com o construtor de List pode lançar ArgumentException).
        for (var i = 0; i < 100; i++)
        {
            var snapshot = TestApp.Snapshot(exported);
            if (condition(snapshot))
                return snapshot;
            await Task.Delay(50);
        }

        return TestApp.Snapshot(exported);
    }
}
