using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace TEC.Observability.Configuration;

/// <summary>Binding da seção <c>Observability</c> e valores padrão que dependem do ambiente.</summary>
internal static class ObservabilityOptionsSetup
{
    /// <summary>Variável padrão do OpenTelemetry com o nome do serviço (definida, por exemplo, pelo .NET Aspire).</summary>
    internal const string ServiceNameVariable = "OTEL_SERVICE_NAME";

    private static readonly Lazy<string?> EntryAssemblyVersion = new(ReadEntryAssemblyVersion);

    /// <summary>
    /// Liga a seção às opções. O binder acrescenta itens a coleções já preenchidas; aqui a lista de <c>IgnoredPaths</c> do
    /// <c>appsettings.json</c> substitui a padrão.
    /// </summary>
    public static void Bind(IConfiguration section, ObservabilityOptions options)
    {
        var defaultIgnoredPaths = options.IgnoredPaths;
        options.IgnoredPaths = [];
        section.Bind(options);
        if (options.IgnoredPaths is { Count: 0 } && !section.GetSection(nameof(ObservabilityOptions.IgnoredPaths)).Exists())
            options.IgnoredPaths = defaultIgnoredPaths;
    }

    /// <summary>
    /// Preenche o que ficou vazio depois da configuração e dos ajustes em código: <c>Provider</c> normalizado (lista separada
    /// por <c>", "</c>; vazio vira <c>None</c>), nome do serviço pela variável
    /// <c>OTEL_SERVICE_NAME</c>, versão pelo assembly de entrada e, quando o ambiente é conhecido, <c>ExposeDetails</c>
    /// (ligado só em <c>Development</c>).
    /// </summary>
    public static void ApplyDefaults(ObservabilityOptions options, IConfiguration configuration, bool? isDevelopment)
    {
        options.Provider = string.Join(", ", ObservabilityProvider.Parse(options.Provider));
        if (string.IsNullOrWhiteSpace(options.ServiceName) && configuration[ServiceNameVariable] is { Length: > 0 } serviceName)
            options.ServiceName = serviceName.Trim();
        if (string.IsNullOrWhiteSpace(options.ServiceVersion) && EntryAssemblyVersion.Value is { } version)
            options.ServiceVersion = version;
        if (isDevelopment is { } development && options.HealthChecks is { } healthChecks)
            healthChecks.ExposeDetails ??= development;
    }

    /// <summary>Opções lidas da configuração disponível no momento do registro (só para o que precisa ser decidido ali).</summary>
    public static ObservabilityOptions CreateSnapshot(IConfiguration configuration, Action<ObservabilityOptions>? configure)
    {
        var options = new ObservabilityOptions();
        Bind(configuration.GetSection(ObservabilityOptions.SectionName), options);
        configure?.Invoke(options);
        ApplyDefaults(options, configuration, isDevelopment: null);
        return options;
    }

    private static string? ReadEntryAssemblyVersion()
    {
        var assembly = Assembly.GetEntryAssembly();
        if (assembly is null)
            return null;

        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(informational) ? assembly.GetName().Version?.ToString() : informational;
    }
}
