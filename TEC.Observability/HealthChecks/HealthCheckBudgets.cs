using System.Globalization;

namespace TEC.Observability.HealthChecks;

/// <summary>Tempo máximo de um health check registrado pela biblioteca e a conta que o gerou (citada no erro de validação).</summary>
internal sealed record HealthCheckBudget(string Name, TimeSpan Limit, string Formula);

/// <summary>
/// Tempos máximos dos health checks registrados pela biblioteca (<c>AddDatabaseHealthCheck</c>, <c>AddSsoHealthCheck</c>,
/// <c>AddExternalServiceHealthCheck</c>). A validação das opções confere que cada um cabe no prazo da execução inteira
/// (<c>HealthChecks:Timeout</c>): com um check mais longo que o prazo, o endpoint responderia <c>Unhealthy</c> por estouro do
/// prazo global antes de o próprio check terminar, sem dizer qual dependência demorou.
/// </summary>
/// <remarks>
/// As verificações de um endpoint rodam em paralelo, então o que conta é o maior tempo, não a soma. Checks registrados
/// direto no <c>IHealthChecksBuilder</c> não entram na conta.
/// </remarks>
internal sealed class HealthCheckBudgets
{
    private readonly List<HealthCheckBudget> _budgets = [];

    public IReadOnlyList<HealthCheckBudget> All => _budgets;

    /// <summary>Banco de dados: o timeout da verificação (a espera é limitada por <c>WaitAsync</c>, mesmo com driver que ignora o cancelamento).</summary>
    public TimeSpan AddDatabase(string name, TimeSpan timeout)
    {
        _budgets.Add(new HealthCheckBudget(name, timeout,
            string.Create(CultureInfo.InvariantCulture, $"timeout {Seconds(timeout)}s")));
        return timeout;
    }

    /// <summary>
    /// HTTP: <c>(Timeout + 1s) × (Retries + 1)</c>, o limite do registro (o framework cancela a verificação nesse prazo). Cada
    /// tentativa é limitada por <c>Timeout</c> (requisição e leitura do corpo) e há 200 ms de espera entre tentativas; o segundo a
    /// mais por tentativa é a folga para isso.
    /// </summary>
    public TimeSpan AddHttp(string name, HttpHealthCheckOptions options)
    {
        var limit = HttpLimit(options);
        _budgets.Add(new HealthCheckBudget(name, limit, string.Create(CultureInfo.InvariantCulture,
            $"(Timeout {Seconds(options.Timeout)}s + 1s) × (Retries {options.Retries} + 1)")));
        return limit;
    }

    internal static TimeSpan HttpLimit(HttpHealthCheckOptions options) => (options.Timeout + TimeSpan.FromSeconds(1)) * (options.Retries + 1);

    /// <summary>Checks que não cabem em <paramref name="timeout"/> (<c>HealthChecks:Timeout</c>), com a mensagem de cada um.</summary>
    public IEnumerable<string> Exceeding(TimeSpan timeout) =>
        _budgets.Where(b => b.Limit >= timeout).Select(b => string.Create(CultureInfo.InvariantCulture,
            $"O health check '{b.Name}' pode levar até {Seconds(b.Limit)}s ({b.Formula}), o que não cabe em HealthChecks:Timeout "
            + $"({Seconds(timeout)}s): aumente HealthChecks:Timeout para mais que {Seconds(b.Limit)}s ou reduza o timeout/as tentativas da verificação."));

    private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
}
