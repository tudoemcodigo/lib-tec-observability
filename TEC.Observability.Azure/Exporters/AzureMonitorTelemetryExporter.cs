using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TEC.Observability.Configuration;

namespace TEC.Observability.Exporters;

/// <summary>
/// Azure Monitor. A distro da Microsoft já registra a instrumentação de ASP.NET Core e HttpClient, o sampler do
/// Application Insights e os três sinais; aqui só entram conexão, credencial, taxa de amostragem e as opções da distro expostas.
/// Os valores são lidos quando o provedor de telemetria é construído: provedores de configuração adicionados depois do
/// <c>AddTecObservability</c> (ex.: cofre com a connection string) valem.
/// </summary>
internal sealed class AzureMonitorTelemetryExporter(Action<AzureMonitorExporterOptions>? configure) : ITelemetryExporter
{
    public string Name => ObservabilityProvider.Azure;

    public void Configure(TelemetryExporterContext context)
    {
        var options = context.Services.AddOptions<AzureMonitorExporterOptions>()
            .Bind(context.Section.GetSection("Azure"))
            .ValidateOnStart();
        if (configure is not null)
            options.Configure(configure);
        context.Services.AddSingleton<IValidateOptions<AzureMonitorExporterOptions>>(
            new AzureMonitorExporterOptionsValidator(context.Configuration));

        context.OpenTelemetry.UseAzureMonitor();

        context.Services.AddOptions<AzureMonitorOptions>().Configure<IServiceProvider>((azure, services) =>
        {
            var observability = services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
            var settings = services.GetRequiredService<IOptions<AzureMonitorExporterOptions>>().Value;

            if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
                azure.ConnectionString = settings.ConnectionString;
            if (settings.UseManagedIdentity)
                azure.Credential = CreateCredential(settings, services.GetService<IHostEnvironment>()?.IsDevelopment() == true);
            if (settings.EnableLiveMetrics is { } liveMetrics)
                azure.EnableLiveMetrics = liveMetrics;
            if (settings.DisableOfflineStorage is { } disableOfflineStorage)
                azure.DisableOfflineStorage = disableOfflineStorage;

            // Amostragem por fração fixa, como nos demais provedores (o padrão da distro é limitar por traces/segundo).
            azure.TracesPerSecond = null;
            azure.SamplingRatio = (float)observability.SamplingRatio;
        });
    }

    /// <summary>
    /// Fora de desenvolvimento a cadeia é fechada em identidades de carga de trabalho: o <c>DefaultAzureCredential</c> também
    /// aceitaria segredo em variável de ambiente e credenciais de ferramentas de desenvolvedor presentes na máquina.
    /// </summary>
    internal static TokenCredential CreateCredential(AzureMonitorExporterOptions options, bool development)
    {
        var clientId = string.IsNullOrWhiteSpace(options.ManagedIdentityClientId) ? null : options.ManagedIdentityClientId;

        if (development)
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions { ManagedIdentityClientId = clientId });

        var workloadOptions = new WorkloadIdentityCredentialOptions();
        if (clientId is not null)
            workloadOptions.ClientId = clientId;

        return new ChainedTokenCredential(
            new WorkloadIdentityCredential(workloadOptions),
            new ManagedIdentityCredential(clientId is null
                ? ManagedIdentityId.SystemAssigned
                : ManagedIdentityId.FromUserAssignedClientId(clientId)));
    }
}
