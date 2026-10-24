using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace TEC.Observability.HealthChecks;

/// <summary>
/// Health check genérico de endpoint TCP (Redis, RabbitMQ, Kafka, bancos em porta direta): tenta estabelecer
/// conexão de socket dentro do prazo especificado. Funciona sem drivers ou pacotes de terceiros, 100% Native AOT.
/// </summary>
internal sealed class TcpEndpointHealthCheck(string host, int port, TimeSpan timeout, ILogger<TcpEndpointHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Endpoint TCP acessível.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            HealthCheckLog.DependencyFailed(logger, ex, context.Registration.Name);
            var description = string.Create(CultureInfo.InvariantCulture, $"Sem resposta do endpoint TCP ({host}:{port}) em {timeout.TotalSeconds:0.#}s.");
            return new HealthCheckResult(context.Registration.FailureStatus, description,
                new TimeoutException("Tempo limite da conexão TCP excedido.", ex));
        }
        catch (Exception ex)
        {
            HealthCheckLog.DependencyFailed(logger, ex, context.Registration.Name);
            return new HealthCheckResult(context.Registration.FailureStatus, $"Endpoint TCP ({host}:{port}) inacessível.", ex);
        }
    }
}
