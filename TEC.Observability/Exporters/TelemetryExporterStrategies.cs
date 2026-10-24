using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TEC.Observability.Configuration;

namespace TEC.Observability.Exporters;

/// <summary>
/// Exportadores do pacote base e acesso às opções efetivas. Os valores (endereço, cabeçalhos, amostragem) são lidos de
/// <c>IOptions&lt;ObservabilityOptions&gt;</c> quando o provedor de telemetria é construído, e não no registro: provedores de
/// configuração adicionados depois do <c>AddTecObservability</c> valem.
/// </summary>
internal static class TelemetryExporterStrategies
{
    /// <summary>Exportador embutido do provedor, ou <c>null</c> para <c>None</c> e para provedores de exportadores registrados.</summary>
    public static ITelemetryExporter? BuiltIn(string provider) =>
        ObservabilityProvider.Is(provider, ObservabilityProvider.Otlp) ? new OtlpTelemetryExporter()
        : ObservabilityProvider.Is(provider, ObservabilityProvider.Console) ? new ConsoleTelemetryExporter()
        : ObservabilityProvider.Is(provider, ObservabilityProvider.LogFile) ? new LogFileTelemetryExporter()
        : null;

    /// <summary>Opções efetivas da biblioteca.</summary>
    public static ObservabilityOptions Options(this IServiceProvider services) =>
        services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
}

/// <summary>
/// Destinos configurados no registro e exportadores registrados e ligados. Fica no container para a validação na subida apontar o
/// exportador que falta (ex.: <c>Provider: Azure</c> sem o pacote TEC.Observability.Azure).
/// </summary>
internal sealed class TelemetryExporterRegistry(IReadOnlyList<string> registeredProviders)
{
    private readonly List<string> _available = [];
    private readonly List<ITelemetryExporter> _active = [];

    /// <summary>Destinos do Provider da configuração disponível no <c>AddTecObservability</c>.</summary>
    public IReadOnlyList<string> RegisteredProviders { get; } = registeredProviders;

    /// <summary>Exportadores ligados (vazio com <c>None</c>, ou enquanto os exportadores do Provider não foram registrados).</summary>
    public IReadOnlyList<ITelemetryExporter> Active => _active;

    /// <summary>Nomes dos exportadores registrados com <c>AddExporter</c>.</summary>
    public IReadOnlyList<string> Available => _available;

    /// <summary>Pipeline comum do OpenTelemetry, montado uma vez para todos os exportadores ligados.</summary>
    internal OpenTelemetryBuilder? Pipeline { get; set; }

    /// <summary>Instrumentação padrão já registrada por algum exportador (não é repetida pelos seguintes).</summary>
    internal bool HasStandardInstrumentation { get; set; }

    /// <summary>Destinos configurados que não são embutidos e ainda não têm exportador ligado.</summary>
    public IEnumerable<string> Missing =>
        RegisteredProviders.Where(p => !ObservabilityProvider.IsBuiltIn(p) && !_active.Exists(a => ObservabilityProvider.Is(a.Name, p)));

    /// <summary><c>true</c> quando <paramref name="provider"/> tem os mesmos destinos do registro, em qualquer ordem.</summary>
    public bool Matches(string? provider)
    {
        var current = ObservabilityProvider.Parse(provider);
        return current.Count == RegisteredProviders.Count
            && current.All(c => RegisteredProviders.Any(r => ObservabilityProvider.Is(r, c)));
    }

    /// <summary>Registra o nome; <c>false</c> se já existir um exportador com ele.</summary>
    public bool Register(string name)
    {
        if (_available.Exists(n => ObservabilityProvider.Is(n, name)))
            return false;

        _available.Add(name);
        return true;
    }

    /// <summary><c>true</c> quando o exportador é um dos destinos configurados e ainda não foi ligado.</summary>
    public bool Selects(ITelemetryExporter exporter) =>
        RegisteredProviders.Any(p => ObservabilityProvider.Is(p, exporter.Name)) && !IsActive(exporter.Name);

    public void Activate(ITelemetryExporter exporter)
    {
        if (IsActive(exporter.Name))
            throw new InvalidOperationException($"O exportador '{exporter.Name}' já está ligado.");
        _active.Add(exporter);
    }

    private bool IsActive(string name) => _active.Exists(a => ObservabilityProvider.Is(a.Name, name));
}

