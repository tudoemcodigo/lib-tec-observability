using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TEC.Observability.Configuration;
using TEC.Observability.HealthChecks;
using TEC.Observability.Tracing;

namespace TEC.Observability.DependencyInjection;

/// <summary>Ativação da observabilidade no pipeline HTTP.</summary>
public static class ApplicationBuilderExtensions
{
    /// <summary>
    /// Ativa o correlation id (<c>X-Correlation-ID</c>) e mapeia os endpoints de liveness e readiness com a saída JSON padronizada.
    /// Chame no início do pipeline, antes dos middlewares e endpoints da aplicação.
    /// </summary>
    /// <example>
    /// <code>
    /// var app = builder.Build();
    /// app.UseTecObservability();
    /// </code>
    /// </example>
    /// <param name="app">Aplicação.</param>
    /// <returns>A mesma aplicação, para encadear.</returns>
    /// <exception cref="InvalidOperationException"><c>AddTecObservability</c> não foi chamado no registro.</exception>
    public static WebApplication UseTecObservability(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseCorrelationId();
        app.MapTecHealthChecks();
        return app;
    }

    /// <summary>Só o middleware de correlation id (para aplicações que não usam <see cref="WebApplication"/>).</summary>
    /// <param name="app">Pipeline da aplicação.</param>
    /// <returns>O mesmo pipeline, para encadear.</returns>
    /// <exception cref="InvalidOperationException"><c>AddTecObservability</c> não foi chamado no registro.</exception>
    public static IApplicationBuilder UseCorrelationId(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        GetOptions(app.ApplicationServices);
        return app.UseMiddleware<CorrelationIdMiddleware>();
    }

    /// <summary>
    /// Só os endpoints de health check: liveness (tag <c>live</c>) e readiness (tag <c>ready</c>). Respondem a GET sem autenticação
    /// e sem cache HTTP; <c>Unhealthy</c> (ou aplicação encerrando) devolve HTTP 503, <c>Healthy</c> e <c>Degraded</c> devolvem 200.
    /// Fora de <c>Development</c> o JSON traz só status e horário, a menos que <c>HealthChecks:ExposeDetails</c> seja ligado junto
    /// com <c>HealthChecks:ManagementPort</c>. Com <c>ManagementPort</c> definida, os endpoints respondem 404 em conexões
    /// recebidas por outra porta.
    /// </summary>
    /// <param name="endpoints">Rotas da aplicação.</param>
    /// <returns>O mesmo builder de rotas, para encadear.</returns>
    /// <exception cref="InvalidOperationException"><c>AddTecObservability</c> não foi chamado no registro.</exception>
    public static IEndpointRouteBuilder MapTecHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = GetOptions(endpoints.ServiceProvider).HealthChecks;
        if (!options.Enabled)
            return endpoints;

        Map(endpoints, options.LiveEndpoint, HealthCheckTags.Live, options);
        Map(endpoints, options.ReadyEndpoint, HealthCheckTags.Ready, options);
        return endpoints;
    }

    private static void Map(IEndpointRouteBuilder endpoints, string pattern, string tag, ObservabilityHealthChecksOptions options)
    {
        // Só GET, fora das métricas HTTP (as sondas distorceriam latência e volume do serviço). O ASP.NET Core 8 não permite tirar
        // um endpoint das métricas (DisableHttpMetrics é do .NET 9): no .NET 8 as sondas entram em http.server.request.duration,
        // identificáveis pelo atributo http.route.
        var managementPort = options.ManagementPort;
        var endpoint = endpoints.MapGet(pattern, context => WriteReportAsync(context, tag, managementPort))
            .AllowAnonymous()
#if NET9_0_OR_GREATER
            .DisableHttpMetrics()
#endif
            .ExcludeFromDescription();

        // Só roteamento: o cabeçalho Host é escolhido pelo cliente. A restrição de verdade é a ManagementPort, conferida na conexão.
        if (options.AllowedHosts.Count > 0)
            endpoint.RequireHost([.. options.AllowedHosts]);
    }

    private static async Task WriteReportAsync(HttpContext context, string tag, int? managementPort)
    {
        // Porta em que a conexão chegou (não a do cabeçalho Host, que o cliente forja): fora da porta de gerência, o endpoint
        // se comporta como se não existisse — sem executar verificação nenhuma.
        if (managementPort is { } port && context.Connection.LocalPort != port)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        HealthSnapshot snapshot;
        try
        {
            snapshot = await context.RequestServices.GetRequiredService<HealthReportCache>()
                .GetAsync(tag, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return; // o cliente desistiu; não há para quem responder
        }
        catch (OperationCanceledException)
        {
            // Encerramento da aplicação (ApplicationStopping) cancelou as verificações: a instância está saindo, então responde
            // 503 — o que tira a instância do balanceamento — em vez de deixar a exceção virar 500.
            var time = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
            snapshot = new HealthSnapshot(new HealthReport(new Dictionary<string, HealthReportEntry>(), HealthStatus.Unhealthy, TimeSpan.Zero),
                time.GetUtcNow());
        }

        var response = context.Response;
        response.StatusCode = snapshot.Report.Status == HealthStatus.Unhealthy
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status200OK;
        response.Headers.CacheControl = "no-store, no-cache";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "Thu, 01 Jan 1970 00:00:00 GMT";
        response.Headers.XContentTypeOptions = "nosniff";

        // O relatório é compartilhado (cache): o corpo é serializado uma vez e reaproveitado pelas respostas seguintes.
        var body = snapshot.Body ??= HealthCheckResponseWriter.Serialize(snapshot.Report, HealthCheckResponseWriter.GetOptions(context), snapshot.Timestamp);
        await HealthCheckResponseWriter.WriteBodyAsync(context, body).ConfigureAwait(false);
    }

    private static ObservabilityOptions GetOptions(IServiceProvider services)
    {
        if (services.GetService<RequestPathFilter>() is null)
            throw new InvalidOperationException("Chame services.AddTecObservability(...) antes de usar a observabilidade no pipeline.");

        return services.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
    }
}
