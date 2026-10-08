using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using TEC.Vault.AzureKeyVault;
using TEC.Vault.DependencyInjection;

namespace TEC.Observability.Tests.Integration;

/// <summary>
/// Acesso ao Application Insights real de testes. Sem connection string (ou sem credencial, nos testes que precisam dela),
/// os testes de integração são pulados, não falham.
/// </summary>
/// <remarks>
/// <para>
/// Configuração (ver <see cref="TestSettings"/>): variável de ambiente ou, na máquina do desenvolvedor, <c>dotnet user-secrets</c>
/// (id <c>tudoemcodigo-tec-testes</c>) e o arquivo <c>appsettings.Local.json</c> (ignorado pelo git), nesta ordem:
/// </para>
/// <list type="bullet">
/// <item>Connection string: primeiro o segredo <see cref="SecretName"/> (padrão <c>tec-testes-appinsights-conexao</c>;
/// <c>TEC_TESTES_OBSERVABILITY_APPINSIGHTS_SEGREDO</c> / <c>TecTestes:AppInsightsSegredo</c>) do Key Vault de testes
/// (<c>TEC_TESTES_VAULT_URI</c> / <c>TecTestes:VaultUri</c>, sem valor padrão), lido pelo TEC.Vault.AzureKeyVault com a mesma
/// <see cref="Credential"/> dos testes; com o cofre offline, não configurado ou sem o segredo, a connection string local em
/// <c>TEC_TESTES_OBSERVABILITY_APPINSIGHTS_CONNECTION_STRING</c> / <c>ApplicationInsights:ConnectionString</c> (user-secrets).</item>
/// <item>Tenant do Entra ID usado para obter tokens: <c>TEC_TESTES_TENANT_ID</c> /
/// <c>TecTestes:TenantId</c> / <c>ApplicationInsights:TenantId</c>.</item>
/// <item><c>cloud_RoleName</c> da telemetria: <c>TEC_TESTES_OBSERVABILITY_SERVICE_NAME</c> / <c>TecTestes:ServiceName</c>, padrão
/// <c>tec-testes-observability</c>.</item>
/// <item><c>TEC_TESTES_OBSERVABILITY_IDENTIDADE=workload</c>: usa Workload/Managed Identity (runner dentro do Azure) em vez das
/// credenciais de desenvolvedor (<c>az login</c>, azd, Visual Studio, Azure PowerShell).</item>
/// </list>
/// <para>
/// Papéis da identidade no recurso: <b>Monitoring Reader</b> (consultar os dados) e <b>Monitoring Metrics Publisher</b>
/// (ingestão autenticada por Entra ID); no cofre de testes, <b>Key Vault Secrets User</b> (ler a connection string).
/// </para>
/// </remarks>
internal static class ApplicationInsightsFixture
{
    public const string ConnectionStringVariable = "TEC_TESTES_OBSERVABILITY_APPINSIGHTS_CONNECTION_STRING";

    /// <summary>Nome padrão do segredo da connection string no Key Vault de testes.</summary>
    public const string DefaultSecretName = "tec-testes-appinsights-conexao";

    /// <summary>Nome padrão do serviço (<c>cloud_RoleName</c>) da telemetria gerada pelos testes.</summary>
    public const string DefaultServiceName = "tec-testes-observability";

    /// <summary>Escopo do token de ingestão do Azure Monitor.</summary>
    public const string IngestionScope = "https://monitor.azure.com//.default";

    /// <summary>Escopo do token da API de consulta do Application Insights.</summary>
    public const string QueryScope = "https://api.applicationinsights.io/.default";

    /// <summary><c>cloud_RoleName</c> da telemetria gerada pelos testes.</summary>
    public static readonly string ServiceName = TestSettings.Read(null, "TEC_TESTES_OBSERVABILITY_SERVICE_NAME", "ServiceName") ?? DefaultServiceName;

    /// <summary>Nome do segredo da connection string no Key Vault de testes.</summary>
    public static readonly string SecretName =
        TestSettings.Read(null, "TEC_TESTES_OBSERVABILITY_APPINSIGHTS_SEGREDO", "AppInsightsSegredo") ?? DefaultSecretName;

    public static readonly string? TenantId = TestSettings.Read(
        [TestSettings.TenantIdVariable],
        [$"{TestSettings.Section}:TenantId", "ApplicationInsights:TenantId"]);

