using System.Diagnostics;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Trace;
using TEC.Observability.Configuration;
using TEC.Observability.DependencyInjection;
using TEC.Observability.Exporters;
using TEC.Observability.HealthChecks;

namespace TEC.Observability.Tests;

/// <summary>Exportador do Azure Monitor (pacote TEC.Observability.Azure): ativação, opções, validação e credencial.</summary>
public class AzureMonitorExporterTests
{
    private const string FakeConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost/";

    /// <summary>Registra com o exportador do Azure e lê as opções: a validação roda na leitura (e, num host, na subida).</summary>
    private static AzureMonitorExporterOptions Register(params (string Key, string? Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecObservability(TestApp.Configuration(settings)).AddAzureMonitorExporter();
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
        return provider.GetRequiredService<IOptions<AzureMonitorExporterOptions>>().Value;
    }

    // ---------- Ativação ----------

    [Test]
    public async Task Azure_provider_builds_the_pipeline_without_error()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TestApp.Configuration());
        services.AddTecObservability(TestApp.Configuration(("Provider", "Azure"), ("Azure:ConnectionString", FakeConnectionString)))
            .AddAzureMonitorExporter();

        await using var serviceProvider = services.BuildServiceProvider();

        await Assert.That(serviceProvider.GetService<TracerProvider>()).IsNotNull();
        await Assert.That(serviceProvider.GetService<OpenTelemetry.Metrics.MeterProvider>()).IsNotNull();
    }

    [Test]
    [Arguments("azure")]
    [Arguments("AZURE")]
    public async Task Provider_name_is_case_insensitive(string provider)
    {
        var options = Register(("Provider", provider), ("Azure:ConnectionString", FakeConnectionString));

        await Assert.That(options.ConnectionString).IsEqualTo(FakeConnectionString);
    }

    [Test]
    public async Task With_another_provider_the_registered_exporter_is_not_enabled()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecObservability(TestApp.Configuration(("Provider", "Console"))).AddAzureMonitorExporter();
        await using var provider = services.BuildServiceProvider();

        await Assert.That(() => provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value).ThrowsNothing();
        await Assert.That(services.Any(d => d.ServiceType == typeof(IConfigureOptions<AzureMonitorOptions>))).IsFalse();
        await Assert.That(services.Any(d => d.ServiceType == typeof(IValidateOptions<AzureMonitorExporterOptions>))).IsFalse();
    }

    [Test]
    public async Task Duplicate_exporter_registration_is_rejected()
    {
        var builder = new ServiceCollection().AddTecObservability(TestApp.Configuration(("Provider", "Azure")))
            .AddAzureMonitorExporter();

        await Assert.That(() => builder.AddAzureMonitorExporter()).Throws<InvalidOperationException>();
    }

    // ---------- Opções ----------

    [Test]
    public async Task Configuration_provider_added_after_registration_applies()
    {
        // Como o TEC.Vault: o cofre é carregado depois do AddTecObservability e traz a connection string.
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(TestApp.Settings(("Provider", "Azure")));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecObservability(configuration).AddAzureMonitorExporter();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Azure:ConnectionString"] = "InstrumentationKey=11111111-1111-1111-1111-111111111111;IngestionEndpoint=https://localhost/",
        });
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<AzureMonitorExporterOptions>>().Value;
        var azure = provider.GetRequiredService<IOptions<AzureMonitorOptions>>().Value;

        await Assert.That(options.ConnectionString).StartsWith("InstrumentationKey=11111111");
        await Assert.That(azure.ConnectionString).StartsWith("InstrumentationKey=11111111");
    }

    [Test]
    public async Task Code_override_wins_over_azure_section()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecObservability(TestApp.Configuration(("Provider", "Azure"), ("Azure:ConnectionString", FakeConnectionString)))
            .AddAzureMonitorExporter(o => o.EnableLiveMetrics = false);
        await using var provider = services.BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IOptions<AzureMonitorOptions>>().Value.EnableLiveMetrics).IsFalse();
    }

    [Test]
    public async Task Azure_distro_options_are_applied()
    {
        await using var app = await TestApp.StartAsync(o => o.AddAzureMonitorExporter(), settings:
        [
            ("Provider", "Azure"),
            ("Azure:ConnectionString", FakeConnectionString),
            ("Azure:EnableLiveMetrics", "false"),
            ("Azure:DisableOfflineStorage", "true"),
            ("SamplingRatio", "0.5"),
        ]);

        var azure = app.Services.GetRequiredService<IOptions<AzureMonitorOptions>>().Value;

        await Assert.That(azure.EnableLiveMetrics).IsFalse();
        await Assert.That(azure.DisableOfflineStorage).IsTrue();
        await Assert.That(azure.SamplingRatio).IsEqualTo(0.5f);
        await Assert.That(azure.TracesPerSecond).IsNull();
    }

    // ---------- Validação ----------

    [Test]
    public async Task Azure_without_connection_string_is_rejected()
    {
        var exception = await Assert.That(() => Register(("Provider", "Azure"), ("Azure:UseManagedIdentity", "true")))
            .Throws<OptionsValidationException>();

        await Assert.That(exception!.Message).Contains("Azure:ConnectionString é obrigatório");
    }

    [Test]
    public async Task Connection_string_from_default_variable_is_accepted()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(TestApp.Settings(("Provider", "Azure")))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["APPLICATIONINSIGHTS_CONNECTION_STRING"] = FakeConnectionString }).Build();
        var services = new ServiceCollection();
        services.AddTecObservability(configuration).AddAzureMonitorExporter();
        await using var provider = services.BuildServiceProvider();

        await Assert.That(() => provider.GetRequiredService<IOptions<AzureMonitorExporterOptions>>().Value).ThrowsNothing();
    }

    [Test]
    public async Task ManagedIdentityClientId_that_is_not_a_guid_is_rejected() =>
        await Assert.That(() => Register(("Provider", "Azure"), ("Azure:ConnectionString", "InstrumentationKey=00000000-0000-0000-0000-000000000000"),
            ("Azure:ManagedIdentityClientId", "nao-e-guid"))).Throws<OptionsValidationException>();

    [Test]
    public async Task Host_startup_fails_with_invalid_azure_configuration() =>
        await Assert.That(async () => await TestApp.StartAsync(o => o.AddAzureMonitorExporter(), settings: [("Provider", "Azure")]))
            .Throws<OptionsValidationException>();

    // ---------- Credencial ----------

    [Test]
    public async Task Outside_development_only_workload_identities_are_accepted()
    {
        var options = new AzureMonitorExporterOptions { UseManagedIdentity = true };

        await Assert.That(AzureMonitorTelemetryExporter.CreateCredential(options, development: false)).IsTypeOf<ChainedTokenCredential>();
        await Assert.That(AzureMonitorTelemetryExporter.CreateCredential(options, development: true)).IsTypeOf<DefaultAzureCredential>();
    }

    // ---------- Sondagens fora dos traces com a instrumentação da distro ----------

    [Test]
    public async Task Health_check_probe_does_not_create_outgoing_span()
    {
        await using var server = new RawHttpServer();
        var target = new Uri($"http://127.0.0.1:{server.Port}/health/live");
        var exported = new List<Activity>();

        // A distro registra a sua instrumentação de HttpClient; o filtro da biblioteca é composto com o dela.
        await using var app = await TestApp.StartAsync(
            o => o.AddAzureMonitorExporter().AddExternalServiceHealthCheck("Dependencia", target, h => h.Retries = 0),
            services: s =>
            {
                s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported));
                s.AddHttpClient(HttpEndpointHealthCheck.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler());
            },
            endpoints: a => a.MapGet("/chamar", async (IHttpClientFactory http) =>
            {
                using var response = await http.CreateClient().GetAsync(target);
                return (int)response.StatusCode;
            }),
            settings:
            [
                ("Provider", "Azure"), ("Azure:ConnectionString", FakeConnectionString),
                ("Azure:EnableLiveMetrics", "false"), ("Azure:DisableOfflineStorage", "true"),
            ]);
        var client = app.GetTestClient();

        (await client.GetAsync("/health/ready")).Dispose();
        (await client.GetAsync("/chamar")).Dispose(); // controle: chamada de negócio ao mesmo destino gera span
        app.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000);

        // Cópia com ToArray: o exportador em memória adiciona spans em outra thread sem lock na lista.
        List<Activity> outgoing = [.. exported.ToArray().OfType<Activity>().Where(a => a.Kind == ActivityKind.Client && Equals(a.GetTagItem("server.port"), server.Port))];

        await Assert.That(server.Requests.Count).IsEqualTo(2);
        await Assert.That(outgoing.Count).IsEqualTo(1);
    }
}
