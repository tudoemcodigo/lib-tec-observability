using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Logs;
using TEC.Observability.Configuration;
using TEC.Observability.DependencyInjection;
using TEC.Observability.HealthChecks;
using TEC.Observability.Internal;
using TEC.Observability.Tracing;

namespace TEC.Observability.Tests.Security;

/// <summary>
/// Endurecimento da redação e da correlação: nomes sensíveis em português e variantes <c>*Key</c>, nomes adicionais
/// configuráveis, pares <c>chave=valor</c> em tempo linear, escopos de log só por lista e redigidos, cofres reconhecidos pelo host,
/// correlation id pelo Baggage do <see cref="Activity"/>, Baggage fora de redirecionamentos para hosts não permitidos e liveness
/// sem inicializador estático frágil.
/// </summary>
public class RedactionHardeningTests
{
    private static readonly string Secret = "SEGREDO" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    /// <summary>Sufixo único por execução: as listas de nomes e hosts adicionais são do processo e só somam.</summary>
    private static string Unique() => Guid.NewGuid().ToString("N")[..10];

    // ---------- Nomes sensíveis: português e *Key ----------

    [Test]
    [Arguments("Senha", true)]
    [Arguments("NovaSenha", true)]
    [Arguments("senha_atual", true)]
    [Arguments("SenhaAtual", true)]
    [Arguments("Senhas", true)]
    [Arguments("Segredo", true)]
    [Arguments("SegredoCliente", true)]
    [Arguments("credencial", true)]
    [Arguments("Credenciais", true)]
    [Arguments("CredencialBanco", true)]
    [Arguments("palavra-passe", true)]
    [Arguments("Chave", true)]
    [Arguments("chaves", true)]
    [Arguments("ChaveDeAcesso", true)]
    [Arguments("chave_privada", true)]
    [Arguments("ChaveApi", true)]
    [Arguments("ChavePix", true)]
    [Arguments("TokenDeAcesso", true)]
    [Arguments("token_acesso", true)]
    [Arguments("CódigoDeSegurança", true)]
    [Arguments("codigo_seguranca", true)]
    [Arguments("NúmeroDoCartão", true)]
    [Arguments("SecretKey", true)]
    [Arguments("SigningKey", true)]
    [Arguments("signing_key", true)]
    [Arguments("EncryptionKey", true)]
    [Arguments("MasterKey", true)]
    [Arguments("HmacKey", true)]
    [Arguments("SecretKeys", true)]
    [Arguments("aws.secret_access_key", true)]
    [Arguments("PartitionKey", false)]
    [Arguments("CacheKey", false)]
    [Arguments("IdempotencyKey", false)]
    [Arguments("RoutingKey", false)]
    [Arguments("PublicKey", false)]
    [Arguments("PrimaryKey", false)]
    [Arguments("ForeignKey", false)]
    [Arguments("SortKey", false)]
    [Arguments("Key", false)]
    [Arguments("Monkey", false)]
    [Arguments("Hotkey", false)]
    [Arguments("ChaveNfe", false)]
    [Arguments("ChavePrimaria", false)]
    [Arguments("ChaveCache", false)]
    [Arguments("Senhorio", false)]
    [Arguments("Credenciamento", false)]
    [Arguments("Assinatura", false)]
    [Arguments("TokenCount", false)]
    [Arguments("PasswordPolicy", false)]
    public async Task Portuguese_and_key_variant_names_are_detected_without_common_false_positives(string key, bool sensitive) =>
        await Assert.That(SensitiveKeys.IsSensitive(key)).IsEqualTo(sensitive);

    [Test]
    [Arguments("Senha=abc", "Senha=Redacted")]
    [Arguments("login ana senha: abc def", "login ana senha: Redacted def")]
    [Arguments("{\"senha\":\"abc\",\"usuario\":\"ana\"}", "{\"senha\":\"Redacted\",\"usuario\":\"ana\"}")]
    [Arguments("CódigoDeSegurança=123;Validade=12/30", "CódigoDeSegurança=Redacted;Validade=12/30")]
    [Arguments("ChaveDeAcesso=xyz&pagina=2", "ChaveDeAcesso=Redacted&pagina=2")]
    [Arguments("PartitionKey=pedidos-2026", "PartitionKey=pedidos-2026")]
    public async Task Portuguese_pairs_inside_text_are_redacted(string text, string expected) =>
        await Assert.That(SensitiveText.Redact(text)).IsEqualTo(expected);

