using System.Diagnostics;
using System.Net;
using System.Reflection;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Trace;
using TEC.Observability.DependencyInjection;

namespace TEC.Observability.Tests;

/// <summary>Cenários executados em processo filho (<see cref="IsolatedProcess"/>); cada resultado é um dicionário de texto.</summary>
internal static class IsolatedScenarios
{
    /// <summary>Spans de um serviço com o <c>Provider</c> do argumento, exportados em memória.</summary>
    public const string Spans = "spans";

    /// <summary>Trace com <c>sampled=00</c> no <c>traceparent</c> recebido (decisão do chamador: não amostrar).</summary>
    public const string NotSampledParent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-00";

    public const string FakeConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://localhost/";

    /// <summary>Instalação do filtro de Baggage como primeira coisa do processo (antes de qualquer uso do <c>Sdk</c>).</summary>
    public const string PropagatorFirst = "propagador-primeiro";

    public static Task<List<Dictionary<string, string?>>> RunAsync(string scenario, string argument) => scenario switch
    {
        Spans => SpansAsync(argument),
        PropagatorFirst => Task.FromResult(PropagatorInstalledFirst()),
        _ => throw new ArgumentException($"Cenário desconhecido: {scenario}", nameof(scenario)),
    };

    /// <summary>
    /// O middleware de correlation id instala o filtro no construtor — com <c>Provider: None</c>, antes de qualquer TracerProvider.
    /// Aqui a instalação é literalmente a primeira coisa do processo; depois, o padrão precisa continuar injetando traceparent e baggage.
    /// </summary>
    private static List<Dictionary<string, string?>> PropagatorInstalledFirst()
    {
        TEC.Observability.Tracing.HostFilteringPropagator.Install();

        var installed = OpenTelemetry.Context.Propagation.Propagators.DefaultTextMapPropagator;
        var inner = (installed as TEC.Observability.Tracing.HostFilteringPropagator)?.Inner;
        using var activity = new Activity("teste").SetIdFormat(ActivityIdFormat.W3C).Start();
        var context = new OpenTelemetry.Context.Propagation.PropagationContext(activity.Context,
            OpenTelemetry.Baggage.Create(new Dictionary<string, string> { ["correlation.id"] = "pedido-42" }));
        var headers = new Dictionary<string, string>();
        installed.Inject(context, headers, (carrier, name, value) => carrier[name] = value);

        return
        [
            new()
            {
                ["instalado"] = installed.GetType().Name,
                ["interno"] = inner?.GetType().Name,
                ["traceparent"] = headers.GetValueOrDefault("traceparent"),
                ["baggage"] = headers.GetValueOrDefault("baggage"),
            },
        ];
    }

    private static async Task<List<Dictionary<string, string?>>> SpansAsync(string provider)
    {
        await using var downstream = new RawHttpServer();
        var exported = new List<Activity>();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(TestApp.Settings(
            ("Provider", provider),
            ("Azure:ConnectionString", FakeConnectionString),
            ("Azure:EnableLiveMetrics", "false"),
            ("Azure:DisableOfflineStorage", "true"),
            ("LogFile:FolderPath", Path.Combine(Path.GetTempPath(), $"tec-observability-isolado-{Guid.NewGuid():N}")),
            ("IgnoredPaths:0", "/interno")));

        builder.Services.AddTecObservability(builder.Configuration).AddAzureMonitorExporter();
        // Ingestão simulada: sem rede, o envio do exportador do Azure Monitor não atrasa o ForceFlush e o encerramento.
        builder.Services.Configure<Azure.Monitor.OpenTelemetry.AspNetCore.AzureMonitorOptions>(o => o.Transport = new HttpClientTransport(new IngestionHandler()));
        builder.Services.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported));
        builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(o =>
            o.EnrichWithHttpRequest = (activity, _) => activity.SetTag("servico.enrich", "sim"));
        builder.Services.AddHttpClient();

        await using var app = builder.Build();
        app.UseTecObservability();
        app.MapGet("/pedidos", () => "ok");
        app.MapGet("/interno/status", () => "ok");
        app.MapGet("/cofre", async () =>
        {
            var secrets = new SecretClient(new Uri("https://meu-cofre.vault.azure.net/"), new FakeCredential(), new SecretClientOptions
            {
                Transport = new HttpClientTransport(new KeyVaultHandler()),
                Retry = { MaxRetries = 0 },
            });
            var secret = await secrets.GetSecretAsync("senha-do-banco", "0123abcd");
            return secret.Value.Name;
        });
        app.MapGet("/saida", async (IHttpClientFactory http) =>
        {
            using var response = await http.CreateClient().GetAsync(new Uri($"http://127.0.0.1:{downstream.Port}/api?api_key=SEGREDO"));
            return (int)response.StatusCode;
        });
        await app.StartAsync();

        var client = app.GetTestClient();
        foreach (var path in new[] { "/pedidos?token=SEGREDO&pagina=2", "/interno/status?x=1", "/health/live?x=1", "/health/ready", "/cofre" })
            EnsureSuccess(await client.GetAsync(path), path);

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/pedidos?caso=pai-nao-amostrado"))
        {
            request.Headers.Add("traceparent", NotSampledParent);
            EnsureSuccess(await client.SendAsync(request), request.RequestUri!.ToString());
        }

        EnsureSuccess(await client.GetAsync("/saida"), "/saida");

        // O span da requisição é encerrado pelo servidor depois que a resposta chega ao cliente.
        var tracer = app.Services.GetRequiredService<TracerProvider>();
        for (var i = 0; i < 100 && !Exported(exported).Any(a => a.Kind == ActivityKind.Server && Equals(a.GetTagItem("url.path"), "/saida")); i++)
            await Task.Delay(50);
        await Task.Delay(200);
        tracer.ForceFlush(5_000);

        var sampler = tracer.GetType().GetProperty("Sampler", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(tracer);
        List<Dictionary<string, string?>> results = [new() { ["tipo"] = "sampler", ["nome"] = sampler?.GetType().Name }];
        foreach (var activity in Exported(exported))
        {
            var result = new Dictionary<string, string?>
            {
                ["tipo"] = "span",
                ["fonte"] = activity.Source.Name,
                ["kind"] = activity.Kind.ToString(),
                ["nome"] = activity.DisplayName,
            };
            foreach (var (key, value) in activity.TagObjects)
                result[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
            results.Add(result);
        }

        await app.StopAsync();
        return results;
    }

    /// <summary>Cópia com ToArray: o exportador em memória adiciona spans em outra thread sem lock na lista.</summary>
    private static List<Activity> Exported(List<Activity> exported) => [.. exported.ToArray().OfType<Activity>()];

    private static void EnsureSuccess(HttpResponseMessage response, string path)
    {
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"GET {path}: {(int)response.StatusCode}");
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("token-de-teste", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }

    /// <summary>Ingestão do Application Insights simulada: aceita tudo.</summary>
    internal sealed class IngestionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"itemsReceived":0,"itemsAccepted":0,"errors":[]}""", System.Text.Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>Key Vault simulado: responde ao GET do segredo sem rede.</summary>
    private sealed class KeyVaultHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"value":"valor","id":"https://meu-cofre.vault.azure.net/secrets/senha-do-banco/0123abcd"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
