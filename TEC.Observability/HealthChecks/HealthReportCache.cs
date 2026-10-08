using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TEC.Observability.Configuration;

namespace TEC.Observability.HealthChecks;

/// <summary>Relatório de health check e o instante em que as verificações rodaram.</summary>
internal sealed record HealthSnapshot(HealthReport Report, DateTimeOffset Timestamp)
{
    /// <summary>
    /// Corpo JSON já serializado, reaproveitado por todas as respostas do mesmo relatório (as opções de saída são fixas). Corrida
    /// benigna: duas requisições simultâneas podem serializar o mesmo relatório, com o mesmo resultado.
    /// </summary>
    public byte[]? Body { get; set; }
}

/// <summary>
/// Executa as verificações de uma tag com execução única e resultado reaproveitado por um curto período. Os endpoints de health
/// são anônimos: sem isso, cada requisição abriria uma conexão de banco e chamadas HTTP, e uma rajada de requisições (ou várias
/// sondas simultâneas) viraria carga direta nas dependências.
/// </summary>
internal sealed class HealthReportCache(HealthCheckService service, IOptions<ObservabilityOptions> options, IHostApplicationLifetime lifetime,
    ILogger<HealthReportCache> logger, TimeProvider time)
{
    /// <summary>Folga para a execução encerrar sozinha depois do cancelamento, antes de ser abandonada.</summary>
    private static readonly TimeSpan AbandonGrace = TimeSpan.FromMilliseconds(500);

    private readonly TimeSpan _duration = options.Value.HealthChecks.CacheDuration;
    private readonly TimeSpan _timeout = options.Value.HealthChecks.Timeout;
    private readonly Slot _live = new(HealthCheckTags.Live);
    private readonly Slot _ready = new(HealthCheckTags.Ready);

    /// <summary>Relatório da tag (<c>live</c> ou <c>ready</c>): o que está em execução, o ainda válido ou um novo.</summary>
    public Task<HealthSnapshot> GetAsync(string tag, CancellationToken cancellationToken)
    {
        var slot = tag == HealthCheckTags.Live ? _live : _ready;
        Task<HealthSnapshot> task;

        lock (slot.Sync)
        {
            var reusable = slot.Current is { } current
                && (!current.IsCompleted || (current.IsCompletedSuccessfully && time.GetElapsedTime(Volatile.Read(ref slot.CompletedAt)) < _duration));

            task = reusable ? slot.Current! : slot.Current = Start(slot);
        }

        // Quem desiste da requisição deixa de esperar, mas a execução compartilhada segue para os demais.
        return task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// A execução é compartilhada: não pode herdar o contexto de quem a disparou (Activity, Baggage com o correlation id, escopos
    /// de log, cultura...), senão logs e spans das verificações ficariam atribuídos à requisição do primeiro chamador.
    /// </summary>
    private Task<HealthSnapshot> Start(Slot slot)
    {
        using (ExecutionContext.SuppressFlow())
            return Task.Run(() => RunAsync(slot), CancellationToken.None);
    }

    private async Task<HealthSnapshot> RunAsync(Slot slot)
    {
        var stopping = lifetime.ApplicationStopping;
        HealthReport report;

        // O tempo de cada verificação é limitado pelo timeout do seu registro, mas ele depende de a verificação respeitar o
        // cancelamento. O prazo daqui vale para a execução inteira: cancela e, se a verificação ignorar o cancelamento, deixa de
        // esperar (WaitAsync) — senão a tarefa compartilhada nunca terminaria e todas as requisições seguintes ficariam presas nela.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(_timeout);
        try
        {
            report = await service.CheckHealthAsync(slot.Predicate, timeout.Token)
                .WaitAsync(_timeout + AbandonGrace, stopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !stopping.IsCancellationRequested))
        {
            // Encerramento da aplicação continua propagando o cancelamento (o endpoint responde 503). Aqui foi o prazo: o
            // resultado é Unhealthy, reaproveitado pelo CacheDuration como qualquer outro; depois disso, nova execução.
            HealthCheckLog.ExecutionTimedOut(logger, slot.Tag, _timeout.TotalSeconds);
            report = new HealthReport(new Dictionary<string, HealthReportEntry>(), HealthStatus.Unhealthy, _timeout);
        }

        var snapshot = new HealthSnapshot(report, time.GetUtcNow());
        Volatile.Write(ref slot.CompletedAt, time.GetTimestamp());
        return snapshot;
    }

    private sealed class Slot(string tag)
    {
        public readonly Lock Sync = new();
        public readonly string Tag = tag;
        public readonly Func<HealthCheckRegistration, bool> Predicate = registration => registration.Tags.Contains(tag);
        public Task<HealthSnapshot>? Current;
        public long CompletedAt;
    }
}