    [Test]
    public async Task Log_with_Senha_placeholder_is_redacted_in_attribute_and_message()
    {
        var record = await LogAsync(logger => logger.LogInformation("Usuário {Usuario} entrou com {Senha}", "ana", Secret));

        await Assert.That(Attribute(record, "Senha")).IsEqualTo("Redacted");
        await Assert.That(Attribute(record, "Usuario")).IsEqualTo("ana");
        await Assert.That(record.FormattedMessage).IsEqualTo("Usuário ana entrou com Redacted");
    }

    // ---------- AdditionalSensitiveKeys ----------

    [Test]
    public async Task Additional_sensitive_keys_are_matched_like_builtin_names()
    {
        var name = "Matricula" + Unique();
        Register(("AdditionalSensitiveKeys:0", name));

        await Assert.That(SensitiveKeys.IsSensitive(name)).IsTrue();
        await Assert.That(SensitiveKeys.IsSensitive("aluno_" + name.ToUpperInvariant())).IsTrue().Because("pelo fim do nome, sem diferenciar maiúsculas");
        await Assert.That(SensitiveKeys.IsSensitive(name + "s")).IsTrue().Because("plural com s");
        await Assert.That(SensitiveKeys.IsSensitive(name + "Hash")).IsFalse().Because("só o fim do nome conta");
        await Assert.That(SensitiveText.Redact($"{name}=2026001 turma=3")).IsEqualTo($"{name}=Redacted turma=3");
    }

    [Test]
    public async Task Additional_sensitive_keys_from_late_configuration_are_applied()
    {
        // Provedor de configuração adicionado depois do AddTecObservability (ex.: cofre): vale quando as opções são montadas.
        var name = "Beneficio" + Unique();
        await using var app = await TestApp.StartAsync(
            lateSettings: new Dictionary<string, string?> { ["Observability:AdditionalSensitiveKeys:0"] = name });

        await Assert.That(SensitiveKeys.IsSensitive("numero" + name)).IsTrue();
    }

    [Test]
    public async Task Additional_sensitive_key_redacts_exported_log_attribute()
    {
        var name = "Prontuario" + Unique();
        var record = await LogAsync(logger =>
        {
#pragma warning disable CA2254 // o nome do atributo é gerado pelo teste
            logger.LogInformation("Paciente {" + name + "} atendido", Secret);
#pragma warning restore CA2254
        }, ("AdditionalSensitiveKeys:0", name));

        await Assert.That(Attribute(record, name)).IsEqualTo("Redacted");
        await Assert.That(record.FormattedMessage).IsEqualTo("Paciente Redacted atendido");
    }

    [Test]
    [Arguments("id")]
    [Arguments("a=b")]
    [Arguments("nome/com/barra")]
    [Arguments("   ")]
    public async Task Invalid_additional_sensitive_key_is_rejected_at_startup(string name) =>
        await Assert.That(() => Register(("AdditionalSensitiveKeys:0", name))).Throws<OptionsValidationException>();

