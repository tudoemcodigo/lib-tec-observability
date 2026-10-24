using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace TEC.Observability.HealthChecks;

/// <summary>
/// Health check genérico de banco relacional: abre uma conexão ADO.NET e executa uma consulta leve. Funciona com qualquer
/// driver (SQL Server, PostgreSQL, Oracle, MySQL...) porque recebe a fábrica de conexão — a biblioteca não referencia drivers.
/// </summary>
/// <remarks>
/// O prazo é garantido mesmo com driver que ignora o <see cref="CancellationToken"/>: a consulta recebe <c>CommandTimeout</c> e a
/// espera é limitada por <see cref="Task.WaitAsync(TimeSpan, CancellationToken)"/>. A operação abandonada termina em segundo
/// plano e a conexão é descartada quando ela terminar.
/// </remarks>
internal sealed class DatabaseHealthCheck(Func<DbConnection> connectionFactory, string query, TimeSpan timeout, ILogger<DatabaseHealthCheck> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        Task? work = null;
        try
        {
            work = ExecuteAsync(cancellationToken);
            await work.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy("Banco de dados acessível.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelamento do framework (prazo do registro, prazo da execução inteira ou encerramento): um driver que ignora o
            // token deixa a operação rodando, e ela pode falhar depois.
            ObserveInBackground(work);
            throw;
        }
        catch (TimeoutException ex)
        {
            // Também quando o token do framework foi cancelado junto: o prazo do registro é o mesmo deste timeout.
            ObserveInBackground(work);
            HealthCheckLog.DependencyFailed(logger, ex, context.Registration.Name);
            var description = string.Create(CultureInfo.InvariantCulture, $"Sem resposta do banco de dados em {timeout.TotalSeconds:0.#}s.");
            return new HealthCheckResult(context.Registration.FailureStatus, description,
                new TimeoutException("Tempo limite do health check de banco de dados excedido.", ex));
        }
        catch (Exception ex)
        {
            HealthCheckLog.DependencyFailed(logger, ex, context.Registration.Name);
            return new HealthCheckResult(context.Registration.FailureStatus, "Banco de dados inacessível.", ex);
        }
    }

    private async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        var connection = connectionFactory();
        await using (connection.ConfigureAwait(false))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                // A consulta é definida pelo serviço na inicialização (padrão "SELECT 1"); nunca vem de entrada de usuário.
#pragma warning disable CA2100
                command.CommandText = query;
#pragma warning restore CA2100
                command.CommandTimeout = CommandTimeoutSeconds(timeout);
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary><c>CommandTimeout</c> em segundos inteiros, arredondado para cima (zero significaria "sem limite").</summary>
    internal static int CommandTimeoutSeconds(TimeSpan timeout) =>
        (int)Math.Clamp(Math.Ceiling(timeout.TotalSeconds), 1, int.MaxValue);

    /// <summary>A operação abandonada pode falhar depois; a exceção é observada para não virar <c>UnobservedTaskException</c>.</summary>
    private static void ObserveInBackground(Task? work) =>
        work?.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}