    /// <summary>Connection string local (variável, user-secrets ou appsettings.Local.json): usada com o cofre offline.</summary>
    private static readonly string? LocalConnectionString =
        TestSettings.Read([ConnectionStringVariable], ["ApplicationInsights:ConnectionString"]);

    /// <summary><c>true</c> quando os testes rodam dentro do Azure com Workload/Managed Identity.</summary>
    public static readonly bool WorkloadIdentity =
        string.Equals(Environment.GetEnvironmentVariable("TEC_TESTES_OBSERVABILITY_IDENTIDADE"), "workload", StringComparison.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> Unavailable = new();

    /// <summary>Endpoint de ingestão da connection string (usado como dependência HTTP real nos testes).</summary>
    public static Uri IngestionEndpoint => new(Part("IngestionEndpoint"));

    /// <summary>Id do aplicativo na API de consulta.</summary>
    public static string ApplicationId => Part("ApplicationId");

    /// <summary>Credencial dos testes: identidade de carga de trabalho dentro do Azure, ou a do desenvolvedor.</summary>
    public static TokenCredential Credential { get; } = WorkloadIdentity
        ? new ChainedTokenCredential(new WorkloadIdentityCredential(), new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned))
        : new ChainedTokenCredential(
            new AzureCliCredential(new AzureCliCredentialOptions { TenantId = TenantId }),
            new AzureDeveloperCliCredential(new AzureDeveloperCliCredentialOptions { TenantId = TenantId }),
            new VisualStudioCredential(new VisualStudioCredentialOptions { TenantId = TenantId }),
            new AzurePowerShellCredential(new AzurePowerShellCredentialOptions { TenantId = TenantId }));

    /// <summary>Connection string resolvida uma vez por execução (segredo do cofre ou fonte local) ou o motivo para pular.</summary>
    private static readonly Lazy<Task<(string? Value, string? SkipReason)>> Connection = new(ResolveConnectionAsync);

    /// <summary>Connection string do Application Insights de testes (disponível depois de <see cref="RequireAsync"/>).</summary>
    public static string ConnectionString => Connection.Value is { IsCompletedSuccessfully: true } task && task.Result.Value is { } value
        ? value
        : throw new InvalidOperationException("Chame RequireAsync antes de usar a connection string.");

    /// <summary>Pula o teste se não houver Application Insights configurado.</summary>
    public static async Task RequireAsync()
    {
        var (_, reason) = await Connection.Value;
        Skip.When(reason is not null, reason ?? string.Empty);
    }

    /// <summary>Pula o teste se, além do recurso, não houver credencial do Entra ID capaz de obter token para o escopo.</summary>
    public static async Task RequireCredentialAsync(string scope)
    {
        await RequireAsync();
        var reason = await Unavailable.GetOrAdd(scope, s => new Lazy<Task<string?>>(() => CheckAsync(s))).Value;
        Skip.When(reason is not null, reason ?? string.Empty);
    }

