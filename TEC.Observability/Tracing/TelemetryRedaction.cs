using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using OpenTelemetry;
using TEC.Observability.Internal;

namespace TEC.Observability.Tracing;

/// <summary>
/// Redação de URLs nos spans HTTP, aplicada pela biblioteca a qualquer provedor (OTLP, console, arquivo, Azure Monitor ou
/// exportador próprio), independentemente da configuração da instrumentação.
/// </summary>
/// <remarks>
/// <para>
/// A instrumentação do OpenTelemetry redige os valores da query string (<c>?token=Redacted</c>), mas a redação pode ser desligada
/// pelas chaves <c>OTEL_DOTNET_EXPERIMENTAL_ASPNETCORE_DISABLE_URL_QUERY_REDACTION</c> e
/// <c>OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION</c> da configuração. A distro do Azure Monitor
/// (<c>UseAzureMonitor</c>) grava <c>true</c> nas duas, na própria <c>IConfiguration</c> da aplicação, quando elas não estão
/// definidas: com <c>Provider: Azure</c> a query string das requisições de entrada ia sem redação para o Application Insights.
/// Os enriquecedores daqui rodam depois que a instrumentação grava as tags e devolvem o formato redigido, compostos com os
/// enriquecedores do serviço.
/// </para>
/// <para>
/// Chamadas a cofres de segredos trazem o nome e a versão do segredo no caminho (<c>/secrets/nome/versão</c>):
/// <see cref="SpanRedactionProcessor"/> reescreve o caminho desses spans mantendo duração e status. O cofre é reconhecido pelo
/// host: os de nuvem pelo nome, os próprios (HashiCorp Vault, Infisical...) pela opção <c>SecretStoreHosts</c>.
/// </para>
/// </remarks>
internal static class TelemetryRedaction
{
    /// <summary>Texto que substitui os valores da query string, o mesmo da instrumentação do OpenTelemetry.</summary>
    internal const string RedactedValue = "Redacted";

    /// <summary>Texto que substitui o caminho de um recurso de cofre (<c>/secrets/***</c>).</summary>
    internal const string RedactedPath = "***";

    internal const string UrlFull = "url.full";
    internal const string UrlPath = "url.path";
    internal const string UrlQuery = "url.query";
    internal const string HttpUrl = "http.url";
    internal const string HttpTarget = "http.target";
    internal const string ServerAddress = "server.address";

    /// <summary>Sufixos de host dos cofres de segredos em nuvem (Azure Key Vault, Managed HSM, Google Secret Manager).</summary>
    private static readonly string[] SecretStoreHostSuffixes =
    [
        ".vault.azure.net", ".vault.azure.cn", ".vault.usgovcloudapi.net", ".vault.microsoftazure.de", ".managedhsm.azure.net",
        "secretmanager.googleapis.com",
    ];

    /// <summary>
    /// Query string com cada valor trocado por <see cref="RedactedValue"/> (<c>?a=1&amp;b=2</c> vira <c>?a=Redacted&amp;b=Redacted</c>),
    /// no mesmo formato da instrumentação do OpenTelemetry. Um trecho sem <c>=</c> pode ser o próprio segredo
    /// (<c>?eyJhbGciOi...</c>) e também vira <see cref="RedactedValue"/> (<c>?a=1&amp;flag</c> vira <c>?a=Redacted&amp;Redacted</c>).
    /// </summary>
    internal static string RedactQuery(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var index = query.StartsWith('?') ? 1 : 0;
        if (index == query.Length)
            return query;

        var builder = new System.Text.StringBuilder(query.Length + 16);
        builder.Append(query, 0, index);
        while (index <= query.Length)
        {
            var end = query.IndexOf('&', index);
            if (end < 0)
                end = query.Length;

            var equals = query.IndexOf('=', index, end - index);
            if (equals >= 0)
                builder.Append(query, index, equals - index).Append('=').Append(RedactedValue);
            else if (end == index + 1 && query[index] == '*')
                builder.Append('*'); // Query já redigida pelo .NET 9+ (?*)
            else if (end > index)
                builder.Append(RedactedValue);

            if (end < query.Length)
                builder.Append('&');
            index = end + 1;
        }

        return builder.ToString();
    }

    /// <summary><c>true</c> quando a query string tem algo a redigir (qualquer coisa além do <c>?</c>).</summary>
    internal static bool HasQuery(string query) => query.Length > (query.StartsWith('?') ? 1 : 0);

