using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TEC.Observability.DependencyInjection;
using TEC.Observability.Tracing;

namespace TEC.Observability.Tests.Integration;

/// <summary>
/// Envia telemetria real (requisições, dependências, traces de log, exceções e métricas) ao Application Insights de testes,
/// pelo mesmo caminho que um serviço usa: <c>Provider: Azure</c> + <c>AddTecObservability(...).AddAzureMonitorExporter()</c>.
/// </summary>
/// <remarks>
/// Cada execução marca tudo com <c>teste.execucao</c> e imprime as consultas KQL para conferir no portal.
/// Configuração e credenciais: ver <see cref="ApplicationInsightsFixture"/>.
/// </remarks>
[Category(TestCategories.Integration)]
[NotInParallel]
public class ApplicationInsightsIntegrationTests
{
    private const int FlushTimeoutMilliseconds = 30_000;
    private const int OrderCount = 5;

    private static readonly TimeSpan IndexingTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(20);

    private static readonly ActivitySource Source = new(ApplicationInsightsFixture.ServiceName);
    private static readonly Meter Meter = new(ApplicationInsightsFixture.ServiceName);
    private static readonly Counter<long> OrdersCreated = Meter.CreateCounter<long>("pedidos.criados", description: "Pedidos criados");
    private static readonly Histogram<double> OrderValue = Meter.CreateHistogram<double>("pedidos.valor", unit: "BRL");

    /// <summary>Autenticação pela chave da connection string: a ingestão aceita os três sinais.</summary>
    [Test]
    public async Task Service_telemetry_reaches_application_insights()
    {
        await ApplicationInsightsFixture.RequireAsync();

        var submission = await SendAsync(useIdentity: false);

        await AssertAcceptedAsync(submission);
    }

    /// <summary>
    /// Autenticação por Entra ID (<c>UseManagedIdentity</c>): a ingestão aceita os três sinais com o token da identidade.
    /// Com <c>TEC_TESTES_OBSERVABILITY_IDENTIDADE=workload</c> roda como em produção (só Workload/Managed Identity); caso contrário,
    /// como em desenvolvimento (<c>DefaultAzureCredential</c> com a credencial do desenvolvedor ou do login OIDC do CI).
    /// </summary>
    /// <remarks>
    /// Só prova a autenticação por identidade se o recurso estiver com a autenticação local desativada (<c>DisableLocalAuth</c>);
    /// com ela ativa, a chave da connection string basta e o token não é exigido.
    /// </remarks>
    [Test]
    public async Task Entra_id_authenticated_ingestion_is_accepted()
    {
        await ApplicationInsightsFixture.RequireCredentialAsync(ApplicationInsightsFixture.IngestionScope);

        var submission = await SendAsync(useIdentity: true);

        await AssertAcceptedAsync(submission);
    }

