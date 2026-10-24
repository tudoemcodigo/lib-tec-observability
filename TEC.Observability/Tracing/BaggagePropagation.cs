using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace TEC.Observability.Tracing;

/// <summary>
/// Hosts que podem receber o cabeçalho <c>baggage</c> nas chamadas HTTP de saída (opção <c>BaggageAllowedHosts</c>).
/// Vale para a requisição em andamento: o <see cref="CorrelationIdMiddleware"/> a define no início de cada requisição. Fora
/// dela (serviços em segundo plano, middlewares anteriores ao de correlation id) vale a política do processo, e, sem nenhuma
/// configurada, nenhum host recebe o Baggage.
/// </summary>
internal sealed class BaggageHostPolicy
{
    private static readonly AsyncLocal<BaggageHostPolicy?> CurrentPolicy = new();
    private static readonly BaggageHostPolicy None = new([]);
    private static volatile BaggageHostPolicy? _default;

    private readonly bool _any;
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _suffixes = [];

    public BaggageHostPolicy(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns.Select(p => p.Trim()))
        {
            if (pattern == "*")
                _any = true;
            else if (pattern.StartsWith("*.", StringComparison.Ordinal))
                _suffixes.Add(pattern[1..]); // ".svc.cluster.local"
            else
                _hosts.Add(pattern);
        }
    }

    /// <summary>Política da requisição atual; <c>null</c> fora de uma requisição atendida pelo middleware.</summary>
    public static BaggageHostPolicy? Current
    {
        get => CurrentPolicy.Value;
        set => CurrentPolicy.Value = value;
    }

    /// <summary>Política do processo (de <c>BaggageAllowedHosts</c>), usada fora de uma requisição atendida pelo middleware.</summary>
    public static BaggageHostPolicy? Default
    {
        get => _default;
        set => _default = value;
    }

    /// <summary>Política que vale agora: a da requisição, a do processo ou, sem nenhuma, a que não permite host algum.</summary>
    public static BaggageHostPolicy Effective => Current ?? Default ?? None;

    /// <summary><c>true</c> quando o host pode receber o Baggage.</summary>
    public bool Allows(string host)
    {
        if (_any || _hosts.Contains(host))
            return true;

        foreach (var suffix in _suffixes)
        {
            if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}

/// <summary>
/// Envolve o propagador padrão do OpenTelemetry e deixa de escrever o cabeçalho <c>baggage</c> em requisições HTTP para hosts
/// fora da <see cref="BaggageHostPolicy"/> da requisição atual. O <c>traceparent</c> e a extração não mudam.
/// </summary>
internal sealed class HostFilteringPropagator(TextMapPropagator inner) : TextMapPropagator
{
    private const string BaggageHeader = "baggage";
    private static readonly Lock InstallLock = new();

    public override ISet<string>? Fields => inner.Fields;

    /// <summary>Propagador envolvido (o padrão do processo no momento da instalação).</summary>
    internal TextMapPropagator Inner => inner;

    /// <summary>
    /// Instala o filtro nos dois propagadores padrão do processo — o do OpenTelemetry e o do .NET
    /// (<see cref="DistributedContextPropagator.Current"/>, usado pelo <see cref="HttpClient"/> mesmo sem OpenTelemetry).
    /// Uma vez; chamadas seguintes não fazem nada.
    /// </summary>
    public static void Install()
    {
        lock (InstallLock)
        {
            // O construtor estático do Sdk é quem define o padrão (TraceContext + Baggage). Lido antes dele rodar, o padrão ainda é o
            // no-op: o filtro envolveria o no-op e, logo em seguida, substituiria o padrão do Sdk — desligando a propagação de
            // traceparent e baggage do processo inteiro (acontecia quando o primeiro host do processo usava Provider None).
            _ = Sdk.SuppressInstrumentation;
            if (Propagators.DefaultTextMapPropagator is not HostFilteringPropagator)
                Sdk.SetDefaultTextMapPropagator(new HostFilteringPropagator(Propagators.DefaultTextMapPropagator));
            if (DistributedContextPropagator.Current is not HostFilteringDistributedContextPropagator)
                DistributedContextPropagator.Current = new HostFilteringDistributedContextPropagator(DistributedContextPropagator.Current);
        }
    }

    public override PropagationContext Extract<T>(PropagationContext context, T carrier, Func<T, string, IEnumerable<string>?> getter) =>
        inner.Extract(context, carrier, getter);

    public override void Inject<T>(PropagationContext context, T carrier, Action<T, string, string> setter)
    {
        if (carrier is not HttpRequestMessage { RequestUri: { IsAbsoluteUri: true } uri } || BaggageHostPolicy.Effective.Allows(uri.IdnHost))
        {
            inner.Inject(context, carrier, setter);
            return;
        }

        inner.Inject(context, carrier, (target, name, value) =>
        {
            if (!string.Equals(name, BaggageHeader, StringComparison.OrdinalIgnoreCase))
                setter(target, name, value);
        });
    }
}

/// <summary>
/// O mesmo filtro para o propagador nativo do .NET: o <c>DiagnosticsHandler</c> do <see cref="HttpClient"/> escreve o Baggage
/// do <see cref="Activity"/> (cabeçalho <c>baggage</c> ou <c>Correlation-Context</c>, conforme a versão do .NET) em toda
/// requisição de saída, por fora do propagador do OpenTelemetry. Para hosts fora da <see cref="BaggageHostPolicy"/> da
/// requisição atual esses dois cabeçalhos não são escritos; <c>traceparent</c>/<c>tracestate</c> e a extração não mudam.
/// </summary>
/// <remarks>
/// O <see cref="SocketsHttpHandler"/> guarda o propagador em uso quando é criado: handlers criados antes da instalação
/// (antes da montagem do pipeline HTTP ou do provedor de telemetria) continuam com o propagador anterior.
/// </remarks>
internal sealed class HostFilteringDistributedContextPropagator(DistributedContextPropagator inner) : DistributedContextPropagator
{
    private const string BaggageHeader = "baggage";
    private const string CorrelationContextHeader = "Correlation-Context";

    public override IReadOnlyCollection<string> Fields => inner.Fields;

    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
        if (setter is null || carrier is not HttpRequestMessage { RequestUri: { IsAbsoluteUri: true } uri }
            || BaggageHostPolicy.Effective.Allows(uri.IdnHost))
        {
            inner.Inject(activity, carrier, setter);
            return;
        }

        inner.Inject(activity, carrier, (target, name, value) =>
        {
            if (!IsBaggageField(name))
                setter(target, name, value);
        });
    }

    public override void ExtractTraceIdAndState(object? carrier, PropagatorGetterCallback? getter, out string? traceId, out string? traceState) =>
        inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(object? carrier, PropagatorGetterCallback? getter) =>
        inner.ExtractBaggage(carrier, getter);

    internal static bool IsBaggageField(string? name) =>
        string.Equals(name, BaggageHeader, StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, CorrelationContextHeader, StringComparison.OrdinalIgnoreCase);
}
