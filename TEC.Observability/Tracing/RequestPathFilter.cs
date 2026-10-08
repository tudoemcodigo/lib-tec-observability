using Microsoft.AspNetCore.Http;
using TEC.Observability.Configuration;

namespace TEC.Observability.Tracing;

/// <summary>
/// Decide se uma requisição gera trace. Rotas ignoradas (health checks, Swagger) são descartadas antes de o span existir,
/// então não há custo de exportação nem ruído no backend.
/// </summary>
internal sealed class RequestPathFilter
{
    private readonly PathString[] _ignored;

    public RequestPathFilter(IEnumerable<string> ignoredPaths) =>
        _ignored = [.. ignoredPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new PathString(p.StartsWith('/') ? p.TrimEnd('/') : "/" + p.TrimEnd('/')))
            .Distinct()];

    /// <summary>Filtro das opções efetivas: as rotas de health check configuradas nunca geram trace, mesmo fora de <c>IgnoredPaths</c>.</summary>
    public static RequestPathFilter Create(ObservabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        IEnumerable<string> ignored = options.IgnoredPaths ?? [];
        return new RequestPathFilter(options.HealthChecks is { Enabled: true } healthChecks
            ? [.. ignored, healthChecks.LiveEndpoint, healthChecks.ReadyEndpoint]
            : ignored);
    }

    /// <summary><c>true</c> quando a rota deve ser rastreada.</summary>
    public bool ShouldTrace(HttpContext context) => ShouldTrace(context.Request.Path);

    /// <summary>Compara por segmento: <c>/health</c> ignora <c>/health</c> e <c>/health/live</c>, mas não <c>/healthcare</c>.</summary>
    public bool ShouldTrace(PathString path)
    {
        foreach (var ignored in _ignored)
        {
            if (path.StartsWithSegments(ignored, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
