using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TEC.Observability.Exporters;
using TEC.Observability.Tracing;

namespace TEC.Observability.SampleApi;

/// <summary>
/// Contadores do que o pipeline de telemetria produziu. Servem para os testes de carga conferirem, sob concorrência, que cada
/// requisição de negócio gerou o seu span com o correlation id, que as sondagens de health não geraram span e que nenhum valor
/// marcado como segredo chegou à telemetria.
/// </summary>
public sealed class TelemetryCounters
{
    /// <summary>Marcador dos valores secretos enviados pelo gerador de carga (ex.: <c>?token=segredo-123</c>).</summary>
    public const string SecretMarker = "segredo-";

    private long _spansEnded;
    private long _serverSpans;
    private long _serverSpansWithoutCorrelation;
    private long _businessSpans;
    private long _clientSpans;
    private long _healthSpans;
    private long _leakedSpans;
    private long _logs;
    private long _logsWithTrace;
    private long _leakedLogs;
    private long _metricExports;
    private long _readinessExecutions;

    internal void SpanEnded() => Interlocked.Increment(ref _spansEnded);
    internal void ServerSpan(bool hasCorrelation)
    {
        Interlocked.Increment(ref _serverSpans);
        if (!hasCorrelation)
            Interlocked.Increment(ref _serverSpansWithoutCorrelation);
    }
    internal void BusinessSpan() => Interlocked.Increment(ref _businessSpans);
    internal void ClientSpan() => Interlocked.Increment(ref _clientSpans);
    internal void HealthSpan() => Interlocked.Increment(ref _healthSpans);
    internal void LeakedSpan() => Interlocked.Increment(ref _leakedSpans);
    internal void Log(bool hasTrace, bool leaked)
    {
        Interlocked.Increment(ref _logs);
        if (hasTrace)
            Interlocked.Increment(ref _logsWithTrace);
        if (leaked)
            Interlocked.Increment(ref _leakedLogs);
    }
    internal void MetricExport() => Interlocked.Increment(ref _metricExports);
    internal void ReadinessExecution() => Interlocked.Increment(ref _readinessExecutions);

    /// <summary>Foto dos contadores.</summary>
    public TelemetrySnapshot Snapshot() => new(
        Interlocked.Read(ref _spansEnded), Interlocked.Read(ref _serverSpans),
        Interlocked.Read(ref _serverSpansWithoutCorrelation), Interlocked.Read(ref _businessSpans), Interlocked.Read(ref _clientSpans),
        Interlocked.Read(ref _healthSpans), Interlocked.Read(ref _leakedSpans), Interlocked.Read(ref _logs),
        Interlocked.Read(ref _logsWithTrace), Interlocked.Read(ref _leakedLogs), Interlocked.Read(ref _metricExports),
        Interlocked.Read(ref _readinessExecutions));
}

/// <summary>Foto dos <see cref="TelemetryCounters"/> (o mesmo JSON de <c>GET /diagnostico/contadores</c>).</summary>
public sealed record TelemetrySnapshot(
    long SpansEnded,
    long ServerSpans,
    long ServerSpansWithoutCorrelation,
    long BusinessSpans,
    long ClientSpans,
    long HealthSpans,
    long LeakedSpans,
    long Logs,
    long LogsWithTrace,
    long LeakedLogs,
    long MetricExports,
    long ReadinessExecutions)
{
    /// <summary>Diferença entre duas fotos (o que aconteceu entre elas).</summary>
    public TelemetrySnapshot Minus(TelemetrySnapshot before) => new(
        SpansEnded - before.SpansEnded, ServerSpans - before.ServerSpans,
        ServerSpansWithoutCorrelation - before.ServerSpansWithoutCorrelation, BusinessSpans - before.BusinessSpans,
        ClientSpans - before.ClientSpans, HealthSpans - before.HealthSpans, LeakedSpans - before.LeakedSpans, Logs - before.Logs,
        LogsWithTrace - before.LogsWithTrace, LeakedLogs - before.LeakedLogs, MetricExports - before.MetricExports,
        ReadinessExecutions - before.ReadinessExecutions);
}

