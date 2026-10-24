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
/// Início do processo e tempo de atividade só entram nos dados com <c>HealthChecks:ExposeDetails</c> ligado: revelam quando o
/// serviço foi reiniciado (janela de atualização, quedas) a qualquer cliente que alcance o endpoint.
/// </remarks>
internal sealed class LivenessHealthCheck(IOptions<ObservabilityOptions> options, TimeProvider time) : IHealthCheck
{
    private static readonly DateTimeOffset StartedAt = GetProcessStart();

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (options.Value.HealthChecks?.ExposeDetails != true)
            return Task.FromResult(HealthCheckResult.Healthy("Processo em execução."));

        var data = new Dictionary<string, object>
        {
            ["startedAt"] = StartedAt,
            ["uptimeSeconds"] = Math.Round((time.GetUtcNow() - StartedAt).TotalSeconds),
        };

        return Task.FromResult(HealthCheckResult.Healthy("Processo em execução.", data));
    }

    private static DateTimeOffset GetProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return process.StartTime.ToUniversalTime();
    }
}
