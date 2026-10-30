using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TEC.Observability.Configuration;

namespace TEC.Observability.HealthChecks;

/// <summary>
/// Liveness: responde enquanto o processo consegue atender requisições. Não toca nenhuma dependência externa —
/// um banco fora do ar não deve fazer o orquestrador reiniciar o serviço.
/// </summary>
/// <remarks>
/// <para>
/// Início do processo e tempo de atividade só entram nos dados com <c>HealthChecks:ExposeDetails</c> ligado: revelam quando o
/// serviço foi reiniciado (janela de atualização, quedas) a qualquer cliente que alcance o endpoint.
/// </para>
/// <para>
/// O início do processo é lido só na primeira verificação com detalhes, uma vez por processo. <see cref="Process.StartTime"/> pode
/// falhar (sandbox sem acesso a <c>/proc</c>, plataforma sem suporte): o liveness continua <c>Healthy</c>, sem os dois campos.
/// Antes, a leitura ficava num inicializador estático, e a falha virava <see cref="TypeInitializationException"/> em toda
/// sondagem, com o orquestrador reiniciando um processo saudável.
/// </para>
/// </remarks>
internal sealed class LivenessHealthCheck : IHealthCheck
{
    private const string Description = "Processo em execução.";

    private static readonly Lazy<DateTimeOffset?> ProcessStart = new(() => TryGetStart(ReadProcessStart), LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly IOptions<ObservabilityOptions> _options;
    private readonly TimeProvider _time;
    private readonly Lazy<DateTimeOffset?> _start;

    public LivenessHealthCheck(IOptions<ObservabilityOptions> options, TimeProvider time)
        : this(options, time, ProcessStart)
    {
    }

    /// <summary>Com a fonte do início do processo injetada (testes).</summary>
    internal LivenessHealthCheck(IOptions<ObservabilityOptions> options, TimeProvider time, Lazy<DateTimeOffset?> start)
    {
        _options = options;
        _time = time;
        _start = start;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (_options.Value.HealthChecks?.ExposeDetails != true || _start.Value is not { } startedAt)
            return Task.FromResult(HealthCheckResult.Healthy(Description));

        var data = new Dictionary<string, object>
        {
            ["startedAt"] = startedAt,
            ["uptimeSeconds"] = Math.Round((_time.GetUtcNow() - startedAt).TotalSeconds),
        };

        return Task.FromResult(HealthCheckResult.Healthy(Description, data));
    }

    /// <summary>Início do processo, ou <c>null</c> quando a plataforma não o informa.</summary>
    internal static DateTimeOffset? TryGetStart(Func<DateTimeOffset> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception
            or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static DateTimeOffset ReadProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }
}
