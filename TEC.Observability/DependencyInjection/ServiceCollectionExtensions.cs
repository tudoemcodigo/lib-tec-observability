using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using TEC.Observability.Configuration;
using TEC.Observability.Exporters;
using TEC.Observability.HealthChecks;
using TEC.Observability.Tracing;

namespace TEC.Observability.DependencyInjection;

/// <summary>Registro da observabilidade no container de injeção de dependência.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Prefixo das fontes (<c>ActivitySource</c>) e medidores (<c>Meter</c>) das bibliotecas TEC (ex.: TEC.Cqrs, TEC.Vault),
    /// exportados por padrão.
    /// </summary>
    public const string TecTelemetryPrefix = "TEC.*";

    /// <summary>
    /// Registra traces, métricas e logs (OpenTelemetry) com o exportador definido em <c>Observability:Provider</c>, o liveness
    /// e a infraestrutura dos health checks. O código de negócio continua usando só <c>ActivitySource</c>, <c>Meter</c> e <c>ILogger</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// As opções são registradas no padrão do .NET (<c>IOptions</c>, <c>IOptionsMonitor</c> e <c>IOptionsSnapshot</c> refletem a
    /// configuração completa) e validadas na subida do host. Exportadores, health checks e endpoints leem os valores quando são
    /// criados, então provedores de configuração adicionados depois desta chamada (ex.: cofre de segredos) valem para
    /// connection string, cabeçalhos OTLP, nome e versão do serviço. A exceção é <c>Observability:Provider</c>, que decide o
    /// pipeline montado aqui e precisa estar na configuração disponível no momento da chamada.
    /// </para>
    /// <para>
    /// <c>services.Configure&lt;ObservabilityOptions&gt;(...)</c> registrado depois desta chamada vence o <c>appsettings.json</c>,
    /// assim como o parâmetro <paramref name="configure"/>.
    /// </para>
    /// <para>
    /// <c>None</c>, <c>Otlp</c>, <c>Console</c> e <c>LogFile</c> funcionam só com este pacote, sozinhos ou combinados
    /// (<c>"Console, LogFile"</c>). Outro destino no <c>Provider</c> exige o exportador registrado no
    /// retorno desta chamada: <c>.AddAzureMonitorExporter()</c> (pacote TEC.Observability.Azure) ou
    /// <see cref="ObservabilityBuilder.AddExporter"/> com um <see cref="ITelemetryExporter"/> próprio; sem ele, a subida falha
    /// com a instrução.
    /// </para>
    /// </remarks>
    /// <param name="services">Container.</param>
    /// <param name="configuration">Configuração da aplicação; a seção <c>Observability</c> é ligada a <see cref="ObservabilityOptions"/>.</param>
    /// <param name="configure">Ajustes em código, aplicados depois do <c>appsettings.json</c>. Pode ser executado mais de uma vez.</param>
    /// <returns>Builder para encadear health checks de dependências e fontes de telemetria.</returns>
    /// <exception cref="InvalidOperationException">Chamado mais de uma vez, ou <c>Provider</c> inválido.</exception>
    /// <exception cref="OptionsValidationException">Configuração inválida, na subida do host ou na primeira leitura das opções.</exception>
    /// <example>
    /// <code>
    /// builder.Services.AddTecObservability(builder.Configuration)
    ///     .AddDatabaseHealthCheck("PrimaryDb", SqlClientFactory.Instance, connectionString)
    ///     .AddSsoHealthCheck("KeycloakSSO", idpDiscoveryUrl)
    ///     .AddExternalServiceHealthCheck("PaymentGateway", apiUri);
    ///
    /// // Com "Observability:Provider": "Azure" (pacote TEC.Observability.Azure)
    /// builder.Services.AddTecObservability(builder.Configuration)
    ///     .AddAzureMonitorExporter();
    /// </code>
    /// </example>
    public static ObservabilityBuilder AddTecObservability(this IServiceCollection services, IConfiguration configuration,
        Action<ObservabilityOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (services.Any(d => d.ServiceType == typeof(RequestPathFilter)))
            throw new InvalidOperationException("AddTecObservability já foi chamado. Configure a observabilidade em uma única chamada.");

        // Foto da configuração disponível agora: decide só o que precisa ser decidido no registro (o pipeline do Provider).
        var registration = ObservabilityOptionsSetup.CreateSnapshot(configuration, configure);
        var providers = ObservabilityProvider.Parse(registration.Provider);
        if (ObservabilityProvider.Check(providers) is { } problem)
            throw new InvalidOperationException($"Configuração inválida na seção '{ObservabilityOptions.SectionName}': {problem}");

        var exporters = new TelemetryExporterRegistry(providers);
        services.AddSingleton(exporters);
        var budgets = new HealthCheckBudgets();
        AddOptions(services, configuration, configure, exporters, budgets);

        services.AddSingleton(sp => RequestPathFilter.Create(sp.GetRequiredService<IOptions<ObservabilityOptions>>().Value));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<HealthReportCache>();
        // Sem os loggers padrão do IHttpClientFactory: no .NET 8 eles registram a URL completa de cada requisição, e a query
        // string de uma sondagem pode trazer chave ou token. As falhas já são registradas pelo próprio health check, sem a URL.
        services.AddHttpClient(HttpEndpointHealthCheck.HttpClientName)
            .RemoveAllLoggers()
            .ConfigurePrimaryHttpMessageHandler(HttpEndpointHealthCheck.CreateHandler);

        var healthChecks = services.AddHealthChecks();
        AddLivenessCheck(services);

        // Fontes do serviço e das bibliotecas TEC: valem para qualquer provedor de telemetria do container — inclusive o
        // montado por outra biblioteca (ex.: .NET Aspire ServiceDefaults) com Provider None. Sem provedor, não têm efeito.
        // A redação vai para o início da coleção, antes de qualquer exportador (inclusive os registrados antes desta chamada):
        // roda antes deles no fim de cada span.
        RegisterFirst(services, s => s.ConfigureOpenTelemetryTracerProvider((sp, tracing) =>
        {
            HostFilteringPropagator.Install();
            BaggageHostPolicy.Default = new BaggageHostPolicy(sp.Options().BaggageAllowedHosts ?? []);
            tracing.AddSource(sp.Options().ServiceName, TecTelemetryPrefix);
            tracing.AddProcessor(new SpanRedactionProcessor());
        }));
        AddRedaction(services);
        AddLogRedaction(services);
        services.ConfigureOpenTelemetryMeterProvider((sp, metrics) => metrics.AddMeter(sp.Options().ServiceName, TecTelemetryPrefix));

        // Otlp, Console e LogFile são ligados aqui; os demais (ex.: Azure), quando o exportador for registrado no builder (AddExporter).
        foreach (var provider in providers)
        {
            if (TelemetryExporterStrategies.BuiltIn(provider) is { } builtIn)
                ActivateExporter(services, configuration, exporters, builtIn);
        }

        return new ObservabilityBuilder(services, configuration, registration, healthChecks, exporters, budgets);
    }

    private static void AddOptions(IServiceCollection services, IConfiguration configuration, Action<ObservabilityOptions>? configure,
        TelemetryExporterRegistry exporters, HealthCheckBudgets budgets)
    {
        var section = configuration.GetSection(ObservabilityOptions.SectionName);

        // Equivale ao Bind(section) do OptionsBuilder, com a lista de IgnoredPaths do appsettings substituindo a padrão.
        services.AddOptions<ObservabilityOptions>().Configure(options => ObservabilityOptionsSetup.Bind(section, options));
        services.AddSingleton<IOptionsChangeTokenSource<ObservabilityOptions>>(new ConfigurationChangeTokenSource<ObservabilityOptions>(section));
        if (configure is not null)
            services.Configure(configure);

        services.AddOptions<ObservabilityOptions>()
            .PostConfigure<IServiceProvider>((options, sp) =>
                ObservabilityOptionsSetup.ApplyDefaults(options, configuration, sp.GetService<IHostEnvironment>()?.IsDevelopment() == true))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ObservabilityOptions>>(sp =>
            new ObservabilityOptionsValidator(configuration, sp.GetService<IHostEnvironment>(), exporters, budgets));
    }

    /// <summary>
    /// Liveness da biblioteca, decidido quando as opções de health check são montadas: assim um check de mesmo nome registrado
    /// antes ou depois (ex.: o <c>self</c> do .NET Aspire ServiceDefaults) é respeitado em vez de virar nome duplicado — desde que
    /// tenha a tag <c>live</c>: sem ela, o <c>/health/live</c> ficaria sem verificação nenhuma, e a subida falha com a instrução.
    /// As opções são montadas na subida do host (<c>ValidateOnStart</c>), não na primeira sondagem.
    /// </summary>
    private static void AddLivenessCheck(IServiceCollection services) =>
        services.AddOptions<HealthCheckServiceOptions>().PostConfigure<IOptions<ObservabilityOptions>>((healthChecks, observability) =>
        {
            var options = observability.Value.HealthChecks;
            if (!options.Enabled || !options.RegisterLivenessCheck)
                return;

            var existing = healthChecks.Registrations.FirstOrDefault(r => string.Equals(r.Name, options.LivenessCheckName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                if (existing.Tags.Contains(HealthCheckTags.Live))
                    return;

                throw new InvalidOperationException($"Já existe um health check '{existing.Name}' sem a tag '{HealthCheckTags.Live}', e esse é o nome do "
                    + "liveness da biblioteca (HealthChecks:LivenessCheckName). Adicione a tag HealthCheckTags.Live ao check existente para usá-lo como "
                    + "liveness, defina outro HealthChecks:LivenessCheckName ou desligue HealthChecks:RegisterLivenessCheck.");
            }

            healthChecks.Registrations.Add(new HealthCheckRegistration(options.LivenessCheckName,
                sp => ActivatorUtilities.CreateInstance<LivenessHealthCheck>(sp), failureStatus: null, [HealthCheckTags.Live]));
        }).ValidateOnStart();

    /// <summary>Liga um exportador do Provider; o pipeline comum do OpenTelemetry é montado no primeiro e compartilhado pelos demais.</summary>
    internal static void ActivateExporter(IServiceCollection services, IConfiguration configuration, TelemetryExporterRegistry exporters,
        ITelemetryExporter exporter)
    {
        exporters.Activate(exporter);
        exporters.Pipeline ??= AddTelemetry(services);
        exporter.Configure(new TelemetryExporterContext(exporters.Pipeline, configuration, exporters));
    }

    /// <summary>
    /// Requisições de saída que não viram span de HttpClient: as sondagens dos health checks e as chamadas a cofres de segredos
    /// do Azure — a URL delas (<c>url.full</c>) traz o nome e a versão do segredo
    /// (<c>https://kv-x.vault.azure.net/secrets/nome/versão</c>). As operações do cofre continuam visíveis pelos spans do
    /// próprio TEC.Vault (fontes <c>TEC.*</c>).
    /// </summary>
    internal static bool ShouldTraceOutgoing(HttpRequestMessage request) =>
        !request.Options.TryGetValue(HttpEndpointHealthCheck.ProbeKey, out _) && !TelemetryRedaction.IsSecretStoreHost(request.RequestUri);

    /// <summary>
    /// Redação que vale para qualquer pipeline de tracing do container (dos exportadores da biblioteca ou montado por outra
    /// biblioteca): query string dos spans HTTP sempre redigida, mesmo com a redação da instrumentação desligada (a distro do
    /// Azure Monitor a desliga), e caminho de recursos de cofres do Azure sem nome e versão. Os enriquecedores já configurados
    /// (pelo serviço, pela distro) continuam rodando; a redação vem depois deles, para nenhum devolver a URL original.
    /// </summary>
    private static void AddRedaction(IServiceCollection services)
    {
        services.PostConfigureAll<AspNetCoreTraceInstrumentationOptions>(options =>
        {
            var previous = options.EnrichWithHttpRequest;
            options.EnrichWithHttpRequest = (activity, request) =>
            {
                previous?.Invoke(activity, request);
                TelemetryRedaction.RedactServerQuery(activity, request);
            };
        });
        services.PostConfigureAll<HttpClientTraceInstrumentationOptions>(options =>
        {
            var previous = options.EnrichWithHttpRequestMessage;
            options.EnrichWithHttpRequestMessage = (activity, request) =>
            {
                previous?.Invoke(activity, request);
                TelemetryRedaction.RedactClientUrl(activity);
            };
        });
    }

    /// <summary>
    /// Query string e credenciais de URL fora dos logs exportados, como nos spans: o processador entra antes dos exportadores
    /// (registrados depois desta linha) e reescreve os logs de requisição do ASP.NET Core e do <c>HttpClient</c>. No .NET 8, os
    /// logs padrão do <c>IHttpClientFactory</c> (que põem a URL completa também no escopo) são trocados por loggers equivalentes
    /// com a URL redigida; um cliente com logging próprio (<c>RemoveAllLoggers</c>/<c>AddLogger</c>) mantém o dele.
    /// </summary>
    private static void AddLogRedaction(IServiceCollection services)
    {
        // Os processadores rodam na ordem de registro: um exportador registrado antes desta chamada (ex.: .NET Aspire
        // ServiceDefaults com Provider None) veria o log sem redação. O registro do processador vai para o início da coleção.
        RegisterFirst(services, s => s.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddProcessor(new LogRedactionProcessor())));
#if !NET9_0_OR_GREATER
        services.ConfigureAll<Microsoft.Extensions.Http.HttpClientFactoryOptions>(options =>
            options.HttpMessageHandlerBuilderActions.Insert(0, RedactingHttpClientLogger.CaptureClientName));
        services.ConfigureHttpClientDefaults(client => client
            .RemoveAllLoggers()
            .AddLogger(sp => RedactingHttpClientLogger.Logical(sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()), wrapHandlersPipeline: true)
            .AddLogger(sp => RedactingHttpClientLogger.Client(sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()), wrapHandlersPipeline: false));
#endif
    }

    /// <summary>Faz os registros de <paramref name="register"/> e os move para o início da coleção, mantendo a ordem entre eles.</summary>
    private static void RegisterFirst(IServiceCollection services, Action<IServiceCollection> register)
    {
        var before = services.Count;
        register(services);
        var added = services.Skip(before).ToList();
        for (var i = 0; i < added.Count; i++)
        {
            services.RemoveAt(before + i);
            services.Insert(i, added[i]);
        }
    }

    private static OpenTelemetryBuilder AddTelemetry(IServiceCollection services)
    {
        // Pós-configuração de todas as instâncias de opções: compõe com o filtro de quem registrou a instrumentação (a distro do
        // Azure Monitor define o seu, para não rastrear a própria ingestão) em vez de ser sobrescrita por ele.
        services.AddSingleton<IPostConfigureOptions<AspNetCoreTraceInstrumentationOptions>>(sp =>
            new PostConfigureOptions<AspNetCoreTraceInstrumentationOptions, RequestPathFilter>(name: null, sp.GetRequiredService<RequestPathFilter>(),
                (options, filter) =>
                {
                    var previous = options.Filter;
                    options.Filter = context => filter.ShouldTrace(context) && (previous?.Invoke(context) ?? true);
                }));
        services.PostConfigureAll<HttpClientTraceInstrumentationOptions>(options =>
        {
            var previous = options.FilterHttpRequestMessage;
            options.FilterHttpRequestMessage = request => ShouldTraceOutgoing(request) && (previous?.Invoke(request) ?? true);
        });
        services.Configure<OpenTelemetryLoggerOptions>(o =>
        {
            o.IncludeScopes = true;
            o.IncludeFormattedMessage = true;
        });

        return services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddDetector(sp => new ServiceResourceDetector(sp.Options())))
            .WithTracing(_ => { })
            .WithMetrics(metrics => metrics.AddRuntimeInstrumentation())
            .WithLogging();
    }
}

/// <summary>
/// Atributos do recurso a partir das opções efetivas: <c>service.*</c>, <c>deployment.environment</c> e <c>host.name</c>.
/// <c>service.instance.id</c> é um GUID por processo — o nome da máquina se repete entre reinícios e entre processos do mesmo host.
/// </summary>
internal sealed class ServiceResourceDetector(ObservabilityOptions options) : IResourceDetector
{
    internal static readonly string InstanceId = Guid.NewGuid().ToString();

    public Resource Detect() => new(new Dictionary<string, object>
    {
        ["service.name"] = options.ServiceName,
        ["service.version"] = options.ServiceVersion,
        ["service.instance.id"] = InstanceId,
        ["host.name"] = Environment.MachineName,
        ["deployment.environment"] = options.Environment,
        ["deployment.environment.name"] = options.Environment,
    });
}
