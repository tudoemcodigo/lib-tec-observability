using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace TEC.Observability.Configuration;

/// <summary>
/// Opções do exportador do Azure Monitor, ligadas à seção <c>Observability:Azure</c> (usadas quando <c>Observability:Provider</c>
/// é <c>Azure</c>). Ajustes em código: <c>.AddAzureMonitorExporter(o =&gt; ...)</c> ou <c>services.Configure&lt;AzureMonitorExporterOptions&gt;</c>.
/// </summary>
public sealed class AzureMonitorExporterOptions
{
    /// <summary>Nome da seção de configuração.</summary>
    public const string SectionName = ObservabilityOptions.SectionName + ":Azure";

    /// <summary>
    /// Connection string do Application Insights. Pode ficar fora do <c>appsettings.json</c> se a variável
    /// <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> estiver definida.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Autentica a ingestão com Entra ID em vez da chave da connection string. Fora do ambiente <c>Development</c> só são aceitas
    /// identidades de carga de trabalho (Workload Identity e Managed Identity); em <c>Development</c> vale o
    /// <c>DefaultAzureCredential</c>, que inclui as credenciais do desenvolvedor (<c>az login</c>, Visual Studio).
    /// </summary>
    public bool UseManagedIdentity { get; set; }

    /// <summary>Client id da identidade atribuída pelo usuário. Vazio usa a identidade atribuída pelo sistema.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>Liga ou desliga o Live Metrics do Application Insights. Vazio mantém o padrão da distro (ligado).</summary>
    public bool? EnableLiveMetrics { get; set; }

    /// <summary>
    /// Desliga o armazenamento em disco da telemetria que falhou ao ser enviada (reenvio posterior). Vazio mantém o padrão da
    /// distro (armazenamento ligado). Útil em contêineres com sistema de arquivos somente leitura.
    /// </summary>
    public bool? DisableOfflineStorage { get; set; }
}

/// <summary>Validação das opções do Azure Monitor, executada na subida do host (<c>ValidateOnStart</c>) e a cada leitura.</summary>
internal sealed class AzureMonitorExporterOptionsValidator(IConfiguration configuration) : IValidateOptions<AzureMonitorExporterOptions>
{
    internal const string ConnectionStringVariable = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    public ValidateOptionsResult Validate(string? name, AzureMonitorExporterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString) && string.IsNullOrWhiteSpace(configuration[ConnectionStringVariable]))
            errors.Add($"Azure:ConnectionString é obrigatório (ou a variável {ConnectionStringVariable}), inclusive com UseManagedIdentity.");
        if (!string.IsNullOrWhiteSpace(options.ManagedIdentityClientId) && !Guid.TryParse(options.ManagedIdentityClientId, out _))
            errors.Add("Azure:ManagedIdentityClientId deve ser um GUID.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Configuração inválida na seção '{ObservabilityOptions.SectionName}': {string.Join(" ", errors)}");
    }
}