    /// <summary>Executa uma consulta KQL que devolve uma única linha e a entrega como coluna → valor.</summary>
    public static async Task<IReadOnlyDictionary<string, long>> QueryRowAsync(HttpClient client, string kql, CancellationToken cancellationToken = default)
    {
        var token = await Credential.GetTokenAsync(new TokenRequestContext([QueryScope], tenantId: TenantId), cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.applicationinsights.io/v1/apps/{ApplicationId}/query");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = JsonContent.Create(new { query = kql });

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Consulta ao Application Insights falhou (HTTP {(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        var table = document.RootElement.GetProperty("tables")[0];
        var columns = table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToArray();
        var row = table.GetProperty("rows")[0];

        return columns.Select((name, index) => (name, value: row[index]))
            .ToDictionary(c => c.name, c => c.value.ValueKind == JsonValueKind.Number ? c.value.GetInt64() : 0L);
    }

    private static async Task<string?> CheckAsync(string scope)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await Credential.GetTokenAsync(new TokenRequestContext([scope], tenantId: TenantId), timeout.Token);
            return null;
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException or OperationCanceledException)
        {
            return WorkloadIdentity
                ? $"Sem Workload/Managed Identity disponível para {scope}."
                : $"Sem credencial de desenvolvedor para {scope} (execute 'az login --tenant <tenant>').";
        }
    }

    /// <summary>
    /// Connection string: o segredo <see cref="SecretName"/> do Key Vault de testes, lido pelo TEC.Vault.AzureKeyVault (como em
    /// produção) com a mesma <see cref="Credential"/> dos testes; com o cofre offline, não configurado ou sem o segredo, a fonte
    /// local (<see cref="LocalConnectionString"/>).
    /// </summary>
    private static async Task<(string? Value, string? SkipReason)> ResolveConnectionAsync()
    {
        var (vaultValue, reason) = await ReadFromVaultAsync();
        if (vaultValue is not null)
            return (vaultValue, null);

        // Cofre offline, não configurado ou sem o segredo: connection string local (variável, user-secrets ou appsettings.Local.json)
        if (LocalConnectionString is not null)
            return (LocalConnectionString, null);

        return (null, $"Sem Application Insights de testes (Key Vault: {reason} Connection string local: defina {ConnectionStringVariable} " +
                      $"ou 'dotnet user-secrets set ApplicationInsights:ConnectionString \"<connection string>\" --id {TestSettings.UserSecretsId}').");
    }

    /// <summary>Segredo <see cref="SecretName"/> do Key Vault de testes, ou <c>null</c> com o motivo (cofre offline, não configurado...).</summary>
    private static async Task<(string? Value, string Reason)> ReadFromVaultAsync()
    {
        var vaultUri = TestSettings.VaultUri(null, out string vaultReason);
        if (vaultUri is null)
            return (null, vaultReason);

        try
        {
            var store = AzureKeyVaultStores.CreateSecretStore(options =>
            {
                options.VaultUri = vaultUri;
                options.Credential = Credential;
                options.TenantId = TenantId;
                options.Stores = VaultStores.Secrets;   // menor privilégio: só leitura de segredos
            });
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var secret = await store.GetSecretAsync(SecretName, cancellationToken: timeout.Token);
            if (secret.IsSuccess && !string.IsNullOrWhiteSpace(secret.Value.Value))
                return (secret.Value.Value, string.Empty);
            return (null, secret.IsSuccess
                ? $"segredo {SecretName} vazio em {vaultUri.Host}."
                : $"segredo {SecretName} indisponível em {vaultUri.Host} ({secret.Error!.Code}; confira o segredo e a credencial: 'az login').");
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or ArgumentException
                                              or AuthenticationFailedException or CredentialUnavailableException)
        {
            return (null, $"{vaultUri.Host} inacessível ({exception.GetType().Name}).");
        }
    }

    private static string Part(string name) => ConnectionString
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .First(part => part.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))[(name.Length + 1)..];
}

/// <summary>
/// Escuta os diagnósticos internos do exportador do Azure Monitor. A exportação acontece em segundo plano e não lança exceção
/// para a aplicação: é por estes eventos que se sabe se a ingestão aceitou o envio (<c>TransmissionSuccess</c>) ou se ele falhou.
/// </summary>
internal sealed class AzureMonitorExporterListener : EventListener
{
    // Falha interna do SDK ao registrar as estatísticas de uso do próprio exportador; não afeta o envio da telemetria.
    private const string IgnoredEvent = "CustomerSdkStatsInitializationFailed";

    private readonly List<string> _problems = [];
    private readonly HashSet<string> _transmitted = [];

    /// <summary>Eventos de nível <c>Warning</c> ou mais grave.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_problems)
                return [.. _problems];
        }
    }

    /// <summary>Exportadores cujo envio foi aceito pela ingestão, ex.: <c>AzureMonitorTraceExporter</c>.</summary>
    public IReadOnlyCollection<string> Transmitted
    {
        get
        {
            lock (_problems)
                return [.. _transmitted];
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name.StartsWith("OpenTelemetry-AzureMonitor", StringComparison.Ordinal))
            EnableEvents(eventSource, EventLevel.Verbose);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        lock (_problems)
        {
            if (eventData.EventName == "TransmissionSuccess" && eventData.Payload is [string exporter, ..])
                _transmitted.Add(exporter);
            else if (eventData.Level <= EventLevel.Warning && eventData.EventName != IgnoredEvent)
                _problems.Add($"[{eventData.Level}] {eventData.EventSource.Name}/{eventData.EventName}: {string.Join(" | ", (IEnumerable<object?>?)eventData.Payload ?? [])}");
        }
    }
}
