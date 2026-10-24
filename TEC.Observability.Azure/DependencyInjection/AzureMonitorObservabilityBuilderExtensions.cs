using TEC.Observability.Configuration;
using TEC.Observability.Exporters;

namespace TEC.Observability.DependencyInjection;

/// <summary>Registro do exportador do Azure Monitor no <see cref="ObservabilityBuilder"/>.</summary>
public static class AzureMonitorObservabilityBuilderExtensions
{
    /// <summary>
    /// Registra o exportador do Azure Monitor / Application Insights (distro oficial do OpenTelemetry), com as opções da seção
    /// <c>Observability:Azure</c>. Só é ligado quando <c>Observability:Provider</c> é <c>Azure</c>; com outro provedor a chamada
    /// não tem efeito, então o mesmo <c>Program.cs</c> serve para todos os ambientes.
    /// </summary>
    /// <param name="builder">Retorno do <c>AddTecObservability</c>.</param>
    /// <param name="configure">Ajustes em código, aplicados depois da seção <c>Observability:Azure</c>.</param>
    /// <returns>O mesmo builder, para encadear.</returns>
    /// <exception cref="InvalidOperationException">Chamado mais de uma vez.</exception>
    /// <exception cref="Microsoft.Extensions.Options.OptionsValidationException">
    /// Sem connection string (nem a variável <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c>) ou com <c>ManagedIdentityClientId</c>
    /// que não é GUID, na subida do host.
    /// </exception>
    /// <example>
    /// <code>
    /// // appsettings.json: "Observability": { "Provider": "Azure", "Azure": { "ConnectionString": "..." } }
    /// builder.Services.AddTecObservability(builder.Configuration)
    ///     .AddAzureMonitorExporter();
    /// </code>
    /// </example>
    public static ObservabilityBuilder AddAzureMonitorExporter(this ObservabilityBuilder builder,
        Action<AzureMonitorExporterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddExporter(new AzureMonitorTelemetryExporter(configure));
    }
}