    [Test]
    public async Task Too_many_additional_sensitive_keys_are_rejected_at_startup()
    {
        var settings = Enumerable.Range(0, ObservabilityOptionsValidator.MaxListItems + 1)
            .Select(i => ($"AdditionalSensitiveKeys:{i}", (string?)("nome" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .ToArray();

        await Assert.That(() => Register(settings)).Throws<OptionsValidationException>();
    }

    // ---------- Pares chave=valor em tempo linear ----------

    [Test]
    [Arguments("token=a&")]
    [Arguments("token=Redacted&")]
    [Arguments("Authorization=;")]
    [Arguments("password=x ")]
    [Arguments("Cookie=a ")]
    public async Task Key_value_redaction_scans_each_char_a_bounded_number_of_times(string pair)
    {
        // Sem quebra de linha nem ';' (ou com ';' a cada par), cada par procurava o fim do texto inteiro: custo quadrático.
        // A contagem de caracteres percorridos pelas buscas à frente prova a linearidade sem depender de tempo.
        var text = string.Concat(Enumerable.Repeat(pair, 50_000));

        _ = SensitiveText.RedactKeyValues(text, out var scanned);

        await Assert.That(scanned).IsLessThanOrEqualTo(2 * text.Length + 2);
    }

    [Test]
    public async Task Repeated_sensitive_pairs_are_all_redacted()
    {
        var text = string.Concat(Enumerable.Repeat("token=a&", 50_000));

        var redacted = SensitiveText.RedactKeyValues(text, out _);

        await Assert.That(redacted).IsEqualTo(string.Concat(Enumerable.Repeat("token=Redacted&", 50_000)));
    }

    [Test]
    [Arguments("token=a\ntoken=b;x=1\r\nsenha=c", "token=Redacted\ntoken=Redacted;x=1\r\nsenha=Redacted")]
    [Arguments("Server=x;Password=p;User=u\nPassword=q", "Server=x;Password=Redacted;User=u\nPassword=Redacted")]
    [Arguments("Cookie: a=1; b=2\nCookie: c=3", "Cookie: Redacted\nCookie: Redacted")]
    [Arguments("Authorization: Bearer abc;x=1\nAuthorization: Basic def", "Authorization: Bearer Redacted;x=1\nAuthorization: Basic Redacted")]
    public async Task Cached_lookahead_respects_line_and_semicolon_boundaries(string text, string expected) =>
        await Assert.That(SensitiveText.Redact(text)).IsEqualTo(expected);

    // ---------- Escopos de log ----------

    [Test]
    public async Task Scopes_are_not_exported_by_default()
    {
        var record = await LogAsync(logger =>
        {
            using (logger.BeginScope(new Dictionary<string, object> { ["Tenant"] = "t1", ["Url"] = $"https://h.com/x?t={Secret}" }))
                logger.LogInformation("dentro do escopo");
        });

        await Assert.That(Attribute(record, "Tenant")).IsNull();
        await Assert.That(Attribute(record, "Url")).IsNull();
        await Assert.That(Scopes(record)).IsEmpty().Because("o IncludeScopes do OpenTelemetry não é ligado pela biblioteca");
    }

    [Test]
    public async Task Only_allowed_scope_keys_are_exported_and_values_are_redacted()
    {
        var record = await LogAsync(logger =>
        {
            using (logger.BeginScope(new Dictionary<string, object>
            {
                ["Tenant"] = "t1", ["Token"] = Secret, ["Url"] = $"https://h.com/x?t={Secret}", ["Cpf"] = "12345678900", ["Nulo"] = null!,
            }))
            {
                logger.LogInformation("dentro do escopo");
            }
        }, ("IncludeScopes", "true"), ("AllowedScopeKeys:0", "tenant"), ("AllowedScopeKeys:1", "Token"), ("AllowedScopeKeys:2", "Url"),
            ("AllowedScopeKeys:3", "Nulo"));

        await Assert.That(Attribute(record, "Tenant")).IsEqualTo("t1").Because("a lista não diferencia maiúsculas");
        await Assert.That(Attribute(record, "Token")).IsEqualTo("Redacted");
        await Assert.That(Attribute(record, "Url")).IsEqualTo("https://h.com/x?t=Redacted");
        await Assert.That(Attribute(record, "Cpf")).IsNull().Because("chave fora da lista");
        await Assert.That(record.Attributes!.Any(a => a.Key == "Nulo")).IsFalse().Because("valor nulo não vira atributo");
        await Assert.That(Scopes(record)).IsEmpty();
        await Assert.That(AllText(record)).DoesNotContain(Secret);
    }

    [Test]
    public async Task Wildcard_exports_keyed_scopes_but_never_text_scopes_or_original_format()
    {
        var record = await LogAsync(logger =>
        {
            using (logger.BeginScope($"escopo de texto com {Secret}"))
            using (logger.BeginScope("Pedido {PedidoId}", 42))
                logger.LogInformation("dentro do escopo");
        }, ("IncludeScopes", "true"), ("AllowedScopeKeys:0", "*"));

        await Assert.That(Attribute(record, "PedidoId")).IsEqualTo(42);
        await Assert.That(record.Attributes!.Count(a => a.Key == LogRedactionProcessor.OriginalFormatAttribute)).IsEqualTo(1)
            .Because("só o modelo do próprio log; o do escopo não");
        await Assert.That(AllText(record)).DoesNotContain(Secret);
    }

    [Test]
    public async Task Log_attribute_wins_over_scope_item_with_same_key()
    {
        var record = await LogAsync(logger =>
        {
            using (logger.BeginScope(new Dictionary<string, object> { ["Tenant"] = "do-escopo" }))
                logger.LogInformation("Tenant {Tenant}", "do-log");
        }, ("IncludeScopes", "true"), ("AllowedScopeKeys:0", "Tenant"));

        await Assert.That(record.Attributes!.Where(a => a.Key == "Tenant").Select(a => a.Value)).IsEquivalentTo(new object?[] { "do-log" });
    }

    [Test]
    public async Task Scope_items_are_bounded()
    {
        var items = Enumerable.Range(0, 500).ToDictionary(i => "k" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), object (i) => i);
        var record = await LogAsync(logger =>
        {
            using (logger.BeginScope(items))
                logger.LogInformation("muitos itens");
        }, ("IncludeScopes", "true"), ("AllowedScopeKeys:0", "*"));

        // O teto vale para todos os escopos: com "*", os do ambiente (ex.: SpanId/TraceId da Activity do executor de testes)
        // também ocupam vagas, por isso os itens "k" ficam no máximo no teto, nunca nos 500
        int fromScope = record.Attributes!.Count(a => a.Key.StartsWith('k'));
        await Assert.That(fromScope).IsLessThanOrEqualTo(LogRedactionProcessor.MaxScopeAttributes);
        await Assert.That(fromScope).IsGreaterThan(LogRedactionProcessor.MaxScopeAttributes / 2);
    }

    [Test]
    public async Task Correlation_id_scope_is_always_allowed_when_scopes_are_enabled()
    {
        var logs = new List<LogRecord>();
        var category = "Escopo" + Unique();
        await using var app = await TestApp.StartAsync(
            services: s => s.ConfigureOpenTelemetryLoggerProvider(l => l.AddInMemoryExporter(logs)),
            endpoints: a => a.MapGet("/log-escopo", (ILoggerFactory factory) =>
            {
                factory.CreateLogger(category).LogInformation("dentro da requisição");
                return "ok";
            }),
            settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317"), ("IncludeScopes", "true")]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/log-escopo");
        request.Headers.Add(CorrelationId.HeaderName, "pedido-42");
        (await app.GetTestClient().SendAsync(request)).Dispose();
        app.Services.GetRequiredService<LoggerProvider>().ForceFlush(5_000);

        var record = logs.ToArray().OfType<LogRecord>().Single(l => l.CategoryName == category);
        await Assert.That(Attribute(record, CorrelationId.AttributeName)).IsEqualTo("pedido-42");
        await Assert.That(Attribute(record, "RequestPath")).IsNull().Because("escopo do ASP.NET Core fora da lista");
    }

    [Test]
    [Arguments("")]
    [Arguments("chave\ncom-quebra")]
    public async Task Invalid_allowed_scope_key_is_rejected_at_startup(string key) =>
        await Assert.That(() => Register(("AllowedScopeKeys:0", key))).Throws<OptionsValidationException>();

    // ---------- Cofres pelo host ----------

    [Test]
    [Arguments("/v1/secret/data/pedidos")]
    [Arguments("/v1/sys/health")]
    [Arguments("/api/v1/secrets/raw/x")]
    [Arguments("/api/v2/secrets/x")]
    public async Task Third_party_api_with_vault_like_path_is_not_suppressed(string path)
    {
        var url = $"https://api-{Unique()}.terceiro.com{path}?pagina=2";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        await Assert.That(ServiceCollectionExtensions.ShouldTraceOutgoing(request)).IsTrue();

        using var source = new ActivitySource("teste-endurecimento-" + Unique());
        using var listener = Listen(source);
        using var activity = source.StartActivity("GET", ActivityKind.Client)!;
        activity.SetTag("url.full", url);
        SpanRedactionProcessor.Redact(activity);
        await Assert.That(activity.GetTagItem("url.full") as string).IsEqualTo(url.Replace("pagina=2", "pagina=Redacted", StringComparison.Ordinal))
            .Because("só a query string sai; o caminho de uma API comum fica");
    }

    [Test]
    public async Task Configured_vault_host_is_suppressed_and_redacted_on_any_path()
    {
        var host = $"cofre-{Unique()}.interno";
        Register(("SecretStoreHosts:0", host));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}:8200/v1/kv/data/{Secret}");
        await Assert.That(ServiceCollectionExtensions.ShouldTraceOutgoing(request)).IsFalse();

        using var source = new ActivitySource("teste-endurecimento-" + Unique());
        using var listener = Listen(source);
        using var activity = source.StartActivity("GET", ActivityKind.Client)!;
        activity.SetTag("url.full", $"https://{host}:8200/v1/kv/data/{Secret}?version=2");
        SpanRedactionProcessor.Redact(activity);
        await Assert.That(activity.GetTagItem("url.full")).IsEqualTo($"https://{host}:8200/v1/***");
    }

    [Test]
    public async Task Vault_host_suffix_pattern_does_not_include_the_suffix_itself()
    {
        var suffix = $"cofres-{Unique()}.local";
        Register(("SecretStoreHosts:0", "*." + suffix));

        using var subdomain = new HttpRequestMessage(HttpMethod.Get, $"https://vault.{suffix}/v1/secret/x");
        using var bare = new HttpRequestMessage(HttpMethod.Get, $"https://{suffix}/v1/secret/x");

        await Assert.That(ServiceCollectionExtensions.ShouldTraceOutgoing(subdomain)).IsFalse();
        await Assert.That(ServiceCollectionExtensions.ShouldTraceOutgoing(bare)).IsTrue();
    }

    [Test]
    [Arguments("*")]
    [Arguments("host com espaço")]
    [Arguments("*.a*b")]
    [Arguments("")]
    public async Task Invalid_vault_host_is_rejected_at_startup(string pattern) =>
        await Assert.That(() => Register(("SecretStoreHosts:0", pattern))).Throws<OptionsValidationException>();

    [Test]
    public async Task Cloud_vaults_are_still_recognized_without_configuration()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://kv-x.vault.azure.net/secrets/nome/versao");

        await Assert.That(ServiceCollectionExtensions.ShouldTraceOutgoing(request)).IsFalse();
    }