    /// <summary>Fecha o ciclo: o que foi enviado aparece nas tabelas do Application Insights, com as dimensões esperadas.</summary>
    [Test]
    public async Task Sent_data_shows_up_in_application_insights_queries()
    {
        await ApplicationInsightsFixture.RequireCredentialAsync(ApplicationInsightsFixture.QueryScope);

        var submission = await SendAsync(useIdentity: false);
        await AssertAcceptedAsync(submission);

        var kql = $"""
            let e = '{submission.RunId}';
            let reqs = requests | where customDimensions['teste.execucao'] == e;
            print requests = toscalar(reqs | count),
                  falhas = toscalar(reqs | where tostring(success) =~ 'false' | count),
                  correlacionados = toscalar(reqs | where customDimensions['correlation.id'] == strcat('integracao-', e) | count),
                  spans = toscalar(dependencies | where customDimensions['teste.execucao'] == e and name == 'ProcessarPedido' | count),
                  http = toscalar(dependencies | where type =~ 'Http' and operation_Id in ((reqs | project operation_Id)) | count),
                  logs = toscalar(traces | where customDimensions['teste.execucao'] == e and message startswith 'Pedido' | count),
                  excecoes = toscalar(exceptions | where customDimensions['teste.execucao'] == e | count),
                  metricas = toscalar(customMetrics | where name == 'pedidos.criados' and customDimensions['teste.execucao'] == e | count),
                  health = toscalar(requests | where cloud_RoleName == '{ApplicationInsightsFixture.ServiceName}' and timestamp > ago(1h) and url has '/health' | count)
            """;

        using var client = new HttpClient();
        var deadline = DateTimeOffset.UtcNow + IndexingTimeout;
        IReadOnlyDictionary<string, long> row;

        // A indexação leva alguns minutos; as tabelas não ficam disponíveis todas ao mesmo tempo. É ela que domina a duração do
        // teste (de segundos a minutos, em qualquer TFM): cada consulta vai para a saída, com o tempo decorrido.
        var polling = Stopwatch.StartNew();
        while (true)
        {
            row = await ApplicationInsightsFixture.QueryRowAsync(client, kql);
            TestContext.Current!.Output.WriteLine($"Consulta após {polling.Elapsed.TotalSeconds:0}s: "
                + string.Join(", ", row.Select(c => $"{c.Key}={c.Value}")));
            var complete = row["requests"] >= OrderCount + 1 && row["spans"] >= OrderCount + 1 && row["http"] >= OrderCount
                && row["logs"] >= OrderCount && row["excecoes"] >= 1 && row["metricas"] >= 1;
            if (complete || DateTimeOffset.UtcNow >= deadline)
                break;
            await Task.Delay(PollingInterval);
        }

        TestContext.Current!.Output.WriteLine("Resultado da consulta: " + string.Join(", ", row.Select(c => $"{c.Key}={c.Value}")));

        // Mínimos, não valores exatos: a ingestão pode duplicar itens em reenvios (at-least-once) e a indexação continua depois
        // do fim do polling. "health" segue exato: nenhuma sondagem pode aparecer.
        await Assert.That(row["requests"]).IsGreaterThanOrEqualTo(OrderCount + 1);
        await Assert.That(row["falhas"]).IsGreaterThanOrEqualTo(1);
        await Assert.That(row["correlacionados"]).IsGreaterThanOrEqualTo(OrderCount);
        await Assert.That(row["spans"]).IsGreaterThanOrEqualTo(OrderCount + 1);
        await Assert.That(row["http"]).IsGreaterThanOrEqualTo(OrderCount);
        await Assert.That(row["logs"]).IsGreaterThanOrEqualTo(OrderCount);
        await Assert.That(row["excecoes"]).IsGreaterThanOrEqualTo(1);
        await Assert.That(row["metricas"]).IsGreaterThanOrEqualTo(1);
        await Assert.That(row["health"]).IsEqualTo(0);
    }

    private sealed record Submission(string RunId, bool Traces, bool Metrics, bool Logs, IReadOnlyList<string> Problems, IReadOnlyCollection<string> Transmitted);

    private static async Task AssertAcceptedAsync(Submission submission)
    {
        await Assert.That(submission.Traces).IsTrue();
        await Assert.That(submission.Metrics).IsTrue();
        await Assert.That(submission.Logs).IsTrue();
        await Assert.That(submission.Problems).IsEmpty();
        await Assert.That(submission.Transmitted).Contains("AzureMonitorTraceExporter");
        await Assert.That(submission.Transmitted).Contains("AzureMonitorMetricExporter");
        await Assert.That(submission.Transmitted).Contains("AzureMonitorLogExporter");
    }

