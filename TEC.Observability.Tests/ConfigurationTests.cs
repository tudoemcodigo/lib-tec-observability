using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TEC.Observability.Configuration;
using TEC.Observability.DependencyInjection;
using TEC.Observability.Exporters;
using TEC.Observability.Tracing;

namespace TEC.Observability.Tests;

/// <summary>Binding, valores padrão e validação das opções; regras puras de rota, endpoint e correlation id.</summary>
public class ConfigurationTests
{
    private static ObservabilityOptions Register(params (string Key, string? Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecObservability(TestApp.Configuration(settings));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
    }

    // ---------- Binding ----------

    [Test]
    public async Task Default_values_are_applied()
    {
        var options = Register();

        await Assert.That(options.Provider).IsEqualTo(ObservabilityProvider.None);
        await Assert.That(options.SamplingRatio).IsEqualTo(1.0);
        await Assert.That(options.IgnoredPaths).IsEquivalentTo(["/health", "/health/live", "/health/ready", "/swagger"]);
        await Assert.That(options.HealthChecks.Enabled).IsTrue();
        await Assert.That(options.HealthChecks.LiveEndpoint).IsEqualTo("/health/live");
        await Assert.That(options.HealthChecks.ReadyEndpoint).IsEqualTo("/health/ready");
        await Assert.That(options.HealthChecks.ExposeExceptionDetails).IsFalse();
        // Sem ambiente conhecido (ou fora de Development), detalhes desligados.
        await Assert.That(options.HealthChecks.ExposeDetails == false).IsTrue();
        await Assert.That(options.HealthChecks.LivenessCheckName).IsEqualTo("self");
        await Assert.That(options.BaggageAllowedHosts).IsEmpty();
    }

    [Test]
    public async Task Otlp_section_is_bound_to_options()
    {
        var options = Register(
            ("Provider", "Otlp"),
            ("Otlp:Endpoint", "https://collector:4318"),
            ("Otlp:Protocol", "HttpProtobuf"),
            ("Otlp:Headers:x-api-key", "segredo"),
            ("SamplingRatio", "0.25"));

        await Assert.That(options.Provider).IsEqualTo(ObservabilityProvider.Otlp);
        await Assert.That(options.Otlp.Endpoint).IsEqualTo(new Uri("https://collector:4318"));
        await Assert.That(options.Otlp.Protocol).IsEqualTo(OtlpProtocol.HttpProtobuf);
        await Assert.That(options.Otlp.Headers["x-api-key"]).IsEqualTo("segredo");
        await Assert.That(options.SamplingRatio).IsEqualTo(0.25);
    }

    [Test]
    public async Task IgnoredPaths_from_appsettings_replaces_default_list()
    {
        var options = Register(("IgnoredPaths:0", "/metrics"));

        await Assert.That(options.IgnoredPaths).IsEquivalentTo(["/metrics"]);
    }

    [Test]
    public async Task Code_configuration_overrides_appsettings()
    {
        var services = new ServiceCollection();
        var builder = services.AddTecObservability(TestApp.Configuration(), o => o.ServiceVersion = "9.9.9");

        await Assert.That(builder.Options.ServiceVersion).IsEqualTo("9.9.9");
    }

    // ---------- Options no padrão do .NET (configuração completa, não uma foto do registro) ----------

    [Test]
    public async Task IOptionsMonitor_and_IOptionsSnapshot_reflect_configuration()
    {
        var services = new ServiceCollection();
        services.AddTecObservability(TestApp.Configuration(("ServiceVersion", "4.5.6")));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await Assert.That(provider.GetRequiredService<IOptionsMonitor<ObservabilityOptions>>().CurrentValue.ServiceVersion).IsEqualTo("4.5.6");
        await Assert.That(scope.ServiceProvider.GetRequiredService<IOptionsSnapshot<ObservabilityOptions>>().Value.ServiceVersion).IsEqualTo("4.5.6");
    }

    [Test]
    public async Task Service_Configure_after_registration_overrides_appsettings()
    {
        var services = new ServiceCollection();
        services.AddTecObservability(TestApp.Configuration());
        services.Configure<ObservabilityOptions>(o => o.ServiceVersion = "7.7.7");
        await using var provider = services.BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value.ServiceVersion).IsEqualTo("7.7.7");
    }