    // ---------- Correlation id pelo Baggage do Activity (Provider None) ----------

    [Test]
    [NotInParallel] // Listeners de ActivitySource são do processo: a instrumentação de outro host preencheria o Baggage do OpenTelemetry
    public async Task Provider_None_uses_correlation_id_from_activity_baggage()
    {
        await using var app = await TestApp.StartAsync(
            endpoints: a => a.MapGet("/correlacao", () => CorrelationId.Current ?? "(nulo)"),
            settings: [("Provider", "None")]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/correlacao");
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        request.Headers.Add("baggage", "correlation.id=pedido-77");
        using var response = await app.GetTestClient().SendAsync(request);

        await Assert.That(response.Headers.GetValues(CorrelationId.HeaderName).Single()).IsEqualTo("pedido-77");
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("pedido-77");
    }

    [Test]
    [NotInParallel]
    [Arguments("correlation.id=a%20b")]
    [Arguments("correlation.id=%0Aforjado")]
    public async Task Unsafe_correlation_id_in_activity_baggage_falls_back_to_trace_id(string baggage)
    {
        await using var app = await TestApp.StartAsync(endpoints: a => a.MapGet("/correlacao", () => "ok"), settings: [("Provider", "None")]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/correlacao");
        request.Headers.Add("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        request.Headers.Add("baggage", baggage);
        using var response = await app.GetTestClient().SendAsync(request);

        await Assert.That(response.Headers.GetValues(CorrelationId.HeaderName).Single()).IsEqualTo("0af7651916cd43dd8448eb211c80319c");
    }

    // ---------- Baggage e redirecionamento ----------

    [Test]
    public async Task Dotnet_propagator_removes_existing_baggage_for_host_outside_policy()
    {
        var propagator = new HostFilteringDistributedContextPropagator(DistributedContextPropagator.CreateDefaultPropagator());
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8080/destino");
        request.Headers.TryAddWithoutValidation("baggage", "correlation.id=pedido-42");
        request.Headers.TryAddWithoutValidation("Correlation-Context", "tenant=x");
        using var activity = new Activity("saida").Start();

        BaggageHostPolicy.Current = new BaggageHostPolicy(["localhost"]);
        try
        {
            propagator.Inject(activity, request, (carrier, name, value) => ((HttpRequestMessage)carrier!).Headers.TryAddWithoutValidation(name, value));
        }
        finally
        {
            BaggageHostPolicy.Current = null;
        }

        await Assert.That(request.Headers.Contains("baggage")).IsFalse();
        await Assert.That(request.Headers.Contains("Correlation-Context")).IsFalse();
        await Assert.That(request.Headers.Contains("traceparent")).IsTrue();
    }

    [Test]
    public async Task OpenTelemetry_propagator_removes_existing_baggage_for_host_outside_policy()
    {
        var propagator = new HostFilteringPropagator(new CompositeTextMapPropagator([new TraceContextPropagator(), new BaggagePropagator()]));
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8080/destino");
        request.Headers.TryAddWithoutValidation("baggage", "correlation.id=pedido-42");
        var context = new PropagationContext(
            new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded),
            Baggage.Create(new Dictionary<string, string> { ["tenant"] = "x" }));

        BaggageHostPolicy.Current = new BaggageHostPolicy(["localhost"]);
        try
        {
            propagator.Inject(context, request, (r, name, value) => r.Headers.TryAddWithoutValidation(name, value));
        }
        finally
        {
            BaggageHostPolicy.Current = null;
        }

        await Assert.That(request.Headers.Contains("baggage")).IsFalse();
        await Assert.That(request.Headers.Contains("traceparent")).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Baggage_is_not_resent_on_redirect_to_host_outside_policy(bool manualHeader)
    {
        // localhost (permitido) responde 302 para 127.0.0.1 (fora da lista): o HttpClient reaproveita a mesma requisição.
        await using var target = new RawHttpServer();
        await using var redirector = new RawHttpServer(_ =>
            $"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{target.Port}/destino\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await using var app = await TestApp.StartAsync(
            endpoints: a => a.MapGet("/redirecionar", async (IHttpClientFactory http) =>
            {
                using var client = http.CreateClient();
                using var outgoing = new HttpRequestMessage(HttpMethod.Get, $"http://localhost:{redirector.Port}/origem");
                if (manualHeader)
                    outgoing.Headers.TryAddWithoutValidation("baggage", "manual=1");
                (await client.SendAsync(outgoing)).Dispose();
                return "ok";
            }),
            settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317"), ("BaggageAllowedHosts:0", "localhost")]);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/redirecionar");
        request.Headers.Add(CorrelationId.HeaderName, "pedido-42");
        (await app.GetTestClient().SendAsync(request)).Dispose();

        var first = redirector.Requests.Single();
        var second = target.Requests.Single();
        await Assert.That(first).Contains("baggage:", StringComparison.OrdinalIgnoreCase).Because("o primeiro destino é permitido");
        await Assert.That(second).Contains("traceparent:", StringComparison.OrdinalIgnoreCase);
        await Assert.That(second).DoesNotContain("baggage:", StringComparison.OrdinalIgnoreCase);
        await Assert.That(second).DoesNotContain("Correlation-Context:", StringComparison.OrdinalIgnoreCase);
        await Assert.That(second).DoesNotContain("pedido-42", StringComparison.Ordinal);
        await Assert.That(second).DoesNotContain("manual=1", StringComparison.Ordinal);
    }

    // ---------- Liveness ----------

    [Test]
    public async Task Liveness_without_details_does_not_read_process_start()
    {
        var start = new Lazy<DateTimeOffset?>(() => throw new InvalidOperationException("não deveria ser lido"));
        var check = new LivenessHealthCheck(Options.Create(new ObservabilityOptions { HealthChecks = { ExposeDetails = false } }), TimeProvider.System, start);

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        await Assert.That(result.Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That(result.Data).IsEmpty();
        await Assert.That(start.IsValueCreated).IsFalse();
    }

    [Test]
    public async Task Liveness_stays_healthy_when_process_start_is_unavailable()
    {
        var start = new Lazy<DateTimeOffset?>(() => LivenessHealthCheck.TryGetStart(() => throw new PlatformNotSupportedException()));
        var check = new LivenessHealthCheck(Options.Create(new ObservabilityOptions { HealthChecks = { ExposeDetails = true } }), TimeProvider.System, start);

        var first = await check.CheckHealthAsync(new HealthCheckContext());
        var second = await check.CheckHealthAsync(new HealthCheckContext());

        await Assert.That(first.Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That(first.Data.ContainsKey("startedAt")).IsFalse();
        await Assert.That(second.Status).IsEqualTo(HealthStatus.Healthy).Because("a falha não vira exceção nas sondagens seguintes");
    }

    [Test]
    public async Task Liveness_with_details_reports_start_and_uptime()
    {
        var startedAt = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var time = new FixedTime(startedAt.AddSeconds(90));
        var check = new LivenessHealthCheck(Options.Create(new ObservabilityOptions { HealthChecks = { ExposeDetails = true } }), time,
            new Lazy<DateTimeOffset?>(() => startedAt));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        await Assert.That(result.Data["startedAt"]).IsEqualTo(startedAt);
        await Assert.That(result.Data["uptimeSeconds"]).IsEqualTo(90d);
    }

    [Test]
    public async Task Process_start_reader_only_swallows_platform_failures()
    {
        await Assert.That(LivenessHealthCheck.TryGetStart(() => throw new InvalidOperationException())).IsNull();
        await Assert.That(LivenessHealthCheck.TryGetStart(() => throw new System.ComponentModel.Win32Exception())).IsNull();
        await Assert.That(() => LivenessHealthCheck.TryGetStart(() => throw new ArgumentException("bug"))).Throws<ArgumentException>();
    }

    // ---------- Apoio ----------

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static ActivityListener Listen(ActivitySource source)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void Register(params (string Key, string? Value)[] settings)
    {
        var services = new ServiceCollection();
        services.AddTecObservability(TestApp.Configuration(settings));
        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<ObservabilityOptions>>().Value;
    }

    private static async Task<LogRecord> LogAsync(Action<ILogger> log, params (string Key, string? Value)[] settings)
    {
        var logs = new List<LogRecord>();
        await using var app = await TestApp.StartAsync(services: s => s.ConfigureOpenTelemetryLoggerProvider(l => l.AddInMemoryExporter(logs)),
            settings: [("Provider", "Otlp"), ("Otlp:Endpoint", "http://localhost:4317"), .. settings]);
        var category = "Endurecimento" + Unique();
        log(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(category));
        app.Services.GetRequiredService<LoggerProvider>().ForceFlush(5_000);
        return logs.ToArray().Single(l => l.CategoryName == category);
    }

    private static object? Attribute(LogRecord record, string key) =>
        record.Attributes?.FirstOrDefault(a => a.Key == key).Value;

    /// <summary>Chaves dos escopos que o próprio OpenTelemetry anexou ao registro (vazio com o IncludeScopes do SDK desligado).</summary>
    private static List<string> Scopes(LogRecord record)
    {
        var keys = new List<string>();
        record.ForEachScope((scope, list) =>
        {
            foreach (var item in scope)
                list.Add(item.Key);
        }, keys);
        return keys;
    }

    private static string AllText(LogRecord record) =>
        string.Join('\n', new[] { record.FormattedMessage, record.Body }
            .Concat(record.Attributes?.Select(a => Convert.ToString(a.Value, System.Globalization.CultureInfo.InvariantCulture)) ?? []));
}