    /// <summary>
    /// Enriquecedor dos spans de servidor (ASP.NET Core): regrava <c>url.query</c> redigida. Só age quando a instrumentação
    /// gravou a tag; com a redação ligada, o valor já é o mesmo.
    /// </summary>
    internal static void RedactServerQuery(Activity activity, HttpRequest request)
    {
        if (request.QueryString.Value is { Length: > 0 } query && activity.GetTagItem(UrlQuery) is not null)
            activity.SetTag(UrlQuery, RedactQuery(query));
    }

    /// <summary>
    /// Enriquecedor dos spans de saída do <c>HttpClient</c>: se a instrumentação gravou <c>url.full</c> sem redação (com a redação
    /// desligada pela configuração ou por outra biblioteca), o próprio valor gravado volta ao formato redigido, sem credenciais nem
    /// fragmento. Valor já redigido (<c>?*</c> do .NET, <c>?a=Redacted</c>) não muda.
    /// </summary>
    internal static void RedactClientUrl(Activity activity) => RedactUrlTag(activity, UrlFull);

    /// <summary>Regrava a tag de URL (<c>url.full</c>/<c>http.url</c>) no formato redigido, se ela tiver algo a redigir.</summary>
    internal static void RedactUrlTag(Activity activity, string tag)
    {
        if (activity.GetTagItem(tag) is string value && RedactUrlText(value) is var redacted && !ReferenceEquals(redacted, value))
            activity.SetTag(tag, redacted);
    }

    /// <summary>URL redigida (sem query, credenciais nem fragmento), ou a mesma instância quando não há nada a redigir.</summary>
    internal static string RedactUrlText(string value)
    {
        // Conferência barata antes do parse: só URLs com query, fragmento ou usuário podem ter algo a redigir.
        if (value.AsSpan().IndexOfAny('?', '#', '@') < 0)
            return value;

        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && Internal.SensitiveText.NeedsRedaction(uri)
            ? Internal.SensitiveText.RedactUrl(uri)
            : value;
    }

    /// <summary>Teto de padrões de host de cofre no processo (a lista é unida a cada recarga da configuração).</summary>
    internal const int MaxSecretStoreHosts = 256;

    private static readonly Lock SecretStoreLock = new();
    private static readonly List<string> SecretStorePatterns = [];

    /// <summary>Hosts de cofres próprios (<c>Observability:SecretStoreHosts</c>), trocados por cópia: a leitura não usa lock.</summary>
    private static volatile HostPatterns _secretStoreHosts = new([]);

