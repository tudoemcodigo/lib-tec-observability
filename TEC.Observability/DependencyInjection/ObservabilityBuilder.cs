using System.Data.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TEC.Observability.Configuration;
using TEC.Observability.Exporters;
using TEC.Observability.HealthChecks;
using TEC.Observability.Internal;

namespace TEC.Observability.DependencyInjection;

/// <summary>
/// Retorno do <c>AddTecObservability</c>: encadeia os health checks de dependências (todos sob a tag <c>ready</c>)
/// e o registro de fontes de telemetria de negócio.
/// </summary>
public sealed class ObservabilityBuilder
{
    private const string DefaultQuery = "SELECT 1";
    private static readonly TimeSpan DefaultDatabaseTimeout = TimeSpan.FromSeconds(5);

    private readonly HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly TelemetryExporterRegistry _exporters;
    private readonly HealthCheckBudgets _budgets;

    internal ObservabilityBuilder(IServiceCollection services, IConfiguration configuration, ObservabilityOptions options,
        IHealthChecksBuilder healthChecks, TelemetryExporterRegistry exporters, HealthCheckBudgets budgets)
    {
        Services = services;
        Configuration = configuration;
        Options = options;
        HealthChecks = healthChecks;
        _exporters = exporters;
        _budgets = budgets;

        if (options.HealthChecks is { RegisterLivenessCheck: true, LivenessCheckName: { Length: > 0 } livenessName })
            _names.Add(livenessName);
    }

    /// <summary>Container de injeção de dependência.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Configuração passada ao <c>AddTecObservability</c> (usada por pacotes de exportadores, como o do Azure).</summary>
    public IConfiguration Configuration { get; }

    /// <summary>
    /// Opções lidas da configuração disponível no momento do registro (configuração + ajustes em código). Em tempo de execução
    /// use <c>IOptions&lt;ObservabilityOptions&gt;</c>, que inclui provedores de configuração adicionados depois do registro.
    /// </summary>
    public ObservabilityOptions Options { get; }

    /// <summary>Builder nativo de health checks, para verificações que a biblioteca não cobre. Use as tags de <see cref="HealthCheckTags"/>.</summary>
    public IHealthChecksBuilder HealthChecks { get; }

    /// <summary>
    /// Registra um exportador de telemetria, escolhido pelo nome em <c>Observability:Provider</c>. Só é ligado quando
    /// <see cref="ITelemetryExporter.Name"/> está entre os destinos configurados (ex.: <c>"Meu"</c> ou <c>"Meu, LogFile"</c>); caso
    /// contrário a chamada não tem efeito — o mesmo código serve para ambientes com provedores diferentes (ex.: <c>Console</c> em
    /// desenvolvimento e o exportador próprio em produção).
    /// </summary>
    /// <param name="exporter">Exportador; o nome não pode ser <c>None</c>, <c>Otlp</c>, <c>Console</c> nem <c>LogFile</c> (embutidos).</param>
    /// <exception cref="ArgumentException">Nome inválido ou de um provedor embutido.</exception>
    /// <returns>O mesmo builder, para encadear.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="exporter"/> nulo.</exception>
    /// <exception cref="InvalidOperationException">Já existe um exportador registrado com o mesmo nome.</exception>
    public ObservabilityBuilder AddExporter(ITelemetryExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(exporter);
        var name = exporter.Name;
        if (!ObservabilityProvider.IsValidName(name))
            throw new ArgumentException($"Nome de exportador inválido: '{name}' (use letras, dígitos, '.', '-' e '_').", nameof(exporter));
        if (ObservabilityProvider.IsBuiltIn(name))
            throw new ArgumentException($"'{name}' é um provedor embutido e não pode ser substituído; use outro nome.", nameof(exporter));
        if (!_exporters.Register(name))
            throw new InvalidOperationException($"Já existe um exportador '{name}' registrado.");

        if (_exporters.Selects(exporter))
            ServiceCollectionExtensions.ActivateExporter(Services, Configuration, _exporters, exporter);
        return this;
    }