    /// <summary>Sobe o serviço, gera a carga de teste e devolve o que a ingestão respondeu.</summary>
    private static async Task<Submission> SendAsync(bool useIdentity)
    {
        var runId = Guid.NewGuid().ToString("N");
        using var listener = new AzureMonitorExporterListener();

        // O DefaultAzureCredential (ambiente Development) lê o tenant desta variável. Ela é do processo inteiro: o valor
        // original é restaurado no fim, para não vazar para outros testes.
        var originalTenant = Environment.GetEnvironmentVariable("AZURE_TENANT_ID");
        if (useIdentity && ApplicationInsightsFixture.TenantId is { } tenant && string.IsNullOrEmpty(originalTenant))
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", tenant);

        try
        {
            var app = await StartAsync(runId, useIdentity);
            bool traces, metrics, logs;
            try
            {
                var client = app.GetTestClient();

                // Sondagens de health check: não devem aparecer em requests.
                using (var live = await client.GetAsync("/health/live"))
                using (var ready = await client.GetAsync("/health/ready"))
                {
                    await Assert.That(live.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(ready.StatusCode).IsEqualTo(HttpStatusCode.OK);
                }

                // Requisições de negócio: request + span interno + dependência HTTP + log + métricas.
                for (var order = 1; order <= OrderCount; order++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"/pedidos/{order}");
                    request.Headers.Add(CorrelationId.HeaderName, $"integracao-{runId}");
                    using var response = await client.SendAsync(request);

                    await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created);
                    await Assert.That(response.Headers.GetValues(CorrelationId.HeaderName).Single()).IsEqualTo($"integracao-{runId}");
                }

                // Requisição com falha: request com erro + exceção.
                using (var failure = await client.PostAsync("/pedidos/0", content: null))
                    await Assert.That(failure.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);

                // O span da requisição é encerrado pelo servidor depois que a resposta chega ao cliente.
                await Task.Delay(TimeSpan.FromSeconds(1));

                traces = app.Services.GetRequiredService<TracerProvider>().ForceFlush(FlushTimeoutMilliseconds);
                metrics = app.Services.GetRequiredService<MeterProvider>().ForceFlush(FlushTimeoutMilliseconds);
                logs = app.Services.GetRequiredService<LoggerProvider>().ForceFlush(FlushTimeoutMilliseconds);
            }
            finally
            {
                await app.DisposeAsync();
            }

            // Depois do Dispose: o encerramento dos provedores ainda pode enviar (e gerar eventos do exportador).
            WriteQueries(runId);
            return new Submission(runId, traces, metrics, logs, listener.Problems, listener.Transmitted);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AZURE_TENANT_ID", originalTenant);
        }
    }

    private static async Task<WebApplication> StartAsync(string runId, bool useIdentity) =>
        await TestApp.StartAsync(
            observability: o => o.AddAzureMonitorExporter()
                .AddDatabaseHealthCheck("PrimaryDb", SqliteFactory.Instance, "Data Source=:memory:"),
            services: s => s.AddHttpClient(),
            endpoints: a => a.MapPost("/pedidos/{id:int}", async (int id, IHttpClientFactory http, ILogger<ApplicationInsightsIntegrationTests> logger) =>
            {
                using var activity = Source.StartActivity("ProcessarPedido");
                activity?.SetTag("pedido.id", id);
                activity?.SetTag("teste.execucao", runId);
                activity?.Parent?.SetTag("teste.execucao", runId);

                using var scope = logger.BeginScope(new Dictionary<string, object> { ["teste.execucao"] = runId });

                if (id == 0)
                {
                    var exception = new InvalidOperationException($"Pedido inválido (teste de integração {runId}).");
                    activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                    logger.LogError(exception, "Falha ao processar o pedido {PedidoId}", id);
                    return Results.Problem("Pedido inválido.", statusCode: StatusCodes.Status500InternalServerError);
                }

                // Dependência HTTP real, registrada pela instrumentação de HttpClient.
                using var dependency = await http.CreateClient().GetAsync(ApplicationInsightsFixture.IngestionEndpoint);

                var tags = new TagList { { "canal", "integracao" }, { "teste.execucao", runId } };
                OrdersCreated.Add(1, tags);
                OrderValue.Record(100.5 * id, tags);

                logger.LogInformation("Pedido {PedidoId} criado pelo canal {Canal}", id, "integracao");
                return Results.Created($"/pedidos/{id}", new { id });
            }),
            // Em produção a biblioteca só aceita Workload/Managed Identity; em desenvolvimento, o DefaultAzureCredential.
            environment: ApplicationInsightsFixture.WorkloadIdentity ? Environments.Production : Environments.Development,
            settings:
            [
                ("ServiceName", ApplicationInsightsFixture.ServiceName),
                ("ServiceVersion", "0.0.1-integracao"),
                ("Environment", "integration-test"),
                ("Provider", "Azure"),
                ("Azure:ConnectionString", ApplicationInsightsFixture.ConnectionString),
                ("Azure:UseManagedIdentity", useIdentity ? "true" : "false"),
                // Falha de envio deve aparecer no teste, não ficar guardada em disco para reenvio; Live Metrics não faz parte do teste.
                ("Azure:DisableOfflineStorage", "true"),
                ("Azure:EnableLiveMetrics", "false"),
            ]);

    private static void WriteQueries(string runId)
    {
        var output = TestContext.Current!.Output;
        output.WriteLine($"Execução: {runId} (cloud_RoleName = {ApplicationInsightsFixture.ServiceName})");
        output.WriteLine("Consultas (Application Insights > Logs), disponíveis alguns minutos após o envio:");
        output.WriteLine($"  requests     | where customDimensions['teste.execucao'] == '{runId}'   // esperado: 6 (5 com sucesso, 1 com erro)");
        output.WriteLine($"  dependencies | where customDimensions['teste.execucao'] == '{runId}'   // esperado: 6 'ProcessarPedido'");
        output.WriteLine($"  dependencies | where operation_Id in ((requests | where customDimensions['teste.execucao'] == '{runId}' | project operation_Id)) and type =~ 'Http'   // esperado: 5");
        output.WriteLine($"  traces       | where customDimensions['teste.execucao'] == '{runId}' and message startswith 'Pedido'   // esperado: 5");
        output.WriteLine($"  exceptions   | where customDimensions['teste.execucao'] == '{runId}'   // esperado: 1");
        output.WriteLine($"  customMetrics| where name startswith 'pedidos.' and customDimensions['teste.execucao'] == '{runId}'");
        output.WriteLine($"  requests     | where cloud_RoleName == '{ApplicationInsightsFixture.ServiceName}' and url has '/health'   // esperado: nenhum");
    }
}