    /// <summary>
    /// Acrescenta hosts de cofres próprios (<c>Observability:SecretStoreHosts</c>: nome exato ou <c>*.sufixo</c>). Vale para o
    /// processo inteiro (o filtro de saída do <c>HttpClient</c> e o processador de spans são estáticos) e só soma: com vários hosts
    /// no mesmo processo, ou a cada recarga, vale a união — esconder a mais é o lado seguro. Padrões inválidos (recusados na
    /// validação da subida) e <c>*</c> são ignorados aqui.
    /// </summary>
    internal static void RegisterSecretStoreHosts(IEnumerable<string?>? patterns)
    {
        if (patterns is null)
            return;

        lock (SecretStoreLock)
        {
            var changed = false;
            foreach (var raw in patterns)
            {
                var pattern = raw?.Trim();
                if (pattern is null or "*" || !Configuration.ObservabilityOptionsValidator.IsHostPattern(pattern)
                    || SecretStorePatterns.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (SecretStorePatterns.Count >= MaxSecretStoreHosts)
                    break;
                SecretStorePatterns.Add(pattern);
                changed = true;
            }

            if (changed)
                _secretStoreHosts = new HostPatterns(SecretStorePatterns);
        }
    }

    /// <summary>
    /// <c>true</c> para hosts de cofres de segredos: os de nuvem, reconhecidos pelo nome (Azure Key Vault e Managed HSM, AWS
    /// Secrets Manager, GCP Secret Manager), e os configurados em <c>Observability:SecretStoreHosts</c>.
    /// </summary>
    internal static bool IsSecretStoreHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
            return false;

        foreach (var suffix in SecretStoreHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (host.StartsWith("secretsmanager.", StringComparison.OrdinalIgnoreCase)
            && (host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".amazonaws.com.cn", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return _secretStoreHosts.Matches(host);
    }

    /// <summary>
    /// <c>true</c> quando a requisição vai a um cofre de segredos (<see cref="IsSecretStoreHost(string?)"/>). Só o host decide: o
    /// caminho sozinho (<c>/v1/secret</c>, <c>/api/v1/secrets</c>) não basta, porque APIs de terceiros usam os mesmos caminhos.
    /// </summary>
    internal static bool IsSecretStoreRequest(Uri? uri) => uri is { IsAbsoluteUri: true } && IsSecretStoreHost(uri.IdnHost);

    /// <summary><c>true</c> quando há hosts de cofre configurados (as URLs não podem ser descartadas pela conferência barata).</summary>
    internal static bool HasConfiguredSecretStoreHosts => !_secretStoreHosts.IsEmpty;

    /// <summary>
    /// Caminho de um recurso de cofre sem o nome e a versão: mantém só o prefixo ou tipo de recurso.
    /// <c>/secrets/nome/versão</c> vira <c>/secrets/***</c>; <c>/v1/projects/x/secrets/y/versions/z</c> vira <c>/v1/projects/x/secrets/***</c>.
    /// </summary>
    internal static string RedactSecretStorePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Google Secret Manager: /v1/projects/{project}/secrets/{secret}/... -> /v1/projects/{project}/secrets/***
        const string gcpSecrets = "/secrets/";
        var gcpIdx = path.IndexOf(gcpSecrets, StringComparison.OrdinalIgnoreCase);
        if (gcpIdx > 0 && path.StartsWith("/v1/projects/", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = path[..(gcpIdx + gcpSecrets.Length)];
            return $"{prefix}{RedactedPath}";
        }

        // HashiCorp Vault / OpenBao: /v1/secret/... -> /v1/secret/***
        if (path.StartsWith("/v1/secret/", StringComparison.OrdinalIgnoreCase))
            return $"/v1/secret/{RedactedPath}";

        // Infisical: /api/v1/secrets/... -> /api/v1/secrets/***
        if (path.StartsWith("/api/v1/secrets/", StringComparison.OrdinalIgnoreCase))
            return $"/api/v1/secrets/{RedactedPath}";
        if (path.StartsWith("/api/v2/secrets/", StringComparison.OrdinalIgnoreCase))
            return $"/api/v2/secrets/{RedactedPath}";

        // Azure Key Vault: /secrets/nome/versão -> /secrets/***
        var trimmed = path.AsSpan().TrimStart('/');
        var separator = trimmed.IndexOf('/');
        if (separator < 0 || trimmed[(separator + 1)..].TrimStart('/').IsEmpty)
            return path;

        return $"/{trimmed[..separator]}/{RedactedPath}";
    }
}


/// <summary>
/// Redação de spans antes de qualquer exportador: atributos de nome sensível (<see cref="Internal.SensitiveKeys"/>, ex.:
/// <c>http.request.header.authorization</c>, <c>db.connection_string</c>, <c>enduser.password</c>) viram <c>Redacted</c>;
/// <c>url.full</c>/<c>http.url</c> dos spans de saída perdem query string, credenciais e fragmento. Os valores de texto dos
/// demais atributos e a descrição do status (<see cref="Activity.StatusDescription"/>) passam pela redação de texto livre
/// (<see cref="Internal.SensitiveText"/>: URL com query, <c>Password=...</c>, <c>Bearer ...</c>).
/// <b>Eventos não são redigidos</b>: o <see cref="ActivityEvent"/> guarda uma cópia imutável dos atributos e o <see cref="Activity"/>
/// não permite trocar nem remover eventos, então <c>exception.message</c>/<c>exception.stacktrace</c> gravados por
/// <c>RecordException</c>/<c>AddException</c> seguem como vieram. Em vez de gravar a exceção no span, registre-a em log (a exceção
/// do log é redigida) ou use <c>SetStatus(Error, descrição)</c>, que é redigida.
/// Spans de chamadas a cofres de segredos do Azure (ex.: <c>Azure.Core.Http</c>, emitidos pelo Azure SDK com a fonte
/// <c>Azure.*</c>, que a distro do Azure Monitor registra): <c>url.full</c>/<c>http.url</c> perdem também o nome e a versão do
/// segredo (<c>https://kv.vault.azure.net/secrets/***</c>); <c>url.path</c>, <c>http.target</c> e o nome de exibição
/// seguem a mesma regra. Nos spans dos clientes do Key Vault (fontes <c>Azure.Security.KeyVault.*</c>, ex.:
/// <c>SecretClient.GetSecret</c>), os atributos <c>az.keyvault.*</c> (nome e versão do segredo, da chave ou do certificado)
/// viram <c>***</c>. Duração, status e os demais atributos ficam.
/// </summary>
/// <remarks>
/// Age no fim, antes dos exportadores registrados depois da biblioteca, e também no início nos spans de cofre (as tags do Azure
/// SDK já existem quando o span começa); a reescrita é idempotente. Sem nada a redigir, não aloca nos atributos de texto e
/// escalares: só confere o nome de cada atributo e varre os textos (objetos e coleções custam um <c>ToString</c>).
/// </remarks>
internal sealed class SpanRedactionProcessor : BaseProcessor<Activity>
{
    /// <summary>
    /// No início, só spans de cofre (as tags do Azure SDK já existem e trazem nome e versão do segredo): os demais são redigidos
    /// no <see cref="OnEnd"/>, antes de qualquer exportador, sem varrer os atributos duas vezes.
    /// </summary>
    public override void OnStart(Activity data)
    {
        if (IsSecretStoreSpan(data))
            Redact(data);
    }

    public override void OnEnd(Activity data) => Redact(data);

    /// <summary>Span de um cliente do Key Vault ou chamada HTTP de saída a um cofre de segredos (conferência barata, sem parse).</summary>
    private static bool IsSecretStoreSpan(Activity activity) =>
        activity.Source.Name.StartsWith(KeyVaultSourcePrefix, StringComparison.Ordinal)
        || (activity.Kind == ActivityKind.Client
            && (TelemetryRedaction.IsSecretStoreHost(activity.GetTagItem(TelemetryRedaction.ServerAddress) as string)
                || TryParse(activity.GetTagItem(TelemetryRedaction.UrlFull) as string, out var fullUri) && TelemetryRedaction.IsSecretStoreRequest(fullUri)
                || TryParse(activity.GetTagItem(TelemetryRedaction.HttpUrl) as string, out var legacyUri) && TelemetryRedaction.IsSecretStoreRequest(legacyUri)));

    /// <summary>Prefixo das fontes dos clientes do Key Vault no Azure SDK.</summary>
    private const string KeyVaultSourcePrefix = "Azure.Security.KeyVault.";

    /// <summary>Prefixo dos atributos do Key Vault nos spans do Azure SDK (<c>az.keyvault.secret.name</c>, <c>.version</c>...).</summary>
    private const string KeyVaultAttributePrefix = "az.keyvault.";

    internal static void Redact(Activity activity)
    {
        RedactAttributes(activity, activity.Source.Name.StartsWith(KeyVaultSourcePrefix, StringComparison.Ordinal));
        RedactStatus(activity);
        if (activity.Kind != ActivityKind.Client)
            return;

        var full = activity.GetTagItem(TelemetryRedaction.UrlFull) as string;
        var legacy = activity.GetTagItem(TelemetryRedaction.HttpUrl) as string;
        Uri? uri = null;
        if (!TryParse(full, out uri) && !TryParse(legacy, out uri))
        {
            if (!TelemetryRedaction.IsSecretStoreHost(activity.GetTagItem(TelemetryRedaction.ServerAddress) as string))
            {
                RedactOrdinaryUrls(activity, full, legacy);
                return;
            }
        }
        else if (!TelemetryRedaction.IsSecretStoreRequest(uri))
        {
            RedactOrdinaryUrls(activity, full, legacy);
            return;
        }

        if (uri is null)
        {
            // Host de cofre (server.address) com URL que não pôde ser lida: sai a URL inteira.
            if (full is not null)
                activity.SetTag(TelemetryRedaction.UrlFull, null);
            if (legacy is not null)
                activity.SetTag(TelemetryRedaction.HttpUrl, null);
        }
        else
        {
            var path = TelemetryRedaction.RedactSecretStorePath(uri.AbsolutePath);
            var redacted = $"{uri.Scheme}{Uri.SchemeDelimiter}{uri.Authority}{path}";
            if (full is not null)
                activity.SetTag(TelemetryRedaction.UrlFull, redacted);
            if (legacy is not null)
                activity.SetTag(TelemetryRedaction.HttpUrl, redacted);
            if (uri.AbsolutePath != path && activity.DisplayName.Contains(uri.AbsolutePath, StringComparison.Ordinal))
                activity.DisplayName = activity.DisplayName.Replace(uri.AbsolutePath, path, StringComparison.Ordinal);
        }

        if (activity.GetTagItem(TelemetryRedaction.UrlPath) is string urlPath)
            activity.SetTag(TelemetryRedaction.UrlPath, TelemetryRedaction.RedactSecretStorePath(urlPath));
        if (activity.GetTagItem(TelemetryRedaction.HttpTarget) is string target)
            activity.SetTag(TelemetryRedaction.HttpTarget, TelemetryRedaction.RedactSecretStorePath(StripQuery(target)));
        if (activity.GetTagItem(TelemetryRedaction.UrlQuery) is not null)
            activity.SetTag(TelemetryRedaction.UrlQuery, null);
    }

    /// <summary>URL de saída que não é de cofre: só query string, credenciais e fragmento saem (instrumentações de terceiros).</summary>
    private static void RedactOrdinaryUrls(Activity activity, string? full, string? legacy)
    {
        if (full is not null)
            TelemetryRedaction.RedactUrlTag(activity, TelemetryRedaction.UrlFull);
        if (legacy is not null)
            TelemetryRedaction.RedactUrlTag(activity, TelemetryRedaction.HttpUrl);
    }

    /// <summary>
    /// Atributos de nome sensível viram <c>Redacted</c>; nos spans do Key Vault, os <c>az.keyvault.*</c> viram <c>***</c>. Os
    /// demais valores de texto, arrays de texto e objetos passam pela redação de texto livre (<see cref="Internal.SensitiveValue"/>).
    /// </summary>
    private static void RedactAttributes(Activity activity, bool keyVault)
    {
        List<(string Key, object Value)>? changes = null;
        foreach (ref readonly var tag in activity.EnumerateTagObjects())
        {
            if (tag.Value is null or bool)
                continue;

            if (keyVault && tag.Key.StartsWith(KeyVaultAttributePrefix, StringComparison.Ordinal))
            {
                if (!Equals(tag.Value, TelemetryRedaction.RedactedPath))
                    (changes ??= []).Add((tag.Key, TelemetryRedaction.RedactedPath));
            }
            else if (Internal.SensitiveKeys.IsSensitive(tag.Key))
            {
                if (!Equals(tag.Value, TelemetryRedaction.RedactedValue))
                    (changes ??= []).Add((tag.Key, TelemetryRedaction.RedactedValue));
            }
            else if (tag.Value is string text)
            {
                if (Internal.SensitiveText.Redact(text) is var redacted && !ReferenceEquals(redacted, text))
                    (changes ??= []).Add((tag.Key, redacted));
            }
            else if (Internal.SensitiveValue.TryRedact(tag.Value, out var redactedValue))
            {
                // string[] (tipo nativo de atributo), Uri e objetos gravados pelo ToString.
                (changes ??= []).Add((tag.Key, redactedValue));
            }
        }

        // Fora do laço: alterar as tags durante a enumeração invalidaria o enumerador.
        foreach (var (key, value) in changes ?? [])
            activity.SetTag(key, value);
    }

    /// <summary>Descrição do status (ex.: mensagem de exceção gravada com <c>SetStatus(Error, ex.Message)</c>) redigida.</summary>
    private static void RedactStatus(Activity activity)
    {
        if (activity.StatusDescription is { } description && Internal.SensitiveText.Redact(description) is var redacted
            && !ReferenceEquals(redacted, description))
        {
            activity.SetStatus(activity.Status, redacted);
        }
    }

    private static bool TryParse(string? value, out Uri? uri)
    {
        uri = null;
        if (value is null)
            return false;

        // Só URLs candidatas a cofre chegam ao parse: a conferência barata evita custo nos demais spans de saída. Com hosts de
        // cofre configurados (SecretStoreHosts), qualquer URL é candidata.
        if (!TelemetryRedaction.HasConfiguredSecretStoreHosts
            && value.IndexOf(".vault.", StringComparison.OrdinalIgnoreCase) < 0
            && value.IndexOf(".managedhsm.", StringComparison.OrdinalIgnoreCase) < 0
            && value.IndexOf("secretmanager", StringComparison.OrdinalIgnoreCase) < 0
            && value.IndexOf("secretsmanager", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Absolute, out uri);
    }

    private static string StripQuery(string target)
    {
        var query = target.IndexOf('?', StringComparison.Ordinal);
        return query < 0 ? target : target[..query];
    }
}
