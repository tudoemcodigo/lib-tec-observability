using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Trace;
using TEC.Observability.Configuration;
using TEC.Observability.DependencyInjection;
using TEC.Observability.Exporters;

namespace TEC.Observability.Tests;

/// <summary>Ponto de extensão de exportadores (<see cref="ITelemetryExporter"/>): escolha pelo Provider, erros e pipeline comum.</summary>
public class ExporterTests
{
    /// <summary>Exportador próprio de teste: spans em memória, com a instrumentação padrão da biblioteca.</summary>
    private sealed class MemoryExporter(List<Activity> spans, string name = "Memoria") : ITelemetryExporter
    {
        public int Configured { get; private set; }

        public string Name => name;

        public void Configure(TelemetryExporterContext context)
        {
            Configured++;
            context.AddStandardInstrumentation().OpenTelemetry.WithTracing(tracing => tracing.AddInMemoryExporter(spans));
        }
    }

    [Test]
    public async Task Custom_exporter_is_enabled_by_provider_name()
    {
        var serviceName = $"proprio-{Guid.NewGuid():N}";
        using var source = new ActivitySource(serviceName);
        var spans = new List<Activity>();
        var exporter = new MemoryExporter(spans);

        await using var app = await TestApp.StartAsync(o => o.AddExporter(exporter),
            settings: [("ServiceName", serviceName), ("Provider", "memoria")]);

        using (source.StartActivity("Operacao"))
        {
        }
        app.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000);

        await Assert.That(exporter.Configured).IsEqualTo(1);
        await Assert.That(TestApp.Snapshot(spans).Any(a => a.OperationName == "Operacao")).IsTrue();
        // Pipeline comum montado pela biblioteca: recurso do serviço.
        var resource = app.Services.GetRequiredService<TracerProvider>().GetResource().Attributes.ToDictionary(a => a.Key, a => a.Value);
        await Assert.That(resource["service.name"]).IsEqualTo(serviceName);
    }

    [Test]
    public async Task Exporter_of_another_provider_is_not_enabled()
    {
        var exporter = new MemoryExporter([]);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecObservability(TestApp.Configuration()).AddExporter(exporter);
        await using var provider = services.BuildServiceProvider();

        await Assert.That(() => provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value).ThrowsNothing();
        await Assert.That(exporter.Configured).IsEqualTo(0);
        await Assert.That(provider.GetService<TracerProvider>()).IsNull();
    }

    [Test]
    public async Task Provider_without_registered_exporter_fails_at_startup_with_available_names()
    {
        var exception = await Assert.That(async () => await TestApp.StartAsync(o => o.AddExporter(new MemoryExporter([])),
                settings: [("Provider", "Datadog")]))
            .Throws<OptionsValidationException>();

        await Assert.That(exception!.Message).Contains("Provider 'Datadog' não tem exportador registrado");
        await Assert.That(exception.Message).Contains("registrados: Memoria");
    }

    [Test]
    [Arguments("None")]
    [Arguments("otlp")]
    [Arguments("Console")]
    public async Task Builtin_provider_name_is_rejected(string name)
    {
        var builder = new ServiceCollection().AddTecObservability(TestApp.Configuration());

        await Assert.That(() => builder.AddExporter(new MemoryExporter([], name))).Throws<ArgumentException>();
    }

    [Test]
    [Arguments("")]
    [Arguments("com espaco")]
    public async Task Invalid_name_is_rejected(string name)
    {
        var builder = new ServiceCollection().AddTecObservability(TestApp.Configuration());

        await Assert.That(() => builder.AddExporter(new MemoryExporter([], name))).Throws<ArgumentException>();
    }

    [Test]
    public async Task Exporter_with_duplicate_name_is_rejected()
    {
        var builder = new ServiceCollection().AddTecObservability(TestApp.Configuration(("Provider", "Memoria")))
            .AddExporter(new MemoryExporter([]));

        await Assert.That(() => builder.AddExporter(new MemoryExporter([], "MEMORIA"))).Throws<InvalidOperationException>();
    }
}
