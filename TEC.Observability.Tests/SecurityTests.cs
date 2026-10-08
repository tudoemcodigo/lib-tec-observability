using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using TEC.Observability.Configuration;
using TEC.Observability.DependencyInjection;
using TEC.Observability.HealthChecks;
using TEC.Observability.Tracing;

namespace TEC.Observability.Tests;

/// <summary>Proteções: abuso dos endpoints de health, sondagens HTTP, validação de configuração, credenciais e cabeçalhos OTLP.</summary>
public class SecurityTests
{
    private static readonly Uri Api = new("https://pagamentos.exemplo.com/health/live");

    /// <summary>Registra e lê as opções: a validação roda na leitura (e, num host, na subida).</summary>
    private static void Register(params (string Key, string? Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddTecObservability(TestApp.Configuration(settings));
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
    }

    // ---------- Endpoints de health: carga e superfície ----------

    [Test]
    public async Task Readiness_result_is_reused_within_cache_window()
    {
        var handler = FakeHttpHandler.Status(HttpStatusCode.OK);
        await using var app = await TestApp.StartAsync(o => o.AddExternalServiceHealthCheck("PaymentGateway", Api), handler);
        var client = app.GetTestClient();

        for (var i = 0; i < 10; i++)
            (await client.GetAsync("/health/ready")).Dispose();

        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Without_cache_each_request_runs_checks()
    {
        var handler = FakeHttpHandler.Status(HttpStatusCode.OK);
        await using var app = await TestApp.StartAsync(o => o.AddExternalServiceHealthCheck("PaymentGateway", Api), handler,
            settings: [("HealthChecks:CacheDuration", "00:00:00")]);
        var client = app.GetTestClient();

        (await client.GetAsync("/health/ready")).Dispose();
        (await client.GetAsync("/health/ready")).Dispose();

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Concurrent_requests_share_single_execution_even_without_cache()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var handler = new GatedHandler(() => Interlocked.Increment(ref calls), release.Task);
        await using var app = await TestApp.StartAsync(
            o => o.AddExternalServiceHealthCheck("PaymentGateway", Api),
            services: s => s.AddHttpClient(HttpEndpointHealthCheck.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler),
            settings: [("HealthChecks:CacheDuration", "00:00:00")]);
        var client = app.GetTestClient();

        var requests = Enumerable.Range(0, 25).Select(_ => client.GetAsync("/health/ready")).ToArray();
        await handler.Started.Task;
        await Task.Delay(1000); // folga para as 25 requisições entrarem na mesma execução, mesmo em runner lento
        release.SetResult();
        var responses = await Task.WhenAll(requests);

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(responses.All(r => r.StatusCode == HttpStatusCode.OK)).IsTrue();
    }

    [Test]
    public async Task Health_endpoint_only_accepts_get_and_is_not_cacheable()
    {
        await using var app = await TestApp.StartAsync();
        var client = app.GetTestClient();

        using var post = await client.PostAsync("/health/live", content: null);
        using var get = await client.GetAsync("/health/live");

        await Assert.That(post.StatusCode).IsEqualTo(HttpStatusCode.MethodNotAllowed);
        await Assert.That(get.Headers.CacheControl!.NoStore).IsTrue();
        await Assert.That(get.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");
    }

    [Test]
    public async Task AllowedHosts_filters_endpoints_by_host_header()
    {
        // Só roteamento (o cliente escolhe o Host): a restrição de verdade é a ManagementPort, nos testes abaixo.
        await using var app = await TestApp.StartAsync(settings: [("HealthChecks:AllowedHosts:0", "*:8081")]);
        var client = app.GetTestClient();

        using var publicResponse = await client.GetAsync("http://api.exemplo.com/health/live");
        using var managementResponse = await client.GetAsync("http://api.exemplo.com:8081/health/live");

        await Assert.That(publicResponse.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(managementResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
    }

    [Test]
    [Arguments(8080, HttpStatusCode.NotFound)]
    [Arguments(8081, HttpStatusCode.OK)]
    public async Task ManagementPort_checks_connection_port_not_host_header(int localPort, HttpStatusCode expected)
    {
        // O TestServer não tem porta local (0): o middleware simula a porta em que a conexão chegou.
        await using var app = await TestApp.StartAsync(
            endpoints: a => a.Use((context, next) =>
            {
                context.Connection.LocalPort = localPort;
                return next();
            }),
            settings: [("HealthChecks:ManagementPort", "8081")]);
        var client = app.GetTestClient();

        // Host forjado com a porta de gerência: não muda nada, vale a porta da conexão.
        using var live = await client.GetAsync("http://x:8081/health/live");
        using var ready = await client.GetAsync("http://x:8081/health/ready");

        await Assert.That(live.StatusCode).IsEqualTo(expected);
        await Assert.That(ready.StatusCode).IsEqualTo(expected);
    }

    [Test]
    public async Task ManagementPort_with_AllowedHosts_is_not_bypassed_by_forged_host()
    {
        await using var app = await TestApp.StartAsync(
            endpoints: a => a.Use((context, next) =>
            {
                context.Connection.LocalPort = 8080; // porta pública
                return next();
            }),
            settings: [("HealthChecks:ManagementPort", "8081"), ("HealthChecks:AllowedHosts:0", "*:8081")]);

        using var response = await app.GetTestClient().GetAsync("http://x:8081/health/live");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    [Arguments("0")]
    [Arguments("65536")]
    [Arguments("-1")]
    public async Task ManagementPort_out_of_range_is_rejected(string port) =>
        await Assert.That(() => Register(("HealthChecks:ManagementPort", port))).Throws<OptionsValidationException>();

    [Test]
    public async Task Configured_health_routes_never_generate_trace()
    {
        var services = new ServiceCollection();
        services.AddTecObservability(TestApp.Configuration(
            ("HealthChecks:LiveEndpoint", "/vivo"), ("HealthChecks:ReadyEndpoint", "/pronto"), ("IgnoredPaths:0", "/swagger")));
        await using var provider = services.BuildServiceProvider();
        var filter = provider.GetRequiredService<RequestPathFilter>();

        await Assert.That(filter.ShouldTrace(new PathString("/vivo"))).IsFalse();
        await Assert.That(filter.ShouldTrace(new PathString("/pronto"))).IsFalse();
        await Assert.That(filter.ShouldTrace(new PathString("/pedidos"))).IsTrue();
    }

    // ---------- Sondagens HTTP ----------

    [Test]
    public async Task Probe_does_not_follow_redirects_nor_propagate_context_to_third_parties()
    {
        using var handler = HttpEndpointHealthCheck.CreateHandler();

        await Assert.That(handler.AllowAutoRedirect).IsFalse();
        await Assert.That(handler.UseCookies).IsFalse();
        await Assert.That(handler.ActivityHeadersPropagator).IsNull();
    }

    [Test]
    public async Task Redirect_counts_as_failure()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://169.254.169.254/latest/meta-data/") } });
        await using var app = await TestApp.StartAsync(o => o.AddExternalServiceHealthCheck("PaymentGateway", Api), handler);

        using var response = await app.GetTestClient().GetAsync("/health/ready");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Discovery_document_above_limit_is_rejected()
    {
        var huge = $$"""{"issuer":"https://sso.exemplo.com","lixo":"{{new string('x', HttpEndpointHealthCheck.MaxDiscoveryDocumentBytes)}}"}""";
        var handler = FakeHttpHandler.Status(HttpStatusCode.OK, huge);
        await using var app = await TestApp.StartAsync(o => o.AddSsoHealthCheck("SSO", new Uri("https://sso.exemplo.com")), handler);

        using var response = await app.GetTestClient().GetAsync("/health/ready");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    [Test]
    public async Task Url_with_credentials_is_rejected()
    {
        var builder = new ServiceCollection().AddTecObservability(TestApp.Configuration());

        await Assert.That(() => builder.AddExternalServiceHealthCheck("api", new Uri("https://usuario:senha@api.exemplo.com/health")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Duplicate_health_check_name_is_rejected_at_registration()
    {
        var builder = new ServiceCollection().AddTecObservability(TestApp.Configuration());
        builder.AddDatabaseHealthCheck("PrimaryDb", SqliteFactory.Instance, "Data Source=:memory:");

        await Assert.That(() => builder.AddExternalServiceHealthCheck("primarydb", Api)).Throws<ArgumentException>();
        await Assert.That(() => builder.AddExternalServiceHealthCheck("self", Api)).Throws<ArgumentException>();
    }

    // ---------- Configuração ----------

    [Test]
    [Arguments("/")]
    [Arguments("")]
    [Arguments(" ")]
    public async Task IgnoredPaths_that_would_disable_all_tracing_is_rejected(string path) =>
        await Assert.That(() => Register(("IgnoredPaths:0", path))).Throws<OptionsValidationException>();

    [Test]
    [Arguments("x-api-key,x-outro")]
    [Arguments("x=y")]
    [Arguments("com espaco")]
    public async Task Invalid_otlp_header_name_is_rejected(string name) =>
        await Assert.That(() => Register(("Provider", "Otlp"), ("Otlp:Endpoint", "https://collector:4317"), ($"Otlp:Headers:{name}", "v")))
            .Throws<OptionsValidationException>();

    [Test]
    public async Task Otlp_headers_without_tls_to_remote_host_are_rejected() =>
        await Assert.That(() => Register(("Provider", "Otlp"), ("Otlp:Endpoint", "http://collector:4317"), ("Otlp:Headers:x-api-key", "segredo")))
            .Throws<OptionsValidationException>();

    [Test]
    [Arguments("http://localhost:4317", null)]
    [Arguments("http://127.0.0.1:4317", null)]
    [Arguments("https://collector:4317", null)]
    [Arguments("http://collector:4317", "true")]
    public async Task Otlp_headers_are_accepted_with_tls_sidecar_or_explicit_opt_in(string endpoint, string? allowInsecure) =>
        await Assert.That(() => Register(("Provider", "Otlp"), ("Otlp:Endpoint", endpoint), ("Otlp:Headers:x-api-key", "segredo"),
            ("Otlp:AllowInsecureTransport", allowInsecure))).ThrowsNothing();

    [Test]
    [Arguments("http://collector:4317", null, false)]
    [Arguments("https://collector:4317", null, true)]
    [Arguments("http://localhost:4317", null, true)]
    [Arguments("http://collector:4317", "true", true)]
    public async Task Headers_from_OTEL_EXPORTER_OTLP_HEADERS_variable_also_require_tls(string endpoint, string? allowInsecure, bool accepted)
    {
        var settings = TestApp.Settings(("Provider", "Otlp"), ("Otlp:Endpoint", endpoint), ("Otlp:AllowInsecureTransport", allowInsecure));
        settings["OTEL_EXPORTER_OTLP_HEADERS"] = "x-api-key=segredo";
        var services = new ServiceCollection();
        services.AddTecObservability(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        await using var provider = services.BuildServiceProvider();

        var read = () => provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;

        if (accepted)
            await Assert.That(read).ThrowsNothing();
        else
            await Assert.That(read).Throws<OptionsValidationException>();
    }

    [Test]
    public async Task Otlp_without_headers_accepts_http_inside_cluster() =>
        await Assert.That(() => Register(("Provider", "Otlp"), ("Otlp:Endpoint", "http://otel-collector:4317"))).ThrowsNothing();

    // ---------- Exposição do JSON de health ----------

    [Test]
    public async Task Without_details_response_has_only_status_and_timestamp()
    {
        await using var app = await TestApp.StartAsync(
            o => o.AddDatabaseHealthCheck("PrimaryDb", SqliteFactory.Instance, "Data Source=Z:\\nao-existe\\banco.db;Mode=ReadOnly"),
            settings: [("HealthChecks:ExposeDetails", "false")]);

        using var response = await app.GetTestClient().GetAsync("/health/ready");
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var properties = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(properties).IsEquivalentTo(["status", "timestamp"]);
        await Assert.That(json.RootElement.GetProperty("status").GetString()).IsEqualTo("Unhealthy");
    }

    // ---------- Baggage recebido ----------

    [Test]
    [Arguments("None")]
    [Arguments("Otlp")]
    public async Task Client_sent_baggage_is_discarded_and_correlation_id_remains(string provider)
    {
        await using var app = await StartBaggageAppAsync(provider, allowInboundBaggage: null);

        var body = await SendWithBaggageAsync(app);

        await Assert.That(body).IsEqualTo("otel=;activity=;correlation=pedido-42");
    }

    [Test]
    public async Task Received_baggage_is_kept_when_allowed()
    {
        await using var app = await StartBaggageAppAsync("Otlp", allowInboundBaggage: "true");

        var body = await SendWithBaggageAsync(app);

        await Assert.That(body).StartsWith("otel=interno;");
        await Assert.That(body).EndsWith(";correlation=pedido-42");
    }

    private static Task<WebApplication> StartBaggageAppAsync(string provider, string? allowInboundBaggage) =>
        TestApp.StartAsync(
            endpoints: a => a.MapGet("/eco", () =>
                $"otel={OpenTelemetry.Baggage.GetBaggage("tenant")};activity={Activity.Current?.GetBaggageItem("tenant")};correlation={CorrelationId.Current}"),
            settings: [("Provider", provider), ("Otlp:Endpoint", "http://localhost:4317"), ("AllowInboundBaggage", allowInboundBaggage)]);

    private static async Task<string> SendWithBaggageAsync(WebApplication app)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/eco");
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        request.Headers.Add("baggage", "tenant=interno");
        request.Headers.Add(CorrelationId.HeaderName, "pedido-42");
        using var response = await app.GetTestClient().SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    [Test]
    [Arguments("a,b")]
    [Arguments("quebra\nde-linha")]
    public async Task Otlp_header_value_that_would_break_export_is_rejected(string value) =>
        await Assert.That(() => Register(("Provider", "Otlp"), ("Otlp:Endpoint", "https://collector:4317"), ("Otlp:Headers:x-api-key", value)))
            .Throws<OptionsValidationException>();

    [Test]
    public async Task Otlp_endpoint_with_credentials_in_url_is_rejected() =>
        await Assert.That(() => Register(("Provider", "Otlp"), ("Otlp:Endpoint", "http://usuario:senha@collector:4317")))
            .Throws<OptionsValidationException>();

    [Test]
    [Arguments("-00:00:01")]
    [Arguments("00:05:00")]
    public async Task CacheDuration_out_of_range_is_rejected(string duration) =>
        await Assert.That(() => Register(("HealthChecks:CacheDuration", duration))).Throws<OptionsValidationException>();

    [Test]
    [Arguments("00:00:00.500")]
    [Arguments("00:05:01")]
    public async Task Health_checks_timeout_out_of_range_is_rejected(string timeout) =>
        await Assert.That(() => Register(("HealthChecks:Timeout", timeout))).Throws<OptionsValidationException>();

    // ---------- OTLP ----------

    [Test]
    public async Task Otlp_http_export_uses_signal_path_and_configured_headers()
    {
        var serviceName = $"otlp-{Guid.NewGuid():N}";
        using var source = new ActivitySource(serviceName);
        var collector = new CapturingHandler();

        await using var app = await TestApp.StartAsync(
            services: s => s.Configure<OtlpExporterOptions>(o => o.HttpClientFactory = () => new HttpClient(collector, disposeHandler: false)),
            settings:
            [
                ("ServiceName", serviceName), ("Provider", "Otlp"), ("Otlp:Protocol", "HttpProtobuf"),
                ("Otlp:Endpoint", "https://collector.exemplo.com:4318"),
                ("Otlp:Headers:x-api-key", "Basic dXNlcjpzZW5oYQ==; espaço e 100%"),
            ]);

        using (source.StartActivity("Operacao"))
        {
        }

        app.Services.GetRequiredService<TracerProvider>().ForceFlush(10_000);

        var traces = collector.Requests.Single(r => r.Uri.AbsolutePath == "/v1/traces");
        await Assert.That(traces.Uri.Authority).IsEqualTo("collector.exemplo.com:4318");
        await Assert.That(traces.ApiKey).IsEqualTo("Basic dXNlcjpzZW5oYQ==; espaço e 100%");
    }

    private sealed class GatedHandler(Action onCall, Task release) : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onCall();
            Started.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // ---------- Detalhes do JSON de health por ambiente (seguro por padrão) ----------

    [Test]
    [Arguments("Production", false)]
    [Arguments("Staging", false)]
    [Arguments("Development", true)]
    public async Task Health_json_details_depend_on_environment_by_default(string environment, bool details)
    {
        await using var app = await TestApp.StartAsync(environment: environment);

        using var response = await app.GetTestClient().GetAsync("/health/live");
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var properties = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(properties.Contains("checks")).IsEqualTo(details);
        await Assert.That(properties.Contains("service")).IsEqualTo(details);
        await Assert.That(app.Services.GetRequiredService<IOptions<ObservabilityOptions>>().Value.HealthChecks.ExposeDetails).IsEqualTo(details);
    }

    [Test]
    public async Task ExposeDetails_outside_development_without_ManagementPort_prevents_startup() =>
        await Assert.That(async () => await TestApp.StartAsync(environment: "Production", settings: [("HealthChecks:ExposeDetails", "true")]))
            .Throws<OptionsValidationException>();

    [Test]
    public async Task ExposeDetails_outside_development_with_only_AllowedHosts_prevents_startup()
    {
        // AllowedHosts confere o cabeçalho Host, que o cliente escolhe: não libera o detalhe.
        var exception = await Assert.That(async () => await TestApp.StartAsync(environment: "Production",
                settings: [("HealthChecks:ExposeDetails", "true"), ("HealthChecks:AllowedHosts:0", "*:8081")]))
            .Throws<OptionsValidationException>();

        await Assert.That(exception!.Message).Contains("HealthChecks:ManagementPort");
    }

    [Test]
    public async Task ExposeDetails_outside_development_with_ManagementPort_is_accepted()
    {
        await using var app = await TestApp.StartAsync(environment: "Production",
            endpoints: a => a.Use((context, next) =>
            {
                context.Connection.LocalPort = 8081;
                return next();
            }),
            settings: [("HealthChecks:ExposeDetails", "true"), ("HealthChecks:ManagementPort", "8081")]);

        using var response = await app.GetTestClient().GetAsync("/health/live");
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        await Assert.That(json.RootElement.TryGetProperty("checks", out _)).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Liveness_only_reports_process_start_and_uptime_with_details(bool exposeDetails)
    {
        var options = Options.Create(new ObservabilityOptions { HealthChecks = { ExposeDetails = exposeDetails } });
        var check = new LivenessHealthCheck(options, TimeProvider.System);
        var context = new Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext
        {
            Registration = new("self", check, failureStatus: null, tags: null),
        };

        var result = await check.CheckHealthAsync(context);

        await Assert.That(result.Data.ContainsKey("startedAt")).IsEqualTo(exposeDetails);
        await Assert.That(result.Data.ContainsKey("uptimeSeconds")).IsEqualTo(exposeDetails);
    }

    // ---------- Execução compartilhada dos health checks ----------

    [Test]
    public async Task Shared_execution_does_not_inherit_triggering_request_context()
    {
        string? correlationSeen = "não executou";
        string? activitySeen = "não executou";
        await using var app = await TestApp.StartAsync(o => o.HealthChecks.AddCheck("contexto", () =>
        {
            correlationSeen = CorrelationId.Current;
            activitySeen = Activity.Current?.TraceId.ToString();
            return Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy();
        }, tags: [HealthCheckTags.Ready]));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health/ready");
        request.Headers.Add(CorrelationId.HeaderName, "primeiro-chamador");
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        using var response = await app.GetTestClient().SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(correlationSeen).IsNull();
        await Assert.That(activitySeen).IsNull();
    }

    // ---------- Sondagens fora dos traces (com a distro do Azure: TEC.Observability.Azure.Tests) ----------

    [Test]
    [Arguments("Otlp")]
    [Arguments("Console")]
    public async Task Health_check_probe_does_not_generate_outgoing_span(string provider)
    {
        await using var server = new RawHttpServer();
        var target = new Uri($"http://127.0.0.1:{server.Port}/health/live");
        var exported = new List<Activity>();

        // O handler padrão das sondagens (sem propagador) nem cria span; aqui o serviço trocou o handler por um com diagnóstico
        // ligado, e quem impede o span é o filtro da instrumentação de HttpClient.
        await using var app = await TestApp.StartAsync(
            o => o.AddExternalServiceHealthCheck("Dependencia", target, h => h.Retries = 0),
            services: s =>
            {
                s.ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported));
                s.AddHttpClient(HttpEndpointHealthCheck.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler());
            },
            endpoints: a => a.MapGet("/chamar", async (IHttpClientFactory http) =>
            {
                using var response = await http.CreateClient().GetAsync(target);
                return (int)response.StatusCode;
            }),
            settings:
            [
                ("Provider", provider), ("Otlp:Endpoint", "http://localhost:4317"),
            ]);
        var client = app.GetTestClient();

        (await client.GetAsync("/health/ready")).Dispose();
        (await client.GetAsync("/chamar")).Dispose(); // controle: chamada de negócio ao mesmo destino gera span
        app.Services.GetRequiredService<TracerProvider>().ForceFlush(5_000);

        // Cópia com ToArray: o exportador em memória continua recebendo spans de outros hosts do processo, sem lock na lista
        List<Activity> outgoing = [.. exported.ToArray().OfType<Activity>().Where(a => a.Kind == ActivityKind.Client && Equals(a.GetTagItem("server.port"), server.Port))];

        await Assert.That(server.Requests.Count).IsEqualTo(2);
        await Assert.That(outgoing.Count).IsEqualTo(1);
    }

    // ---------- Propagação do Baggage (correlation id) só para hosts permitidos ----------

    [Test]
    [Arguments(null, false, false)]
    [Arguments("localhost", true, false)]
    [Arguments("*", true, true)]
    public async Task Baggage_with_correlation_id_is_only_sent_to_allowed_hosts(string? allowed, bool toLocalhost, bool toIp)
    {
        await using var server = new RawHttpServer();
        await using var app = await TestApp.StartAsync(
            endpoints: a => a.MapGet("/chamar", async (IHttpClientFactory http) =>
            {
                using var client = http.CreateClient();
                (await client.GetAsync($"http://localhost:{server.Port}/a")).Dispose();
                (await client.GetAsync($"http://127.0.0.1:{server.Port}/b")).Dispose();
                return "ok";
            }),
            settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317"), allowed is null ? ("Sem", null) : ("BaggageAllowedHosts:0", allowed)]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/chamar");
        request.Headers.Add(CorrelationId.HeaderName, "pedido-42");
        (await app.GetTestClient().SendAsync(request)).Dispose();

        var localhostRequest = server.Requests.Single(r => r.StartsWith("GET /a ", StringComparison.Ordinal));
        var ipRequest = server.Requests.Single(r => r.StartsWith("GET /b ", StringComparison.Ordinal));

        // O traceparent segue para todos; só o Baggage é filtrado.
        await Assert.That(localhostRequest).Contains("traceparent:", StringComparison.OrdinalIgnoreCase);
        await Assert.That(ipRequest).Contains("traceparent:", StringComparison.OrdinalIgnoreCase);
        await Assert.That(localhostRequest.Contains("correlation.id=pedido-42", StringComparison.Ordinal)).IsEqualTo(toLocalhost);
        await Assert.That(ipRequest.Contains("correlation.id=pedido-42", StringComparison.Ordinal)).IsEqualTo(toIp);
    }

    [Test]
    [Arguments("None")]
    [Arguments("Otlp")]
    public async Task Activity_baggage_is_also_only_sent_to_allowed_hosts(string provider)
    {
        // Com AllowInboundBaggage o Baggage recebido fica no Activity, e quem o escreve na saída é o HttpClient
        // (DistributedContextPropagator), por fora do propagador do OpenTelemetry — inclusive com Provider None.
        await using var server = new RawHttpServer();
        await using var app = await TestApp.StartAsync(
            endpoints: a => a.MapGet("/chamar", async (IHttpClientFactory http) =>
            {
                using var client = http.CreateClient();
                (await client.GetAsync($"http://localhost:{server.Port}/a")).Dispose();
                (await client.GetAsync($"http://127.0.0.1:{server.Port}/b")).Dispose();
                return "ok";
            }),
            settings:
            [
                ("Provider", provider), ("Otlp:Endpoint", "http://localhost:4317"),
                ("AllowInboundBaggage", "true"), ("BaggageAllowedHosts:0", "localhost"),
            ]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/chamar");
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        request.Headers.Add("baggage", "tenant=x");
        (await app.GetTestClient().SendAsync(request)).Dispose();

        var allowedRequest = server.Requests.Single(r => r.StartsWith("GET /a ", StringComparison.Ordinal));
        var thirdPartyRequest = server.Requests.Single(r => r.StartsWith("GET /b ", StringComparison.Ordinal));

        // Com Provider None quem escreve é só o HttpClient, e a instrumentação do OpenTelemetry de outro teste em execução no
        // mesmo processo (os listeners são globais) pode reescrever o cabeçalho: o envio ao host permitido, nesse caso, é
        // conferido sem interferência no teste do propagador, logo abaixo.
        if (provider == "Otlp")
            await Assert.That(allowedRequest.Replace(" ", "", StringComparison.Ordinal)).Contains("tenant=x", StringComparison.Ordinal);
        await Assert.That(thirdPartyRequest).Contains("traceparent:", StringComparison.OrdinalIgnoreCase);
        await Assert.That(thirdPartyRequest).DoesNotContain("baggage:", StringComparison.OrdinalIgnoreCase);
        await Assert.That(thirdPartyRequest).DoesNotContain("Correlation-Context:", StringComparison.OrdinalIgnoreCase);
        await Assert.That(thirdPartyRequest).DoesNotContain("tenant", StringComparison.Ordinal);
    }

    [Test]
    [Arguments("localhost", true)]
    [Arguments("127.0.0.1", false)]
    public async Task Dotnet_propagator_only_writes_activity_baggage_to_allowed_hosts(string host, bool allowed)
    {
        var propagator = new HostFilteringDistributedContextPropagator(DistributedContextPropagator.CreateDefaultPropagator());
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:8080/a");
        using var activity = new Activity("saida").AddBaggage("tenant", "x").Start();

        BaggageHostPolicy.Current = new BaggageHostPolicy(["localhost"]);
        try
        {
            propagator.Inject(activity, request, (_, name, value) => written[name] = value);
        }
        finally
        {
            BaggageHostPolicy.Current = null;
        }

        await Assert.That(written.ContainsKey("traceparent")).IsTrue();
        await Assert.That(written.Keys.Any(HostFilteringDistributedContextPropagator.IsBaggageField)).IsEqualTo(allowed);
        await Assert.That(written.Values.Any(v => v.Contains("tenant", StringComparison.Ordinal))).IsEqualTo(allowed);
    }

    [Test]
    public async Task Dotnet_propagator_also_filters_outside_middleware_handled_request()
    {
        // Sem política da requisição vale a do processo (definida por outros testes só com "localhost") ou nenhuma: em ambos os
        // casos 127.0.0.1 não recebe o Baggage — um serviço em segundo plano não vaza o Baggage para terceiros.
        var propagator = new HostFilteringDistributedContextPropagator(DistributedContextPropagator.CreateDefaultPropagator());
        var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8080/a");
        using var activity = new Activity("saida").AddBaggage("tenant", "x").Start();

        BaggageHostPolicy.Current = null;
        propagator.Inject(activity, request, (_, name, value) => written[name] = value);

        await Assert.That(written.ContainsKey("traceparent")).IsTrue();
        await Assert.That(written.Keys.Any(HostFilteringDistributedContextPropagator.IsBaggageField)).IsFalse();
    }

    [Test]
    [Arguments("baggage", true)]
    [Arguments("Baggage", true)]
    [Arguments("correlation-context", true)]
    [Arguments("traceparent", false)]
    [Arguments("tracestate", false)]
    public async Task Dotnet_propagator_discards_only_baggage_fields(string field, bool baggage) =>
        await Assert.That(HostFilteringDistributedContextPropagator.IsBaggageField(field)).IsEqualTo(baggage);

    // ---------- Cofres de segredos fora dos traces de HttpClient ----------

    [Test]
    [Arguments("https://kv-minha-app.vault.azure.net/secrets/senha-do-banco/0123456789abcdef", false)]
    [Arguments("https://KV-MINHA-APP.VAULT.AZURE.NET/secrets/x", false)]
    [Arguments("https://kv.vault.azure.cn/secrets/x", false)]
    [Arguments("https://kv.vault.usgovcloudapi.net/secrets/x", false)]
    [Arguments("https://kv.vault.microsoftazure.de/secrets/x", false)]
    [Arguments("https://hsm.managedhsm.azure.net/keys/x", false)]
    [Arguments("https://vault.azure.net.evil.io/secrets/x", true)]
    [Arguments("https://pagamentos.exemplo.com/vault.azure.net", true)]
    [Arguments("https://pagamentos.exemplo.com/pedidos", true)]
    public async Task Calls_to_azure_vaults_do_not_generate_outgoing_span(string url, bool traced)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        await Assert.That(ServiceCollectionExtensions.ShouldTraceOutgoing(request)).IsEqualTo(traced);
    }

    [Test]
    public async Task Outgoing_filter_still_discards_probes()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://pagamentos.exemplo.com/health/live");
        request.Options.Set(HttpEndpointHealthCheck.ProbeKey, true);

        await Assert.That(ServiceCollectionExtensions.ShouldTraceOutgoing(request)).IsFalse();
    }

    // ---------- Redação de URLs nos spans ----------

    [Test]
    [Arguments("?token=SEGREDO", "?token=Redacted")]
    [Arguments("?a=1&b=2", "?a=Redacted&b=Redacted")]
    [Arguments("?a=1&flag&b=", "?a=Redacted&Redacted&b=Redacted")]
    [Arguments("?a==x&b=y", "?a=Redacted&b=Redacted")]
    [Arguments("?sem-valor", "?Redacted")]
    [Arguments("?eyJhbGciOi.SEGREDO", "?Redacted")]
    [Arguments("?", "?")]
    [Arguments("?*", "?*")]
    [Arguments("", "")]
    public async Task Query_string_is_redacted_in_instrumentation_format(string query, string expected) =>
        await Assert.That(TelemetryRedaction.RedactQuery(query)).IsEqualTo(expected);

    [Test]
    [Arguments("https://api.exemplo.com/v1?api_key=SEGREDO", "https://api.exemplo.com/v1?api_key=SEGREDO", "https://api.exemplo.com/v1?api_key=Redacted")]
    [Arguments("https://usuario:senha@api.exemplo.com/v1", "https://usuario:senha@api.exemplo.com/v1", "https://api.exemplo.com/v1")]
    [Arguments("https://api.exemplo.com/v1?api_key=SEGREDO", "https://api.exemplo.com/v1?*", "https://api.exemplo.com/v1?*")]
    [Arguments("https://api.exemplo.com/v1?sem-valor", "https://api.exemplo.com/v1?sem-valor", "https://api.exemplo.com/v1?Redacted")]
    public async Task Unredacted_outgoing_url_returns_to_redacted_format(string requestUrl, string tag, string expected)
    {
        using var listener = SpanListener.Start(out var source);
        using var activity = source.StartActivity("GET", ActivityKind.Client)!;
        activity.SetTag("url.full", tag);
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);

        TelemetryRedaction.RedactClientUrl(activity);

        await Assert.That(activity.GetTagItem("url.full")).IsEqualTo(expected);
    }

    [Test]
    public async Task Incoming_request_query_string_is_redacted_and_service_enrich_still_runs()
    {
        var exported = new List<Activity>();
        await using var app = await TestApp.StartAsync(
            services: s => s
                .ConfigureOpenTelemetryTracerProvider(t => t.AddInMemoryExporter(exported))
                .Configure<OpenTelemetry.Instrumentation.AspNetCore.AspNetCoreTraceInstrumentationOptions>(o =>
                    o.EnrichWithHttpRequest = (activity, _) => activity.SetTag("servico.enrich", "sim")),
            endpoints: a => a.MapGet("/consulta", () => "ok"),
            settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317")]);

        (await app.GetTestClient().GetAsync("/consulta?token=SEGREDO&pagina=2")).Dispose();
        var server = await WaitForServerSpanAsync(exported, "/consulta");

        await Assert.That(server.GetTagItem("url.query")).IsEqualTo("?token=Redacted&pagina=Redacted");
        await Assert.That(server.GetTagItem("servico.enrich")).IsEqualTo("sim");
    }

    [Test]
    [Arguments("https://kv-app.vault.azure.net/secrets/senha-do-banco/0123abcd?api-version=7.5", "https://kv-app.vault.azure.net/secrets/***")]
    [Arguments("https://kv-app.vault.azure.net:443/keys/chave/v1/sign?api-version=7.5", "https://kv-app.vault.azure.net/keys/***")]
    [Arguments("https://KV-APP.VAULT.AZURE.CN/certificates/cert", "https://kv-app.vault.azure.cn/certificates/***")]
    [Arguments("https://hsm.managedhsm.azure.net/keys/chave", "https://hsm.managedhsm.azure.net/keys/***")]
    [Arguments("https://kv-app.vault.azure.net/secrets?maxresults=25", "https://kv-app.vault.azure.net/secrets")]
    [Arguments("https://vault.azure.net.evil.io/secrets/x", "https://vault.azure.net.evil.io/secrets/x")]
    [Arguments("https://pagamentos.exemplo.com/secrets/x?a=1", "https://pagamentos.exemplo.com/secrets/x?a=Redacted")]
    public async Task Vault_call_span_loses_secret_name_and_version(string url, string expected)
    {
        using var listener = SpanListener.Start(out var source);
        using var activity = source.StartActivity($"GET {new Uri(url).AbsolutePath}", ActivityKind.Client)!;
        activity.SetTag("url.full", url);
        activity.SetTag("http.url", url);
        activity.SetTag("http.response.status_code", 200);

        SpanRedactionProcessor.Redact(activity);
        var redactedPath = new Uri(expected).AbsolutePath;

        await Assert.That(activity.GetTagItem("url.full")).IsEqualTo(expected);
        await Assert.That(activity.GetTagItem("http.url")).IsEqualTo(expected);
        await Assert.That(activity.DisplayName).IsEqualTo($"GET {redactedPath}");
        await Assert.That(activity.GetTagItem("http.response.status_code")).IsEqualTo(200);
    }

    [Test]
    public async Task Vault_span_identified_by_server_address_is_also_redacted()
    {
        using var listener = SpanListener.Start(out var source);
        using var activity = source.StartActivity("GET", ActivityKind.Client)!;
        activity.SetTag("server.address", "kv-app.vault.azure.net");
        activity.SetTag("url.path", "/secrets/senha-do-banco/0123abcd");
        activity.SetTag("url.query", "?api-version=7.5");
        activity.SetTag("http.target", "/secrets/senha-do-banco/0123abcd?api-version=7.5");

        SpanRedactionProcessor.Redact(activity);

        await Assert.That(activity.GetTagItem("url.path")).IsEqualTo("/secrets/***");
        await Assert.That(activity.GetTagItem("http.target")).IsEqualTo("/secrets/***");
        await Assert.That(activity.GetTagItem("url.query")).IsNull();
    }

    [Test]
    public async Task Key_vault_attributes_in_azure_sdk_spans_are_redacted()
    {
        using var listener = SpanListener.Start(out var source, "Azure.Security.KeyVault.Teste");
        using var activity = source.StartActivity("SecretClient.GetSecret", ActivityKind.Internal)!;
        activity.SetTag("az.keyvault.secret.name", "senha-do-banco");
        activity.SetTag("az.keyvault.secret.version", "0123abcd");
        activity.SetTag("az.namespace", "Microsoft.KeyVault");

        SpanRedactionProcessor.Redact(activity);

        await Assert.That(activity.GetTagItem("az.keyvault.secret.name")).IsEqualTo("***");
        await Assert.That(activity.GetTagItem("az.keyvault.secret.version")).IsEqualTo("***");
        await Assert.That(activity.GetTagItem("az.namespace")).IsEqualTo("Microsoft.KeyVault");
    }

    [Test]
    public async Task Server_span_is_not_changed_by_vault_redaction()
    {
        using var listener = SpanListener.Start(out var source);
        using var activity = source.StartActivity("GET /secrets/{nome}", ActivityKind.Server)!;
        activity.SetTag("url.full", "https://kv-app.vault.azure.net/secrets/nome");

        SpanRedactionProcessor.Redact(activity);

        await Assert.That(activity.GetTagItem("url.full")).IsEqualTo("https://kv-app.vault.azure.net/secrets/nome");
    }

    /// <summary>Fonte com um listener que grava tudo, para criar spans de teste com o <see cref="ActivityKind"/> desejado.</summary>
    private sealed class SpanListener : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly ActivitySource _source;

        private SpanListener(string name)
        {
            _source = new ActivitySource(name);
            _listener = new ActivityListener
            {
                ShouldListenTo = s => s.Name == name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public static SpanListener Start(out ActivitySource source, string prefix = "teste-redacao-")
        {
            var listener = new SpanListener($"{prefix}{Guid.NewGuid():N}");
            source = listener._source;
            return listener;
        }

        public void Dispose()
        {
            _listener.Dispose();
            _source.Dispose();
        }
    }

    private static async Task<Activity> WaitForServerSpanAsync(List<Activity> exported, string path)
    {
        // O span da requisição é encerrado pelo servidor depois que a resposta já chegou ao cliente. Cópia com ToArray antes de
        // procurar: o exportador em memória adiciona spans (de qualquer host do processo) sem o lock da lista.
        for (var i = 0; i < 100; i++)
        {
            if (exported.ToArray().OfType<Activity>().FirstOrDefault(a => a.Kind == ActivityKind.Server && Equals(a.GetTagItem("url.path"), path)) is { } span)
                return span;
            await Task.Delay(50);
        }

        throw new TimeoutException($"Span de servidor de {path} não exportado.");
    }

    [Test]
    [Arguments("*.")]
    [Arguments("api.*.com")]
    [Arguments("com espaco")]
    public async Task Invalid_BaggageAllowedHosts_is_rejected(string pattern) =>
        await Assert.That(() => Register(("BaggageAllowedHosts:0", pattern))).Throws<OptionsValidationException>();

    [Test]
    [Arguments("pedidos-api", "pedidos-api", true)]
    [Arguments("*.svc.cluster.local", "pedidos.ns.svc.cluster.local", true)]
    [Arguments("*.svc.cluster.local", "svc.cluster.local", false)]
    [Arguments("*.empresa.com", "empresa.com.evil.io", false)]
    [Arguments("pedidos-api", "pagamentos.exemplo.com", false)]
    public async Task Baggage_host_policy(string pattern, string host, bool allowed) =>
        await Assert.That(new BaggageHostPolicy([pattern]).Allows(host)).IsEqualTo(allowed);
}
