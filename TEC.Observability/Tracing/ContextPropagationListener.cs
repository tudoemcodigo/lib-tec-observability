using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace TEC.Observability.Tracing;

/// <summary>
/// Com <c>Provider: None</c> não há <c>TracerProvider</c> da biblioteca, e o ASP.NET Core e o <see cref="HttpClient"/> só criam
/// <see cref="Activity"/> quando alguém escuta as suas fontes (ou quando os logs do ASP.NET Core estão ligados): sem
/// <see cref="Activity"/>, o <c>traceparent</c> e o <c>baggage</c> (com o correlation id) não seguem para os serviços chamados, e
/// cada serviço gera o próprio id. Este listener mínimo escuta só as fontes <c>Microsoft.AspNetCore</c> e <c>System.Net.Http</c> e
/// pede <see cref="ActivitySamplingResult.PropagationData"/>: os spans são criados para a propagação, sem gravar atributos e sem
/// exportar nada.
/// </summary>
/// <remarks>
/// Os listeners de <see cref="ActivitySource"/> são do processo: com outro pipeline de tracing (ex.: .NET Aspire ServiceDefaults),
/// vale a decisão mais alta entre os listeners, então este não muda a amostragem dele. Fica ativo da subida (antes do servidor
/// aceitar requisições) ao fim do host.
/// </remarks>
internal sealed class ContextPropagationListener : IHostedLifecycleService, IDisposable
{
    private static readonly string[] Sources = ["Microsoft.AspNetCore", "System.Net.Http"];

    private ActivityListener? _listener;

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        _listener ??= Create();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _listener?.Dispose();
        _listener = null;
    }

    private static ActivityListener Create()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => Array.IndexOf(Sources, source.Name) >= 0,
            Sample = static (ref ActivityCreationOptions<ActivityContext> options) =>
                options.Parent.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.PropagationData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> options) =>
                ActivityContext.TryParse(options.Parent, null, out var ctx) && ctx.TraceFlags.HasFlag(ActivityTraceFlags.Recorded)
                    ? ActivitySamplingResult.AllDataAndRecorded
                    : ActivitySamplingResult.PropagationData,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