    [Test]
    public async Task Configuration_provider_added_after_registration_applies()
    {
        // Como o TEC.Vault: o cofre é carregado depois do AddTecObservability e traz o endpoint e a versão.
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(TestApp.Settings(("Provider", "Otlp")));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecObservability(configuration);
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Observability:Otlp:Endpoint"] = "https://collector.exemplo.com:4317",
            ["Observability:ServiceVersion"] = "5.5.5",
        });
        await using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;

        await Assert.That(options.Otlp.Endpoint).IsEqualTo(new Uri("https://collector.exemplo.com:4317"));
        await Assert.That(options.ServiceVersion).IsEqualTo("5.5.5");
    }

    [Test]
    public async Task Provider_changed_after_registration_is_rejected()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(TestApp.Settings());
        var services = new ServiceCollection();
        services.AddTecObservability(configuration);
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Observability:Provider"] = "Console" });
        await using var provider = services.BuildServiceProvider();

        await Assert.That(() => provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value)
            .Throws<OptionsValidationException>();
    }

    [Test]
    public async Task Empty_ServiceName_uses_OTEL_SERVICE_NAME()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(TestApp.Settings(("ServiceName", "")))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OTEL_SERVICE_NAME"] = "pedidos-aspire" }).Build();
        var services = new ServiceCollection();
        services.AddTecObservability(configuration);
        await using var provider = services.BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value.ServiceName).IsEqualTo("pedidos-aspire");
    }

    [Test]
    public async Task Empty_ServiceVersion_uses_entry_assembly_version()
    {
        var expected = System.Reflection.Assembly.GetEntryAssembly()!
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion;

        var options = Register(("ServiceVersion", ""));

        await Assert.That(options.ServiceVersion).IsEqualTo(expected);
    }

    [Test]
    public async Task Null_collection_is_rejected_with_clear_message()
    {
        var services = new ServiceCollection();
        services.AddTecObservability(TestApp.Configuration(("Provider", "Otlp"), ("Otlp:Endpoint", "https://collector:4317")), o =>
        {
            o.HealthChecks.AllowedHosts = null!;
            o.Otlp.Headers = null!;
        });
        await using var provider = services.BuildServiceProvider();

        var exception = await Assert.That(() => provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value)
            .Throws<OptionsValidationException>();

        await Assert.That(exception!.Message).Contains("HealthChecks:AllowedHosts não pode ser nulo");
        await Assert.That(exception.Message).Contains("Otlp:Headers não pode ser nulo");
    }

    [Test]
    public async Task Otlp_without_endpoint_accepts_OTEL_EXPORTER_OTLP_ENDPOINT()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(TestApp.Settings(("Provider", "Otlp")))
            .AddInMemoryCollection(new Dictionary<string, string?> { ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4317" }).Build();
        var services = new ServiceCollection();
        services.AddTecObservability(configuration);
        await using var provider = services.BuildServiceProvider();

        await Assert.That(() => provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value).ThrowsNothing();
    }

    // ---------- Validação ----------

    [Test]
    [Arguments("ServiceName")]
    [Arguments("Environment")]
    public async Task Missing_required_field_is_rejected(string key) =>
        await Assert.That(() => Register((key, "")))
            .Throws<OptionsValidationException>();

    [Test]
    [Arguments("-0.1")]
    [Arguments("1.5")]
    public async Task SamplingRatio_out_of_range_is_rejected(string ratio) =>
        await Assert.That(() => Register(("SamplingRatio", ratio)))
            .Throws<OptionsValidationException>();

    [Test]
    public async Task Otlp_without_endpoint_is_rejected() =>
        await Assert.That(() => Register(("Provider", "Otlp")))
            .Throws<OptionsValidationException>();

    [Test]
    public async Task Azure_provider_without_azure_package_fails_with_instructions()
    {
        var exception = await Assert.That(() => Register(("Provider", "Azure"), ("Azure:ConnectionString", "InstrumentationKey=x")))
            .Throws<OptionsValidationException>();

        await Assert.That(exception!.Message).Contains("TEC.Observability.Azure");
        await Assert.That(exception.Message).Contains(".AddAzureMonitorExporter()");
    }

    [Test]
    public async Task Empty_provider_equals_None()
    {
        var options = Register(("Provider", ""));

        await Assert.That(options.Provider).IsEqualTo(ObservabilityProvider.None);
    }

    [Test]
    [Arguments("otlp")]
    [Arguments("OTLP")]
    public async Task Provider_name_is_case_insensitive(string provider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTecObservability(TestApp.Configuration(("Provider", provider), ("Otlp:Endpoint", "http://localhost:4317")));
        await using var serviceProvider = services.BuildServiceProvider();

        await Assert.That(() => serviceProvider.GetRequiredService<IOptions<ObservabilityOptions>>().Value).ThrowsNothing();
        await Assert.That(serviceProvider.GetService<OpenTelemetry.Trace.TracerProvider>()).IsNotNull();
    }

    [Test]
    [Arguments("com espaco")]
    [Arguments("Otlp; com espaco")]
    [Arguments("Console, None")]
    public async Task Provider_with_invalid_name_is_rejected_at_registration(string provider) =>
        await Assert.That(() => new ServiceCollection().AddTecObservability(TestApp.Configuration(("Provider", provider))))
            .Throws<InvalidOperationException>();

    [Test]
    public async Task Equal_health_endpoints_are_rejected() =>
        await Assert.That(() => Register(("HealthChecks:LiveEndpoint", "/health"), ("HealthChecks:ReadyEndpoint", "/health")))
            .Throws<OptionsValidationException>();

    [Test]
    public async Task Duplicate_registration_is_rejected()
    {
        var services = new ServiceCollection();
        services.AddTecObservability(TestApp.Configuration());

        await Assert.That(() => services.AddTecObservability(TestApp.Configuration()))
            .Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments("Otlp")]
    [Arguments("Console")]
    public async Task Each_provider_builds_pipeline_without_error(string provider)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TestApp.Configuration());
        services.AddTecObservability(TestApp.Configuration(
            ("Provider", provider),
            ("Otlp:Endpoint", "http://localhost:4317")));

        await using var serviceProvider = services.BuildServiceProvider();

        await Assert.That(serviceProvider.GetService<OpenTelemetry.Trace.TracerProvider>()).IsNotNull();
        await Assert.That(serviceProvider.GetService<OpenTelemetry.Metrics.MeterProvider>()).IsNotNull();
    }

    // ---------- Filtro de rotas ----------

    [Test]
    [Arguments("/health", false)]
    [Arguments("/health/live", false)]
    [Arguments("/HEALTH/Ready", false)]
    [Arguments("/swagger/index.html", false)]
    [Arguments("/healthcare", true)]
    [Arguments("/pedidos", true)]
    [Arguments("/", true)]
    public async Task Filter_discards_ignored_routes_by_segment(string path, bool traced)
    {
        var filter = new RequestPathFilter(new ObservabilityOptions().IgnoredPaths);

        await Assert.That(filter.ShouldTrace(new PathString(path))).IsEqualTo(traced);
    }

    // ---------- OTLP ----------

    [Test]
    [Arguments("http://collector:4317", OtlpProtocol.Grpc, "http://collector:4317/")]
    [Arguments("http://collector:4318", OtlpProtocol.HttpProtobuf, "http://collector:4318/v1/traces")]
    [Arguments("https://otlp.exemplo.com/otlp/", OtlpProtocol.HttpProtobuf, "https://otlp.exemplo.com/otlp/v1/traces")]
    public async Task Http_endpoint_receives_signal_path(string endpoint, OtlpProtocol protocol, string expected) =>
        await Assert.That(OtlpTelemetryExporter.ResolveEndpoint(new Uri(endpoint), protocol, "v1/traces").AbsoluteUri).IsEqualTo(expected);

    // ---------- SSO ----------

    [Test]
    [Arguments("https://sso.exemplo.com/realms/tec", "https://sso.exemplo.com/realms/tec/.well-known/openid-configuration")]
    [Arguments("https://sso.exemplo.com/realms/tec/", "https://sso.exemplo.com/realms/tec/.well-known/openid-configuration")]
    [Arguments("https://login.microsoftonline.com/t/v2.0/.well-known/openid-configuration", "https://login.microsoftonline.com/t/v2.0/.well-known/openid-configuration")]
    public async Task Issuer_url_receives_discovery_path(string url, string expected) =>
        await Assert.That(ObservabilityBuilder.ResolveDiscoveryUrl(new Uri(url)).AbsoluteUri).IsEqualTo(expected);

    // ---------- Correlation id ----------

    [Test]
    [Arguments("abc-123_DEF.4:5", true)]
    [Arguments("", false)]
    [Arguments("com espaco", false)]
    [Arguments("quebra\r\nde-linha", false)]
    [Arguments("<script>", false)]
    public async Task Correlation_id_only_accepts_safe_characters(string value, bool safe) =>
        await Assert.That(CorrelationIdMiddleware.IsSafe(value)).IsEqualTo(safe);

    [Test]
    public async Task Too_long_correlation_id_is_rejected() =>
        await Assert.That(CorrelationIdMiddleware.IsSafe(new string('a', 129))).IsFalse();
}
