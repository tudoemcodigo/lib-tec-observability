using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TEC.Observability.Configuration;

namespace TEC.Observability.Exporters;

/// <summary>
/// Exportador de telemetria plugável: liga um backend ao pipeline do OpenTelemetry montado pela biblioteca. É escolhido pelo
/// nome em <c>Observability:Provider</c>: registre-o com <c>AddExporter</c> no retorno do <c>AddTecObservability</c>, e
/// ele só é ligado quando <see cref="Name"/> está entre os destinos configurados (registrar e não usar não tem efeito). Vários
/// destinos ligados juntos (<c>"Otlp, LogFile"</c>) compartilham o mesmo pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <c>None</c>, <c>Otlp</c>, <c>Console</c> e <c>LogFile</c> vêm no pacote base; o Azure Monitor vem no pacote <c>TEC.Observability.Azure</c>
/// (<c>.AddAzureMonitorExporter()</c>), que implementa esta mesma interface.
/// </para>
/// <para>
/// Leia endereços, credenciais e demais valores quando o provedor de telemetria for construído (callbacks com
/// <see cref="IServiceProvider"/>, <c>IOptions</c>), e não em <see cref="Configure"/>: assim provedores de configuração adicionados
/// depois do registro (ex.: cofre de segredos) valem.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class ArquivoTelemetryExporter : ITelemetryExporter
/// {
///     public string Name => "Arquivo";
///
///     public void Configure(TelemetryExporterContext context) =>
///         context.AddStandardInstrumentation().OpenTelemetry
///             .WithTracing(tracing => tracing.AddProcessor(new BatchActivityExportProcessor(new MeuExportador())));
/// }
///
/// // Program.cs ("Observability:Provider": "Arquivo")
/// builder.Services.AddTecObservability(builder.Configuration)
///     .AddExporter(new ArquivoTelemetryExporter());
/// </code>
/// </example>
public interface ITelemetryExporter
{
    /// <summary>
    /// Nome usado em <c>Observability:Provider</c> (sem diferenciar maiúsculas; letras, dígitos, <c>.</c>, <c>-</c> e <c>_</c>).
    /// Não pode ser um dos embutidos (<c>None</c>, <c>Otlp</c>, <c>Console</c>, <c>LogFile</c>).
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Liga instrumentação, amostragem e exportadores do backend. Chamado uma única vez, no registro, e só quando
    /// <see cref="Name"/> está entre os destinos configurados.
    /// </summary>
    /// <param name="context">Pipeline do OpenTelemetry e configuração.</param>
    void Configure(TelemetryExporterContext context);
}

/// <summary>
/// O que um <see cref="ITelemetryExporter"/> recebe: o pipeline do OpenTelemetry já com o recurso do serviço (<c>service.*</c>,
/// <c>deployment.environment</c>), métricas de runtime, logs, filtros de rota e o registro das fontes do serviço e das
/// bibliotecas TEC.
/// </summary>
public sealed class TelemetryExporterContext
{
    private readonly TelemetryExporterRegistry _exporters;

    internal TelemetryExporterContext(OpenTelemetryBuilder openTelemetry, IConfiguration configuration, TelemetryExporterRegistry exporters)
    {
        OpenTelemetry = openTelemetry;
        Configuration = configuration;
        Section = configuration.GetSection(ObservabilityOptions.SectionName);
        _exporters = exporters;
    }

    /// <summary>Builder do OpenTelemetry (<c>services.AddOpenTelemetry()</c>) em que o exportador se liga.</summary>
    public OpenTelemetryBuilder OpenTelemetry { get; }

    /// <summary>Container de injeção de dependência.</summary>
    public IServiceCollection Services => OpenTelemetry.Services;

    /// <summary>Configuração passada ao <c>AddTecObservability</c>.</summary>
    public IConfiguration Configuration { get; }

    /// <summary>Seção <c>Observability</c>; as opções de cada exportador ficam numa subseção (ex.: <c>Observability:Azure</c>).</summary>
    public IConfigurationSection Section { get; }

    /// <summary>
    /// Instrumentação padrão para backends que não trazem a sua: ASP.NET Core e HttpClient (traces e métricas) e amostragem
    /// <c>ParentBased(TraceIdRatio(SamplingRatio))</c>, que respeita a decisão do serviço chamador. Usada pelos exportadores OTLP
    /// e console; não chame quando o backend já registra a sua (como a distro do Azure Monitor). Com vários destinos no
    /// <c>Provider</c>, só a primeira chamada registra: as seguintes não têm efeito.
    /// </summary>
    /// <returns>O mesmo contexto, para encadear.</returns>
    public TelemetryExporterContext AddStandardInstrumentation()
    {
        if (_exporters.HasStandardInstrumentation)
            return this;

        _exporters.HasStandardInstrumentation = true;
        OpenTelemetry
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .SetSampler(services => new ParentBasedSampler(new TraceIdRatioBasedSampler(services.Options().SamplingRatio))))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation());
        return this;
    }
}
