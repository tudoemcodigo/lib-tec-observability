using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TEC.Observability.DependencyInjection;

namespace TEC.Observability.Tests;

/// <summary>Endpoints de liveness/readiness, saída JSON e as verificações de banco, SSO e serviço externo.</summary>
public class HealthCheckTests
{
    private const string DatabaseOk = "Data Source=:memory:";
    private const string MissingDatabase = "Data Source=Z:\\nao-existe\\banco.db;Mode=ReadOnly";
    private const string Discovery = """{"issuer":"https://sso.exemplo.com/realms/tec","jwks_uri":"https://sso.exemplo.com/jwks"}""";

    private static readonly Uri Api = new("https://pagamentos.exemplo.com/health/live");

    private static async Task<(HttpStatusCode Status, JsonElement Json)> GetAsync(WebApplication app, string path)
    {
        using var response = await app.GetTestClient().GetAsync(path);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (response.StatusCode, document.RootElement.Clone());
    }

    private static JsonElement Check(JsonElement json, string name) =>
        json.GetProperty("checks").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static string[] Tags(JsonElement check) =>
        [.. check.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!)];

    // ---------- Liveness x readiness ----------

    [Test]
    public async Task Liveness_does_not_run_dependency_checks()
    {
        await using var app = await TestApp.StartAsync(o => o.AddDatabaseHealthCheck("PrimaryDb", SqliteFactory.Instance, MissingDatabase));

        var (status, json) = await GetAsync(app, "/health/live");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(json.GetProperty("status").GetString()).IsEqualTo("Healthy");
        await Assert.That(json.GetProperty("checks").GetArrayLength()).IsEqualTo(1);
        await Assert.That(Check(json, "self").GetProperty("tags")[0].GetString()).IsEqualTo("live");
    }

    [Test]
    public async Task Readiness_includes_service_header_and_details_of_each_check()
    {
        await using var app = await TestApp.StartAsync(o => o.AddDatabaseHealthCheck("PrimaryDb", SqliteFactory.Instance, DatabaseOk));

        var (status, json) = await GetAsync(app, "/health/ready");
        var check = Check(json, "PrimaryDb");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(json.GetProperty("service").GetString()).IsEqualTo("testes-api");
        await Assert.That(json.GetProperty("version").GetString()).IsEqualTo("1.2.3");
        await Assert.That(json.GetProperty("environment").GetString()).IsEqualTo("test");
        await Assert.That(json.GetProperty("timestamp").GetDateTimeOffset()).IsGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-1));
        await Assert.That(json.GetProperty("totalDurationMs").GetDouble()).IsGreaterThanOrEqualTo(0);
        await Assert.That(json.GetProperty("checks").GetArrayLength()).IsEqualTo(1);
        await Assert.That(check.GetProperty("status").GetString()).IsEqualTo("Healthy");
        await Assert.That(check.GetProperty("description").GetString()).IsEqualTo("Banco de dados acessível.");
        await Assert.That(Tags(check)).IsEquivalentTo(["ready", "database"]);
        await Assert.That(check.TryGetProperty("exception", out _)).IsFalse();
    }

    [Test]
    public async Task Configured_endpoints_replace_defaults()
    {
        await using var app = await TestApp.StartAsync(settings: [("HealthChecks:LiveEndpoint", "/vivo"), ("HealthChecks:ReadyEndpoint", "/pronto")]);

        using var customResponse = await app.GetTestClient().GetAsync("/vivo");
        using var defaultResponse = await app.GetTestClient().GetAsync("/health/live");

        await Assert.That(customResponse.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(defaultResponse.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Disabled_health_checks_do_not_map_endpoints()
    {
        await using var app = await TestApp.StartAsync(settings: [("HealthChecks:Enabled", "false")]);

        using var response = await app.GetTestClient().GetAsync("/health/live");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    // ---------- Banco de dados ----------

    [Test]
    public async Task Unreachable_database_returns_503_without_exposing_exception_message()
    {
        await using var app = await TestApp.StartAsync(o => o.AddDatabaseHealthCheck("PrimaryDb", SqliteFactory.Instance, MissingDatabase));

        var (status, json) = await GetAsync(app, "/health/ready");
        var check = Check(json, "PrimaryDb");

        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(json.GetProperty("status").GetString()).IsEqualTo("Unhealthy");
        await Assert.That(check.GetProperty("description").GetString()).IsEqualTo("Banco de dados inacessível.");
        await Assert.That(check.GetProperty("exception").GetProperty("type").GetString()).IsEqualTo(nameof(SqliteException));
        await Assert.That(check.GetProperty("exception").TryGetProperty("message", out _)).IsFalse();
        await Assert.That(json.GetRawText()).DoesNotContain("nao-existe");
    }

    [Test]
    public async Task Exception_message_appears_when_enabled()
    {
        await using var app = await TestApp.StartAsync(
            o => o.AddDatabaseHealthCheck("PrimaryDb", SqliteFactory.Instance, MissingDatabase),
            settings: [("HealthChecks:ExposeExceptionDetails", "true")]);

        var (_, json) = await GetAsync(app, "/health/ready");

        await Assert.That(Check(json, "PrimaryDb").GetProperty("exception").GetProperty("message").GetString()).IsNotEmpty();
    }

    [Test]
    public async Task Invalid_query_reports_failure()
    {
        await using var app = await TestApp.StartAsync(o => o.AddDatabaseHealthCheck("PrimaryDb",
            () => new SqliteConnection(DatabaseOk), query: "SELECT 1 FROM tabela_que_nao_existe"));

        var (status, _) = await GetAsync(app, "/health/ready");

        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    // ---------- SSO ----------

    [Test]
    public async Task Healthy_sso_queries_discovery_document()
    {
        var handler = FakeHttpHandler.Status(HttpStatusCode.OK, Discovery);
        await using var app = await TestApp.StartAsync(
            o => o.AddSsoHealthCheck("KeycloakSSO", new Uri("https://sso.exemplo.com/realms/tec")), handler);

        var (status, json) = await GetAsync(app, "/health/ready");
        var check = Check(json, "KeycloakSSO");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Tags(check)).IsEquivalentTo(["ready", "sso"]);
        await Assert.That(check.GetProperty("data").GetProperty("statusCode").GetInt32()).IsEqualTo(200);
        await Assert.That(handler.Requests.Single().AbsoluteUri).IsEqualTo("https://sso.exemplo.com/realms/tec/.well-known/openid-configuration");
    }

    [Test]
    [Arguments("""{"mensagem":"pagina de manutencao"}""")]
    [Arguments("<html>login</html>")]
    public async Task Sso_with_200_response_that_is_not_openid_discovery_fails(string body)
    {
        var handler = FakeHttpHandler.Status(HttpStatusCode.OK, body);
        await using var app = await TestApp.StartAsync(
            o => o.AddSsoHealthCheck("KeycloakSSO", new Uri("https://sso.exemplo.com/realms/tec")), handler);

        var (status, json) = await GetAsync(app, "/health/ready");

        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(Check(json, "KeycloakSSO").GetProperty("status").GetString()).IsEqualTo("Unhealthy");
    }

    // ---------- Serviço externo ----------

    [Test]
    public async Task Healthy_external_service()
    {
        var handler = FakeHttpHandler.Status(HttpStatusCode.OK);
        await using var app = await TestApp.StartAsync(o => o.AddExternalServiceHealthCheck("PaymentGateway", Api), handler);

        var (status, json) = await GetAsync(app, "/health/ready");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Tags(Check(json, "PaymentGateway")))
            .IsEquivalentTo(["ready", "external"]);
    }

    [Test]
    public async Task Failure_5xx_is_retried_before_reporting()
    {
        var handler = FakeHttpHandler.Status(HttpStatusCode.BadGateway);
        await using var app = await TestApp.StartAsync(o => o.AddExternalServiceHealthCheck("PaymentGateway", Api, h => h.Retries = 2), handler);

        var (status, json) = await GetAsync(app, "/health/ready");
        var check = Check(json, "PaymentGateway");

        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(handler.Requests.Count).IsEqualTo(3);
        await Assert.That(check.GetProperty("description").GetString()).IsEqualTo("Resposta HTTP 502.");
        await Assert.That(check.GetProperty("data").GetProperty("statusCode").GetInt32()).IsEqualTo(502);
    }

    [Test]
    public async Task Transient_failure_recovers_on_retry()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(Interlocked.Increment(ref calls) == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        await using var app = await TestApp.StartAsync(o => o.AddExternalServiceHealthCheck("PaymentGateway", Api), handler);

        var (status, _) = await GetAsync(app, "/health/ready");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(handler.Requests.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Failure_4xx_is_not_retried()
    {
        var handler = FakeHttpHandler.Status(HttpStatusCode.NotFound);
        await using var app = await TestApp.StartAsync(o => o.AddExternalServiceHealthCheck("PaymentGateway", Api), handler);

        var (status, _) = await GetAsync(app, "/health/ready");

        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Network_failure_reports_exception_type()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException("host interno 10.0.0.7 recusou a conexão"));
        await using var app = await TestApp.StartAsync(o => o.AddExternalServiceHealthCheck("PaymentGateway", Api, h => h.Retries = 0), handler);

        var (status, json) = await GetAsync(app, "/health/ready");

        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(Check(json, "PaymentGateway").GetProperty("exception").GetProperty("type").GetString()).IsEqualTo(nameof(HttpRequestException));
        await Assert.That(json.GetRawText()).DoesNotContain("10.0.0.7");
    }

    [Test]
    public async Task Timeout_reports_failure()
    {
        var handler = new SlowHandler();
        await using var app = await TestApp.StartAsync(
            o => o.AddExternalServiceHealthCheck("PaymentGateway", Api, h => { h.Timeout = TimeSpan.FromMilliseconds(100); h.Retries = 0; }),
            services: s => s.AddHttpClient(HealthChecks.HttpEndpointHealthCheck.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));

        var (status, json) = await GetAsync(app, "/health/ready");

        var check = Check(json, "PaymentGateway");
        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(check.GetProperty("exception").GetProperty("type").GetString()).IsEqualTo(nameof(TimeoutException));
        // A descrição do timeout não é a mensagem da exceção: o writer não a esconde.
        await Assert.That(check.GetProperty("description").GetString()).IsEqualTo("Sem resposta em 0.1s.");
    }

    // ---------- Banco com driver que ignora o cancelamento ----------

    [Test]
    public async Task Database_ignoring_cancellation_respects_timeout()
    {
        var connection = new IgnoringCancellationConnection(hangOnOpen: true);
        await using var app = await TestApp.StartAsync(o => o.AddDatabaseHealthCheck("Legado", () => connection, timeout: TimeSpan.FromMilliseconds(300)));

        var started = System.Diagnostics.Stopwatch.StartNew();
        var (status, json) = await GetAsync(app, "/health/ready").WaitAsync(TimeSpan.FromSeconds(15));
        started.Stop();
        connection.Release();

        await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(Check(json, "Legado").GetProperty("status").GetString()).IsEqualTo("Unhealthy");
        await Assert.That(started.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    [Arguments(300, 1)]
    [Arguments(5000, 5)]
    [Arguments(2500, 3)]
    public async Task Query_receives_CommandTimeout_from_health_check(int timeoutMs, int expectedSeconds)
    {
        var connection = new IgnoringCancellationConnection(hangOnOpen: false);
        await using var app = await TestApp.StartAsync(o => o.AddDatabaseHealthCheck("Legado", () => connection, timeout: TimeSpan.FromMilliseconds(timeoutMs)));

        var (status, _) = await GetAsync(app, "/health/ready");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(connection.CommandTimeout).IsEqualTo(expectedSeconds);
    }

    // ---------- Verificação travada (ignora o cancelamento e nunca termina) ----------

    [Test]
    public async Task Hung_check_returns_503_in_time_and_does_not_block_subsequent_requests()
    {
        var never = new TaskCompletionSource<HealthCheckResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var app = await TestApp.StartAsync(
            o => o.HealthChecks.AddAsyncCheck("Travado", _ =>
                Interlocked.Increment(ref calls) == 1 ? never.Task : Task.FromResult(HealthCheckResult.Healthy()), // de propósito, sem o token
                tags: [HealthChecks.HealthCheckTags.Ready]),
            settings: [("HealthChecks:Timeout", "00:00:01"), ("HealthChecks:CacheDuration", "00:00:00.200")]);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var (first, json) = await GetAsync(app, "/health/ready").WaitAsync(TimeSpan.FromSeconds(15));
        started.Stop();
        await Task.Delay(400); // passa o CacheDuration: a próxima requisição dispara execução nova
        var (later, _) = await GetAsync(app, "/health/ready").WaitAsync(TimeSpan.FromSeconds(15));
        never.TrySetResult(HealthCheckResult.Healthy());

        await Assert.That(first).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(json.GetProperty("status").GetString()).IsEqualTo("Unhealthy");
        await Assert.That(started.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(later).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(calls).IsEqualTo(2);
    }

    // ---------- Encerramento da aplicação ----------

    [Test]
    public async Task Shutdown_during_check_returns_503_not_500()
    {
        var started = new TaskCompletionSource();
        await using var app = await TestApp.StartAsync(o => o.HealthChecks.AddAsyncCheck("Lento", async cancellationToken =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return HealthCheckResult.Healthy();
        }, tags: [HealthChecks.HealthCheckTags.Ready]));

        var pending = app.GetTestClient().GetAsync("/health/ready");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
    }

    // ---------- Convivência com o .NET Aspire ServiceDefaults ----------

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Self_check_already_registered_by_aspire_is_reused(bool registeredBefore)
    {
        // Mesmo registro do AddServiceDefaults: AddCheck("self", () => Healthy(), ["live"]).
        static void AddAspireSelf(IServiceCollection s) => s.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy("aspire"), ["live"]);

        await using var app = await TestApp.StartAsync(
            servicesBefore: registeredBefore ? AddAspireSelf : null,
            services: registeredBefore ? null : AddAspireSelf);

        var (status, json) = await GetAsync(app, "/health/live");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(json.GetProperty("checks").GetArrayLength()).IsEqualTo(1);
        await Assert.That(Check(json, "self").GetProperty("description").GetString()).IsEqualTo("aspire");
    }

    [Test]
    public async Task Liveness_name_is_configurable()
    {
        await using var app = await TestApp.StartAsync(settings: [("HealthChecks:LivenessCheckName", "processo")]);

        var (_, json) = await GetAsync(app, "/health/live");

        await Assert.That(Check(json, "processo").GetProperty("status").GetString()).IsEqualTo("Healthy");
    }

    [Test]
    public async Task Library_liveness_can_be_disabled()
    {
        await using var app = await TestApp.StartAsync(settings: [("HealthChecks:RegisterLivenessCheck", "false")]);

        var (status, json) = await GetAsync(app, "/health/live");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(json.GetProperty("checks").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Degraded_dependency_keeps_readiness_at_200()
    {
        var handler = FakeHttpHandler.Status(HttpStatusCode.InternalServerError);
        await using var app = await TestApp.StartAsync(o => o.AddExternalServiceHealthCheck("PaymentGateway", Api, h =>
        {
            h.FailureStatus = HealthStatus.Degraded;
            h.Retries = 0;
            h.Tags.Add("pagamentos");
        }), handler);

        var (status, json) = await GetAsync(app, "/health/ready");
        var check = Check(json, "PaymentGateway");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(json.GetProperty("status").GetString()).IsEqualTo("Degraded");
        await Assert.That(Tags(check)).Contains("pagamentos");
    }

    [Test]
    public async Task Non_http_url_is_rejected()
    {
        var builder = new ServiceCollection().AddTecObservability(TestApp.Configuration());

        await Assert.That(() => builder.AddExternalServiceHealthCheck("arquivo", new Uri("file:///etc/passwd")))
            .Throws<ArgumentException>();
    }

    // ---------- Liveness: check de mesmo nome sem a tag live ----------

    [Test]
    public async Task Service_self_check_without_live_tag_prevents_startup()
    {
        var exception = await Assert.That(async () => await TestApp.StartAsync(
                services: s => s.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy())))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("'self' sem a tag 'live'");
        await Assert.That(exception.Message).Contains("LivenessCheckName");
    }

    [Test]
    public async Task Self_check_without_live_tag_is_accepted_with_library_liveness_disabled()
    {
        await using var app = await TestApp.StartAsync(
            services: s => s.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), [HealthChecks.HealthCheckTags.Ready]),
            settings: [("HealthChecks:RegisterLivenessCheck", "false")]);

        var (status, _) = await GetAsync(app, "/health/ready");

        await Assert.That(status).IsEqualTo(HttpStatusCode.OK);
    }

    // ---------- Prazo global x tempo máximo de cada verificação ----------

    [Test]
    public async Task Http_check_exceeding_global_timeout_prevents_startup()
    {
        // (Timeout 5s + 1s) × (Retries 4 + 1) = 30s, igual ao HealthChecks:Timeout padrão.
        var exception = await Assert.That(async () => await TestApp.StartAsync(
                o => o.AddExternalServiceHealthCheck("PaymentGateway", Api, h => h.Retries = 4)))
            .Throws<Microsoft.Extensions.Options.OptionsValidationException>();

        await Assert.That(exception!.Message).Contains("'PaymentGateway' pode levar até 30s ((Timeout 5s + 1s) × (Retries 4 + 1))");
        await Assert.That(exception.Message).Contains("HealthChecks:Timeout (30s)");
    }

    [Test]
    [Arguments(3, "00:00:30", true)]   // (5 + 1) × 4 = 24s
    [Arguments(5, "00:00:30", false)]  // (5 + 1) × 6 = 36s
    [Arguments(5, "00:00:37", true)]
    public async Task Http_check_is_validated_against_HealthChecks_Timeout(int retries, string timeout, bool accepted)
    {
        var services = new ServiceCollection();
        services.AddTecObservability(TestApp.Configuration(("HealthChecks:Timeout", timeout)))
            .AddSsoHealthCheck("Sso", new Uri("https://sso.exemplo.com/realms/tec"), h => h.Retries = retries);
        await using var provider = services.BuildServiceProvider();

        Func<object> read = () => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Configuration.ObservabilityOptions>>().Value;
        if (accepted)
            await Assert.That(read).ThrowsNothing();
        else
            await Assert.That(read).Throws<Microsoft.Extensions.Options.OptionsValidationException>();
    }

    [Test]
    public async Task Database_check_with_timeout_equal_to_global_timeout_is_rejected()
    {
        var exception = await Assert.That(async () => await TestApp.StartAsync(
                o => o.AddDatabaseHealthCheck("PrimaryDb", SqliteFactory.Instance, DatabaseOk, timeout: TimeSpan.FromSeconds(10)),
                settings: [("HealthChecks:Timeout", "00:00:10")]))
            .Throws<Microsoft.Extensions.Options.OptionsValidationException>();

        await Assert.That(exception!.Message).Contains("'PrimaryDb' pode levar até 10s (timeout 10s)");
    }

    [Test]
    public async Task Global_timeout_is_not_checked_when_health_checks_are_disabled()
    {
        var services = new ServiceCollection();
        services.AddTecObservability(TestApp.Configuration(("HealthChecks:Enabled", "false"), ("HealthChecks:Timeout", "00:00:02")))
            .AddDatabaseHealthCheck("PrimaryDb", SqliteFactory.Instance, DatabaseOk);
        await using var provider = services.BuildServiceProvider();

        await Assert.That(() => provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Configuration.ObservabilityOptions>>().Value)
            .ThrowsNothing();
    }

    // ---------- Banco: operação abandonada com o token do framework cancelado ----------

    [Test]
    public async Task Operation_abandoned_by_framework_cancellation_has_failure_observed()
    {
        var marker = $"falha-tardia-{Guid.NewGuid():N}";
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(x => x.Message == marker))
                Interlocked.Increment(ref unobserved);
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var connection = new IgnoringCancellationConnection(hangOnOpen: true, new InvalidOperationException(marker));
                await RunAbandonedAsync(connection, cancelFirst: i == 0);
                connection.Release(); // a operação abandonada falha agora, depois de o check já ter devolvido o controle
            }

            for (var i = 0; i < 5; i++)
            {
                await Task.Delay(100);
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        await Assert.That(unobserved).IsEqualTo(0);
    }

    /// <summary>
    /// Roda a verificação com o token do framework cancelado antes do timeout (<paramref name="cancelFirst"/>) ou junto com ele.
    /// Em método separado: nenhuma referência à operação abandonada sobra na pilha do teste antes da coleta.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task RunAbandonedAsync(IgnoringCancellationConnection connection, bool cancelFirst)
    {
        var timeout = TimeSpan.FromMilliseconds(cancelFirst ? 5_000 : 100);
        var check = new HealthChecks.DatabaseHealthCheck(() => connection, "SELECT 1", timeout,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HealthChecks.DatabaseHealthCheck>.Instance);
        using var framework = new CancellationTokenSource(cancelFirst ? TimeSpan.FromMilliseconds(50) : timeout);
        var context = new HealthCheckContext { Registration = new HealthCheckRegistration("PrimaryDb", check, null, null) };

        try
        {
            await check.CheckHealthAsync(context, framework.Token);
        }
        catch (OperationCanceledException)
        {
            // Esperado quando o cancelamento do framework chega primeiro.
        }
    }

    // ---------- Writer fora do AddTecObservability ----------

    [Test]
    public async Task Writer_without_registered_options_responds_without_details()
    {
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        using var body = new MemoryStream();
        context.Response.Body = body;
        var report = new HealthReport(new Dictionary<string, HealthReportEntry>
        {
            ["PrimaryDb"] = new(HealthStatus.Unhealthy, "Banco de dados inacessível.", TimeSpan.FromMilliseconds(10),
                new InvalidOperationException("host=db-interno;user=sa"), null),
        }, TimeSpan.FromMilliseconds(10));

        await HealthChecks.HealthCheckResponseWriter.WriteAsync(context, report);
        using var json = JsonDocument.Parse(body.ToArray());
        var properties = json.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        await Assert.That(context.Response.ContentType).IsEqualTo("application/json; charset=utf-8");
        await Assert.That(properties).IsEquivalentTo(["status", "timestamp"]);
        await Assert.That(json.RootElement.GetProperty("status").GetString()).IsEqualTo("Unhealthy");
        await Assert.That(System.Text.Encoding.UTF8.GetString(body.ToArray())).DoesNotContain("db-interno");
    }

    private sealed class SlowHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    /// <summary>Conexão de um driver que ignora o <see cref="CancellationToken"/> (abre e consulta só quando liberada).</summary>
    private sealed class IgnoringCancellationConnection(bool hangOnOpen, Exception? failureAfterRelease = null) : System.Data.Common.DbConnection
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private System.Data.ConnectionState _state = System.Data.ConnectionState.Closed;

        public int? CommandTimeout { get; private set; }

        public void Release() => _release.TrySetResult();

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "fake";
        public override string DataSource => "fake";
        public override string ServerVersion => "1.0";
        public override System.Data.ConnectionState State => _state;

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            if (hangOnOpen)
                await _release.Task; // de propósito, sem o token
            if (failureAfterRelease is not null)
                throw failureAfterRelease;
            _state = System.Data.ConnectionState.Open;
        }

        public override void Open() => _state = System.Data.ConnectionState.Open;
        public override void Close() => _state = System.Data.ConnectionState.Closed;
        public override void ChangeDatabase(string databaseName) { }
        protected override System.Data.Common.DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override System.Data.Common.DbCommand CreateDbCommand() => new Command(this);

        private sealed class Command(IgnoringCancellationConnection owner) : System.Data.Common.DbCommand
        {
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get => owner.CommandTimeout ?? 30; set => owner.CommandTimeout = value; }
            public override System.Data.CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override System.Data.UpdateRowSource UpdatedRowSource { get; set; }
            protected override System.Data.Common.DbConnection? DbConnection { get; set; }
            protected override System.Data.Common.DbParameterCollection DbParameterCollection => throw new NotSupportedException();
            protected override System.Data.Common.DbTransaction? DbTransaction { get; set; }
            public override void Cancel() { }
            public override int ExecuteNonQuery() => 0;
            public override object? ExecuteScalar() => 1;
            public override void Prepare() { }
            protected override System.Data.Common.DbParameter CreateDbParameter() => throw new NotSupportedException();
            protected override System.Data.Common.DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior) => throw new NotSupportedException();
        }
    }
}
