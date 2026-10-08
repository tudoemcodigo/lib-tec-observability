namespace TEC.Observability.Tests;

/// <summary>
/// Spans com a instrumentação montada só pelo provedor em teste, num processo filho (<see cref="IsolatedProcess"/>): redação da
/// query string e de cofres, rotas ignoradas, enrich do serviço e o sampler efetivo.
/// </summary>
public class IsolatedTelemetryTests
{
    /// <summary>Um processo filho por provider, compartilhado pelos testes (o cenário é o mesmo).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<Dictionary<string, string?>>>>> Runs = new();

    private static async Task<(string? Sampler, List<Dictionary<string, string?>> Spans)> RunAsync(string provider)
    {
        var results = await Runs.GetOrAdd(provider, p => new(() => IsolatedProcess.RunAsync(IsolatedScenarios.Spans, p))).Value;
        var sampler = results.Single(r => r["tipo"] == "sampler")["nome"];
        return (sampler, [.. results.Where(r => r["tipo"] == "span")]);
    }

    private static string? Tag(Dictionary<string, string?> span, string key) => span.GetValueOrDefault(key);

    private static List<Dictionary<string, string?>> Server(List<Dictionary<string, string?>> spans, string path) =>
        [.. spans.Where(s => Tag(s, "kind") == "Server" && Tag(s, "url.path") == path)];

    [Test]
    [Arguments("Azure")]
    [Arguments("Azure, LogFile")]
    [Arguments("LogFile, Azure")]
    [Arguments("LogFile")]
    public async Task Incoming_request_query_string_is_redacted_with_any_provider(string provider)
    {
        var (_, spans) = await RunAsync(provider);
        var orderSpan = Server(spans, "/pedidos").Single(s => Tag(s, "url.query")?.StartsWith("?token", StringComparison.Ordinal) == true);

        await Assert.That(Tag(orderSpan, "url.query")).IsEqualTo("?token=Redacted&pagina=Redacted");
        // O enrich configurado pelo serviço continua rodando.
        await Assert.That(Tag(orderSpan, "servico.enrich")).IsEqualTo("sim");
        await Assert.That(spans.SelectMany(s => s.Values).Any(v => v?.Contains("SEGREDO", StringComparison.Ordinal) == true)).IsFalse();
    }

    [Test]
    [Arguments("Azure")]
    [Arguments("Azure, LogFile")]
    [Arguments("LogFile")]
    public async Task Vault_call_creates_span_without_secret_name_and_version(string provider)
    {
        var (_, spans) = await RunAsync(provider);
        var values = spans.SelectMany(s => s.Values).ToList();

        await Assert.That(values.Any(v => v?.Contains("senha-do-banco", StringComparison.Ordinal) == true)).IsFalse();
        await Assert.That(values.Any(v => v?.Contains("0123abcd", StringComparison.Ordinal) == true)).IsFalse();

        // A distro do Azure Monitor assina as fontes Azure.*: o span do Azure SDK fica, com duração e status, sem o caminho.
        if (provider.Contains("Azure", StringComparison.Ordinal))
        {
            var vault = spans.Single(s => Tag(s, "fonte") == "Azure.Core.Http");
            await Assert.That(Tag(vault, "url.full")).IsEqualTo("https://meu-cofre.vault.azure.net/secrets/***");
            await Assert.That(Tag(vault, "http.response.status_code")).IsEqualTo("200");
        }
    }

    [Test]
    [Arguments("Azure")]
    [Arguments("Azure, LogFile")]
    public async Task IgnoredPaths_and_health_routes_do_not_create_spans_with_azure(string provider)
    {
        var (_, spans) = await RunAsync(provider);
        var paths = spans.Where(s => Tag(s, "kind") == "Server").Select(s => Tag(s, "url.path")).ToList();

        await Assert.That(paths).Contains("/pedidos");
        await Assert.That(paths.Any(p => p!.StartsWith("/interno", StringComparison.Ordinal))).IsFalse();
        await Assert.That(paths.Any(p => p!.StartsWith("/health", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("Azure", "ApplicationInsightsSampler", true)]
    [Arguments("Azure, LogFile", "ApplicationInsightsSampler", true)]
    [Arguments("LogFile", "ParentBasedSampler", false)]
    public async Task Effective_sampler_depends_on_provider(string provider, string sampler, bool notSampledParentIsExported)
    {
        var (effective, spans) = await RunAsync(provider);
        var exported = Server(spans, "/pedidos").Any(s => Tag(s, "url.query")?.StartsWith("?caso=", StringComparison.Ordinal) == true);

        // Com Azure, o sampler da distro decide pelo TraceId e ignora o sampled=00 do chamador; o ParentBased o respeita.
        await Assert.That(effective).IsEqualTo(sampler);
        await Assert.That(exported).IsEqualTo(notSampledParentIsExported);
    }

    [Test]
    [Arguments("Azure")]
    [Arguments("LogFile")]
    public async Task Outgoing_url_is_redacted_with_any_provider(string provider)
    {
        var (_, spans) = await RunAsync(provider);
        var outgoing = spans.Single(s => Tag(s, "kind") == "Client" && Tag(s, "url.full")?.Contains("127.0.0.1", StringComparison.Ordinal) == true);

        await Assert.That(Tag(outgoing, "url.full")).DoesNotContain("SEGREDO");
    }

    [Test]
    public async Task Baggage_filter_installed_before_sdk_keeps_process_propagation()
    {
        // Regressão: lido antes do construtor estático do Sdk, o padrão era o no-op, e o filtro desligava a propagação do processo
        var result = (await IsolatedProcess.RunAsync(IsolatedScenarios.PropagatorFirst, string.Empty)).Single();

        await Assert.That(result["instalado"]).IsEqualTo("HostFilteringPropagator");
        await Assert.That(result["interno"]).IsNotEqualTo("NoopTextMapPropagator");
        await Assert.That(result["traceparent"]).IsNotNull();
        await Assert.That(result["baggage"]).IsEqualTo("correlation.id=pedido-42");
    }
}
