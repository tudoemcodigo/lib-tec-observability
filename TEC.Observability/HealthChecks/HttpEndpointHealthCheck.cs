using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace TEC.Observability.HealthChecks;

/// <summary>Opções das verificações HTTP (SSO e serviços externos).</summary>
public sealed class HttpHealthCheckOptions
{
    /// <summary>Tempo máximo de cada tentativa. Padrão: 5 segundos.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Novas tentativas após falha de rede, timeout ou HTTP 5xx, antes de reportar a falha. Padrão: 1. O tempo máximo da
    /// verificação, <c>(Timeout + 1s) × (Retries + 1)</c>, precisa ser menor que <c>HealthChecks:Timeout</c> (conferido na subida).
    /// </summary>
    public int Retries { get; set; } = 1;

    /// <summary>
    /// Status reportado quando a dependência falha. Use <see cref="HealthStatus.Degraded"/> para dependências sem as quais o
    /// serviço ainda atende (o readiness continua respondendo 200).
    /// </summary>
    public HealthStatus FailureStatus { get; set; } = HealthStatus.Unhealthy;

    /// <summary>Tags adicionais (a tag <c>ready</c> e a do tipo de dependência já são aplicadas).</summary>
    public IList<string> Tags { get; } = [];

    internal void Validate()
    {
        if (Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Timeout), Timeout, "O timeout deve ser maior que zero.");
        if (Retries is < 0 or > 5)
            throw new ArgumentOutOfRangeException(nameof(Retries), Retries, "Retries deve estar entre 0 e 5.");
        if (Tags.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Tags não aceita itens vazios.", nameof(Tags));
    }
}

/// <summary>
/// Verificação HTTP genérica: GET na URL, sucesso com resposta 2xx. Falhas transitórias (rede, timeout, 5xx) são repetidas
/// conforme <see cref="HttpHealthCheckOptions.Retries"/>. Quando <paramref name="requireOpenIdDiscovery"/> é verdadeiro, o corpo
/// também precisa ser um documento de descoberta OpenID Connect (JSON com <c>issuer</c>).
/// </summary>
internal sealed class HttpEndpointHealthCheck(
    IHttpClientFactory httpClientFactory,
    Uri uri,
    HttpHealthCheckOptions options,
    bool requireOpenIdDiscovery,
    ILogger<HttpEndpointHealthCheck> logger) : IHealthCheck
{
    /// <summary>Nome do <see cref="HttpClient"/> usado pelas verificações.</summary>
    internal const string HttpClientName = "TEC.Observability.HealthChecks";

    /// <summary>Marca a requisição de saída como sondagem, para não gerar trace a cada verificação.</summary>
    internal static readonly HttpRequestOptionsKey<bool> ProbeKey = new("TEC.Observability.HealthCheckProbe");

    /// <summary>Tamanho máximo aceito do documento de descoberta OpenID (os reais têm poucos KB).</summary>
    internal const int MaxDiscoveryDocumentBytes = 256 * 1024;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Handler das sondagens: não segue redirecionamentos (a URL configurada é a que é chamada, nunca um destino indicado pela
    /// resposta), não guarda cookies e não envia cabeçalhos de trace/baggage a terceiros.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ActivityHeadersPropagator = null,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(10),
    };

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        string failure = "Dependência inacessível.";
        Exception? exception = null;
        int? statusCode = null;

        for (var attempt = 0; attempt <= options.Retries; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Options.Set(ProbeKey, true);

                var client = httpClientFactory.CreateClient(HttpClientName);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

                statusCode = (int)response.StatusCode;
                exception = null;

                if (response.IsSuccessStatusCode)
                {
                    if (!requireOpenIdDiscovery || await IsOpenIdDiscoveryAsync(response, timeout.Token).ConfigureAwait(false))
                        return HealthCheckResult.Healthy("Dependência acessível.", Data(statusCode));

                    failure = "A resposta não é um documento de descoberta OpenID Connect válido.";
                    break;
                }

                failure = $"Resposta HTTP {statusCode}.";
                if (statusCode < 500)
                    break; // 4xx não melhora repetindo
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                // A mensagem da exceção é diferente da descrição: o writer esconde descrições iguais à mensagem da exceção
                // (regra para checks de terceiros), e a descrição do timeout não traz nada sensível.
                failure = string.Create(CultureInfo.InvariantCulture, $"Sem resposta em {options.Timeout.TotalSeconds:0.#}s.");
                exception = new TimeoutException("Tempo limite da sondagem HTTP excedido.", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                failure = "Dependência inacessível.";
                exception = ex;
            }
        }

        HealthCheckLog.HttpDependencyFailed(logger, exception, context.Registration.Name, statusCode);
        return new HealthCheckResult(context.Registration.FailureStatus, failure, exception, Data(statusCode));
    }

    private static Dictionary<string, object>? Data(int? statusCode) =>
        statusCode is { } code ? new Dictionary<string, object> { ["statusCode"] = code } : null;

    private static async Task<bool> IsOpenIdDiscoveryAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            // O corpo vem de fora: lê só até o limite, para uma resposta enorme não virar consumo de memória.
            if (response.Content.Headers.ContentLength > MaxDiscoveryDocumentBytes)
                return false;

            var buffer = ArrayPool<byte>.Shared.Rent(MaxDiscoveryDocumentBytes + 1);
            try
            {
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (stream.ConfigureAwait(false))
                {
                    var length = await stream.ReadAtLeastAsync(buffer.AsMemory(0, MaxDiscoveryDocumentBytes + 1),
                        MaxDiscoveryDocumentBytes + 1, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                    if (length > MaxDiscoveryDocumentBytes)
                        return false;

                    using var document = JsonDocument.Parse(buffer.AsMemory(0, length));
                    return document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("issuer", out var issuer)
                        && issuer.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(issuer.GetString());
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal static partial class HealthCheckLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Health check '{Name}' falhou.")]
    public static partial void DependencyFailed(ILogger logger, Exception exception, string name);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Health check '{Name}' falhou (HTTP {StatusCode}).")]
    public static partial void HttpDependencyFailed(ILogger logger, Exception? exception, string name, int? statusCode);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Health checks da tag '{Tag}' não terminaram em {TimeoutSeconds}s (HealthChecks:Timeout): respondendo Unhealthy.")]
    public static partial void ExecutionTimedOut(ILogger logger, string tag, double timeoutSeconds);
}
