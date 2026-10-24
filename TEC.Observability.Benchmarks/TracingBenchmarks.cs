using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Microsoft.AspNetCore.Http;
using TEC.Observability.Configuration;
using TEC.Observability.Tracing;

namespace TEC.Observability.Benchmarks;

/// <summary>Correlation id, política de hosts do Baggage e filtro de rotas: rodam em toda requisição.</summary>
[MemoryDiagnoser]
public class CorrelationBenchmarks
{
    private static readonly string Short = "pedido-42";
    private static readonly string Longest = new('a', 128);
    private static readonly string InvalidFirst = "<script>" + new string('a', 120);
    private static readonly string InvalidLast = new string('a', 127) + "\n";

    private readonly BaggageHostPolicy _policy = new(
        ["pedidos.interno", "estoque.interno", "pagamentos.interno", "*.svc.cluster.local", "*.empresa.internal", "127.0.0.1"]);

    private readonly RequestPathFilter _filter = RequestPathFilter.Create(new ObservabilityOptions());
    private readonly PathString _business = new("/api/pedidos/42/itens");
    private readonly PathString _health = new("/health/ready");

    [Benchmark]
    public bool IsSafe_Short() => CorrelationIdMiddleware.IsSafe(Short);

    [Benchmark]
    public bool IsSafe_128Chars() => CorrelationIdMiddleware.IsSafe(Longest);

    [Benchmark]
    public bool IsSafe_InvalidFirstChar() => CorrelationIdMiddleware.IsSafe(InvalidFirst);

    [Benchmark]
    public bool IsSafe_InvalidLastChar() => CorrelationIdMiddleware.IsSafe(InvalidLast);

    [Benchmark]
    public bool BaggagePolicy_ExactHost() => _policy.Allows("pagamentos.interno");

    [Benchmark]
    public bool BaggagePolicy_Suffix() => _policy.Allows("pedidos.producao.svc.cluster.local");

    [Benchmark]
    public bool BaggagePolicy_Denied() => _policy.Allows("api.terceiro.com.br");

    [Benchmark]
    public bool PathFilter_BusinessRoute() => _filter.ShouldTrace(_business);

    [Benchmark]
    public bool PathFilter_HealthRoute() => _filter.ShouldTrace(_health);
}

/// <summary>Redação de query string e de spans de cofre (o processador roda no início e no fim de todo span).</summary>
[MemoryDiagnoser]
public class RedactionBenchmarks
{
    private static readonly ActivitySource Source = new("TEC.Observability.Benchmarks");
    private static readonly ActivityListener Listener = CreateListener();

    private readonly string _noValues = "?ativo&ordenar";
    private readonly string _threeParams = "?token=abc123&pagina=2&tamanho=50";
    private readonly string _fiftyParams = "?" + string.Join('&', Enumerable.Range(0, 50).Select(i => $"campo{i}=valor-{i}-{Guid.Empty:N}"));

    private Activity _ordinaryClient = null!;
    private Activity _vaultClient = null!;
    private Activity _internal = null!;

    [GlobalSetup]
    public void Setup()
    {
        _ = Listener;
        _ordinaryClient = Source.StartActivity("GET", ActivityKind.Client)!;
        _ordinaryClient.SetTag("url.full", "https://api.parceiro.com.br/v1/pedidos/42?pagina=2");
        _ordinaryClient.SetTag("server.address", "api.parceiro.com.br");
        _ordinaryClient.Stop();

        _internal = Source.StartActivity("ProcessarPedido", ActivityKind.Internal)!;
        _internal.SetTag("pedido.id", 42);
        _internal.Stop();

        _vaultClient = Source.StartActivity("GET", ActivityKind.Client)!;
        _vaultClient.SetTag("url.full", "https://cofre.vault.azure.net/secrets/conexao-banco/0123456789abcdef?api-version=7.5");
        _vaultClient.SetTag("url.path", "/secrets/conexao-banco/0123456789abcdef");
        _vaultClient.SetTag("url.query", "api-version=7.5");
        _vaultClient.SetTag("server.address", "cofre.vault.azure.net");
        _vaultClient.Stop();
    }

    [Benchmark]
    public string RedactQuery_NoValues() => TelemetryRedaction.RedactQuery(_noValues);

    [Benchmark]
    public string RedactQuery_3Params() => TelemetryRedaction.RedactQuery(_threeParams);

    [Benchmark]
    public string RedactQuery_50Params() => TelemetryRedaction.RedactQuery(_fiftyParams);

    [Benchmark]
    public string RedactSecretStorePath() => TelemetryRedaction.RedactSecretStorePath("/secrets/conexao-banco/0123456789abcdef");

    /// <summary>Custo do processador em um span de saída comum (o caso de quase todos os spans de cliente).</summary>
    [Benchmark]
    public void SecretStore_OrdinaryClientSpan() => SpanRedactionProcessor.Redact(_ordinaryClient);

    /// <summary>Custo do processador em um span interno (sai na primeira conferência).</summary>
    [Benchmark]
    public void SecretStore_InternalSpan() => SpanRedactionProcessor.Redact(_internal);

    /// <summary>
    /// Span de chamada ao Key Vault. Depois da primeira invocação o span já está redigido: é o custo do <c>OnEnd</c> (o
    /// <c>OnStart</c> já redigiu), com o parse da URL do cofre.
    /// </summary>
    [Benchmark]
    public void SecretStore_VaultClientSpan() => SpanRedactionProcessor.Redact(_vaultClient);

    private static ActivityListener CreateListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "TEC.Observability.Benchmarks",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

/// <summary>Redação de dados sensíveis em textos livres e nomes de atributo: roda em toda mensagem e atributo de log exportado.</summary>
[MemoryDiagnoser]
public class SensitiveTextBenchmarks
{
    private readonly string _clean = "Pedido 42 criado para o cliente 1234 às 10:30 com 3 itens, total=150.00";
    private readonly string _connectionString = "Falha ao abrir Server=db.interno;Database=pedidos;User Id=app;Password=s3gr3d0;Encrypt=true";
    private readonly string _urlWithToken = "Chamando https://api.parceiro.com/v1/pedidos?api_key=abc123&pagina=2 para sincronizar";

    [Benchmark(Baseline = true)]
    public string Redact_CleanMessage() => TEC.Observability.Internal.SensitiveText.Redact(_clean);

    [Benchmark]
    public string Redact_ConnectionString() => TEC.Observability.Internal.SensitiveText.Redact(_connectionString);

    [Benchmark]
    public string Redact_UrlWithToken() => TEC.Observability.Internal.SensitiveText.Redact(_urlWithToken);

    [Benchmark]
    public bool SensitiveKey_Ordinary() => TEC.Observability.Internal.SensitiveKeys.IsSensitive("http.response.status_code");

    [Benchmark]
    public bool SensitiveKey_Sensitive() => TEC.Observability.Internal.SensitiveKeys.IsSensitive("http.request.header.authorization");
}