/// <summary>
/// OTLP para AWS ADOT, GCP OTel Collector, Jaeger ou qualquer coletor compatível. Sem <c>Otlp:Endpoint</c> (nem
/// <c>Otlp:Headers</c>), valem as variáveis padrão <c>OTEL_EXPORTER_OTLP_*</c>, lidas pelo próprio SDK.
/// </summary>
internal sealed class OtlpTelemetryExporter : ITelemetryExporter
{
    /// <summary>
    /// Endereço base configurado, por instância de opções do exportador. A parte comum (protocolo, endereço, cabeçalhos) é
    /// aplicada pelas opções nomeadas, com acesso ao container; o caminho de cada sinal é acrescentado depois, no callback do
    /// exportador, que não tem acesso ao container.
    /// </summary>
    private static readonly ConditionalWeakTable<OtlpExporterOptions, Uri> BaseEndpoints = [];

    public string Name => ObservabilityProvider.Otlp;

    public void Configure(TelemetryExporterContext context)
    {
        // Mesmo nome de opções (o padrão) que o SDK usa: ajustes do serviço em Configure<OtlpExporterOptions> continuam valendo.
        context.Services.AddOptions<OtlpExporterOptions>().Configure<IServiceProvider>((exporter, services) =>
            ApplyShared(exporter, services.Options().Otlp));

        context.AddStandardInstrumentation().OpenTelemetry
            .WithTracing(tracing => tracing.AddOtlpExporter(o => ApplySignal(o, "v1/traces")))
            .WithMetrics(metrics => metrics.AddOtlpExporter(o => ApplySignal(o, "v1/metrics")))
            .WithLogging(logging => logging.AddOtlpExporter(o => ApplySignal(o, "v1/logs")));
    }

    internal static void ApplyShared(OtlpExporterOptions exporter, OtlpExporterSettings settings)
    {
        if (settings.Endpoint is { } endpoint)
        {
            exporter.Protocol = settings.Protocol == OtlpProtocol.Grpc ? OtlpExportProtocol.Grpc : OtlpExportProtocol.HttpProtobuf;
            exporter.Endpoint = endpoint;
            BaseEndpoints.AddOrUpdate(exporter, endpoint);
        }

        if (settings.Headers.Count > 0)
            exporter.Headers = string.Join(',', settings.Headers.Select(h => $"{h.Key}={Uri.EscapeDataString(h.Value)}"));
    }

    private static void ApplySignal(OtlpExporterOptions exporter, string signalPath)
    {
        // Endereço vindo de OTEL_EXPORTER_OTLP_ENDPOINT: o SDK já acrescenta o caminho do sinal.
        if (BaseEndpoints.TryGetValue(exporter, out var endpoint))
        {
            var protocol = exporter.Protocol == OtlpExportProtocol.Grpc ? OtlpProtocol.Grpc : OtlpProtocol.HttpProtobuf;
            exporter.Endpoint = ResolveEndpoint(endpoint, protocol, signalPath);
        }
    }

    /// <summary>
    /// Em HTTP cada sinal tem o seu caminho (<c>/v1/traces</c> etc.). Com o endpoint definido em código o SDK não o acrescenta,
    /// então a configuração informa só o endereço base e o caminho é montado aqui.
    /// </summary>
    internal static Uri ResolveEndpoint(Uri endpoint, OtlpProtocol protocol, string signalPath)
    {
        if (protocol == OtlpProtocol.Grpc)
            return endpoint;

        var text = endpoint.AbsoluteUri;
        return new Uri(text.EndsWith('/') ? text + signalPath : $"{text}/{signalPath}");
    }
}

/// <summary>
/// Console, para desenvolvimento local: traces, métricas e logs no formato detalhado do exportador de console do OpenTelemetry
/// (vários campos por registro). Os logs do console padrão do .NET (<c>Logging:Console</c>) continuam, independentes deste.
/// </summary>
internal sealed class ConsoleTelemetryExporter : ITelemetryExporter
{
    public string Name => ObservabilityProvider.Console;

    public void Configure(TelemetryExporterContext context) =>
        context.AddStandardInstrumentation().OpenTelemetry
            .WithTracing(tracing => tracing.AddConsoleExporter())
            .WithMetrics(metrics => metrics.AddConsoleExporter())
            .WithLogging(logging => logging.AddConsoleExporter());
}