/// <summary>
/// Exportador <c>Contagem</c> (<c>Observability:Provider</c>): pipeline completo do TEC.Observability (instrumentação padrão,
/// amostragem, filtros, redação), mas em vez de enviar a telemetria para fora, só conta e confere. Mede o custo da biblioteca
/// sem medir rede ou disco.
/// </summary>
public sealed class CountingTelemetryExporter : ITelemetryExporter
{
    /// <summary>Nome usado em <c>Observability:Provider</c>.</summary>
    public const string ProviderName = "Contagem";

    public string Name => ProviderName;

    public void Configure(TelemetryExporterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.AddStandardInstrumentation().OpenTelemetry
            .WithTracing(tracing => tracing.AddProcessor(sp => new CountingActivityProcessor(sp.GetRequiredService<TelemetryCounters>(), SampleApiApp.ServiceName)))
            .WithLogging(logging => logging.AddProcessor(sp => new CountingLogProcessor(sp.GetRequiredService<TelemetryCounters>())))
            .WithMetrics(metrics => metrics.AddReader(sp =>
                new PeriodicExportingMetricReader(new CountingMetricExporter(sp.GetRequiredService<TelemetryCounters>()), exportIntervalMilliseconds: 1_000)));
    }
}

/// <remarks>
/// Só o fim do span é contado: o início (<c>OnStart</c>) acontece antes de o filtro da instrumentação descartar as rotas de
/// health, então spans iniciados e terminados nunca batem — e o que interessa é o que chega aos exportadores.
/// </remarks>
internal sealed class CountingActivityProcessor(TelemetryCounters counters, string serviceName) : BaseProcessor<Activity>
{
    public override void OnEnd(Activity data)
    {
        counters.SpanEnded();
        switch (data.Kind)
        {
            case ActivityKind.Server:
                var path = data.GetTagItem("url.path") as string ?? string.Empty;
                if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
                    counters.HealthSpan();
                counters.ServerSpan(data.GetTagItem(CorrelationId.AttributeName) is string { Length: > 0 });
                break;
            case ActivityKind.Client:
                counters.ClientSpan();
                break;
            default:
                if (data.Source.Name == serviceName)
                    counters.BusinessSpan();
                break;
        }

        if (data.DisplayName.Contains(TelemetryCounters.SecretMarker, StringComparison.OrdinalIgnoreCase)
            || data.TagObjects.Any(t => t.Value is string text && text.Contains(TelemetryCounters.SecretMarker, StringComparison.OrdinalIgnoreCase)))
        {
            counters.LeakedSpan();
        }
    }
}

internal sealed class CountingLogProcessor(TelemetryCounters counters) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        // Tudo o que um exportador escreve: mensagem, corpo, exceção, atributos e escopos
        var leaked = Leaks(data.FormattedMessage) || Leaks(data.Body) || Leaks(data.Exception?.ToString())
            || (data.Attributes?.Any(a => Leaks(Text(a.Value))) ?? false);
        if (!leaked)
        {
            var scopes = new ScopeState();
            data.ForEachScope(static (scope, state) =>
            {
                foreach (var item in scope)
                    state.Leaked |= Leaks(Text(item.Value));
            }, scopes);
            leaked = scopes.Leaked;
        }

        counters.Log(data.TraceId != default, leaked);
    }

    private static string? Text(object? value) => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);

    private static bool Leaks(string? text) =>
        text is not null && text.Contains(TelemetryCounters.SecretMarker, StringComparison.OrdinalIgnoreCase);

    private sealed class ScopeState
    {
        public bool Leaked;
    }
}

internal sealed class CountingMetricExporter(TelemetryCounters counters) : BaseExporter<Metric>
{
    public override ExportResult Export(in Batch<Metric> batch)
    {
        counters.MetricExport();
        return ExportResult.Success;
    }
}