    /// <summary>Registra <c>ActivitySource</c>s adicionais para exportação (os de nome igual ao <c>ServiceName</c> e os <c>TEC.*</c> já são registrados).</summary>
    /// <param name="names">Nomes das fontes (aceitam curinga, ex.: <c>MinhaEmpresa.*</c>).</param>
    /// <returns>O mesmo builder, para encadear.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> nulo.</exception>
    public ObservabilityBuilder AddActivitySource(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource(names));
        return this;
    }

    /// <summary>Registra <c>Meter</c>s adicionais para exportação (os de nome igual ao <c>ServiceName</c> e os <c>TEC.*</c> já são registrados).</summary>
    /// <param name="names">Nomes dos medidores (aceitam curinga, ex.: <c>MinhaEmpresa.*</c>).</param>
    /// <returns>O mesmo builder, para encadear.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="names"/> nulo.</exception>
    public ObservabilityBuilder AddMeter(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(names));
        return this;
    }

    /// <summary>
    /// Health check de banco relacional a partir da fábrica do driver e da connection string.
    /// </summary>
    /// <param name="name">Nome da verificação no JSON.</param>
    /// <param name="providerFactory">Fábrica do driver ADO.NET, ex.: <c>SqlClientFactory.Instance</c> ou <c>NpgsqlFactory.Instance</c>.</param>
    /// <param name="connectionString">Connection string.</param>
    /// <param name="query">Consulta de verificação. Deve ser leve e fixa (nunca montada com entrada de usuário).</param>
    /// <param name="failureStatus">Status em caso de falha. Padrão: <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="timeout">
    /// Tempo máximo da verificação. Padrão: 5 segundos. Vale também para drivers que ignoram o cancelamento: a consulta recebe o
    /// <c>CommandTimeout</c> correspondente e a verificação é encerrada no prazo mesmo que o driver não responda. Precisa ser
    /// menor que <c>HealthChecks:Timeout</c> (conferido na subida).
    /// </param>
    /// <example>
    /// <code>
    /// .AddDatabaseHealthCheck("PrimaryDb", SqlClientFactory.Instance, connectionString)
    /// .AddDatabaseHealthCheck("Relatorios", NpgsqlFactory.Instance, pgConnectionString, query: "SELECT 1 FROM pg_database LIMIT 1")
    /// </code>
    /// </example>
    /// <returns>O mesmo builder, para encadear.</returns>
    /// <exception cref="ArgumentException">Nome vazio ou repetido, connection string ou consulta vazia.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> menor ou igual a zero.</exception>
    public ObservabilityBuilder AddDatabaseHealthCheck(string name, DbProviderFactory providerFactory, string connectionString,
        string query = DefaultQuery, HealthStatus? failureStatus = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return AddDatabaseHealthCheck(name, _ =>
        {
            var connection = providerFactory.CreateConnection()
                ?? throw new InvalidOperationException($"{providerFactory.GetType().Name} não criou uma conexão.");
            connection.ConnectionString = connectionString;
            return connection;
        }, query, failureStatus, timeout);
    }

    /// <summary>Health check de banco relacional a partir de uma fábrica de conexão (qualquer driver ADO.NET).</summary>
    /// <param name="name">Nome da verificação no JSON.</param>
    /// <param name="connectionFactory">Cria uma conexão nova (fechada) a cada verificação.</param>
    /// <param name="query">Consulta de verificação. Deve ser leve e fixa (nunca montada com entrada de usuário).</param>
    /// <param name="failureStatus">Status em caso de falha. Padrão: <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="timeout">Tempo máximo da verificação. Padrão: 5 segundos; menor que <c>HealthChecks:Timeout</c>.</param>
    /// <returns>O mesmo builder, para encadear.</returns>
    /// <exception cref="ArgumentException">Nome vazio ou repetido, ou consulta vazia.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> menor ou igual a zero.</exception>
    /// <example>
    /// <code>
    /// .AddDatabaseHealthCheck("PrimaryDb", () => new NpgsqlConnection(connectionString))
    /// </code>
    /// </example>
    public ObservabilityBuilder AddDatabaseHealthCheck(string name, Func<DbConnection> connectionFactory,
        string query = DefaultQuery, HealthStatus? failureStatus = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        return AddDatabaseHealthCheck(name, _ => connectionFactory(), query, failureStatus, timeout);
    }

    /// <summary>Health check de banco relacional com a conexão resolvida pelo container (ex.: a partir de um <c>DbDataSource</c> registrado).</summary>
    /// <remarks>A conexão devolvida é fechada e descartada ao final de cada verificação: devolva sempre uma conexão nova.</remarks>
    /// <param name="name">Nome da verificação no JSON.</param>
    /// <param name="connectionFactory">Cria uma conexão nova (fechada) a cada verificação, com acesso ao container.</param>
    /// <param name="query">Consulta de verificação. Deve ser leve e fixa (nunca montada com entrada de usuário).</param>
    /// <param name="failureStatus">Status em caso de falha. Padrão: <see cref="HealthStatus.Unhealthy"/>.</param>
    /// <param name="timeout">Tempo máximo da verificação. Padrão: 5 segundos; menor que <c>HealthChecks:Timeout</c>.</param>
    /// <returns>O mesmo builder, para encadear.</returns>
    /// <exception cref="ArgumentException">Nome vazio ou repetido, ou consulta vazia.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> menor ou igual a zero.</exception>
    public ObservabilityBuilder AddDatabaseHealthCheck(string name, Func<IServiceProvider, DbConnection> connectionFactory,
        string query = DefaultQuery, HealthStatus? failureStatus = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "O timeout deve ser maior que zero.");
        ReserveName(name);

        var effectiveTimeout = _budgets.AddDatabase(name, timeout ?? DefaultDatabaseTimeout);
        HealthChecks.Add(new HealthCheckRegistration(name,
            sp => ActivatorUtilities.CreateInstance<DatabaseHealthCheck>(sp, (Func<DbConnection>)(() => connectionFactory(sp)), query, effectiveTimeout),
            failureStatus, [HealthCheckTags.Ready, HealthCheckTags.Database], effectiveTimeout));
        return this;
    }

    /// <summary>
    /// Health check de SSO / provedor de identidade (Entra ID, Keycloak, Auth0...): busca o documento de descoberta OpenID Connect
    /// e confere que ele traz o <c>issuer</c>.
    /// </summary>
    /// <param name="name">Nome da verificação no JSON.</param>
    /// <param name="discoveryUrl">
    /// URL do documento de descoberta, ou a URL base do emissor (authority) — nesse caso <c>/.well-known/openid-configuration</c> é acrescentado.
    /// </param>
    /// <param name="configure">Timeout, tentativas, status de falha e tags.</param>
    /// <example>
    /// <code>
    /// .AddSsoHealthCheck("KeycloakSSO", new Uri("https://sso.empresa.com/realms/tec"), o => o.Timeout = TimeSpan.FromSeconds(3))
    /// </code>
    /// </example>
    /// <returns>O mesmo builder, para encadear.</returns>
    /// <exception cref="ArgumentException">Nome vazio ou repetido, URL não http(s) ou com usuário e senha, ou opções inválidas.</exception>
    public ObservabilityBuilder AddSsoHealthCheck(string name, Uri discoveryUrl, Action<HttpHealthCheckOptions>? configure = null) =>
        AddHttpHealthCheck(name, ResolveDiscoveryUrl(discoveryUrl), configure, HealthCheckTags.Sso, requireOpenIdDiscovery: true);

    /// <summary>
    /// Health check de API externa ou microsserviço downstream: GET na URL, saudável com resposta 2xx. Falhas transitórias são
    /// repetidas antes de reportar.
    /// </summary>
    /// <param name="name">Nome da verificação no JSON.</param>
    /// <param name="uri">URL a verificar — de preferência o endpoint de health do serviço chamado.</param>
    /// <param name="configure">Timeout, tentativas, status de falha e tags.</param>
    /// <example>
    /// <code>
    /// .AddExternalServiceHealthCheck("PaymentGateway", new Uri("https://pagamentos.empresa.com/health/live"),
    ///     o => o.FailureStatus = HealthStatus.Degraded)
    /// </code>
    /// </example>
    /// <returns>O mesmo builder, para encadear.</returns>
    /// <exception cref="ArgumentException">Nome vazio ou repetido, URL não http(s) ou com usuário e senha, ou opções inválidas.</exception>
    public ObservabilityBuilder AddExternalServiceHealthCheck(string name, Uri uri, Action<HttpHealthCheckOptions>? configure = null) =>
        AddHttpHealthCheck(name, uri, configure, HealthCheckTags.External, requireOpenIdDiscovery: false);

    private ObservabilityBuilder AddHttpHealthCheck(string name, Uri uri, Action<HttpHealthCheckOptions>? configure, string kindTag, bool requireOpenIdDiscovery)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var problem = HttpUrl.Check(uri);
        if (problem == HttpUrlProblem.NotHttp)
            throw new ArgumentException($"A URL do health check '{name}' deve ser absoluta e http ou https.", nameof(uri));
        if (problem == HttpUrlProblem.HasUserInfo)
            throw new ArgumentException($"A URL do health check '{name}' não pode conter usuário e senha.", nameof(uri));

        var options = new HttpHealthCheckOptions();
        configure?.Invoke(options);
        options.Validate();
        ReserveName(name);

        string[] tags = [HealthCheckTags.Ready, kindTag, .. options.Tags];

        // Rede de segurança acima do timeout por tentativa, que é quem normalmente encerra a verificação:
        // (Timeout + 1s) × (Retries + 1). Precisa caber em HealthChecks:Timeout (conferido na validação das opções).
        var overallTimeout = _budgets.AddHttp(name, options);

        HealthChecks.Add(new HealthCheckRegistration(name,
            sp => ActivatorUtilities.CreateInstance<HttpEndpointHealthCheck>(sp, uri, options, requireOpenIdDiscovery),
            options.FailureStatus, tags, overallTimeout));
        return this;
    }

    /// <summary>Nome duplicado só falharia na primeira sondagem; aqui a falha acontece na inicialização.</summary>
    private void ReserveName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_names.Add(name))
            throw new ArgumentException($"Já existe um health check chamado '{name}'.", nameof(name));
    }

    internal static Uri ResolveDiscoveryUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!url.IsAbsoluteUri || url.AbsolutePath.Contains("/.well-known/", StringComparison.OrdinalIgnoreCase))
            return url;

        return new UriBuilder(url) { Path = url.AbsolutePath.TrimEnd('/') + "/.well-known/openid-configuration" }.Uri;
    }
}
