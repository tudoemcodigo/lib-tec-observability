using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using OpenTelemetry;

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
/// definidas: com <c>Provider: Azure</c> a query string das requisições de entrada (e, no .NET 8, a das chamadas de saída) ia
/// sem redação para o Application Insights. Os enriquecedores daqui rodam depois que a instrumentação grava as tags e
/// devolvem o formato redigido, compostos com os enriquecedores do serviço.
/// </para>
/// <para>
/// Chamadas a cofres de segredos do Azure (Key Vault, Managed HSM) trazem o nome e a versão do segredo no caminho
/// (<c>/secrets/nome/versão</c>): <see cref="SpanRedactionProcessor"/> reescreve o caminho desses spans mantendo duração e status.
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

    /// <summary>Sufixos de host dos cofres do Azure (Key Vault nas nuvens pública e soberanas, e Managed HSM).</summary>
    private static readonly string[] SecretStoreHostSuffixes =
    [
        ".vault.azure.net", ".vault.azure.cn", ".vault.usgovcloudapi.net", ".vault.microsoftazure.de", ".managedhsm.azure.net",
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
    /// Enriquecedor dos spans de saída do <c>HttpClient</c>: no .NET 8 a instrumentação grava <c>url.full</c> sem redação quando
    /// ela está desligada (com a URL original ou, se a URL tinha usuário e senha, só sem eles); aqui o próprio valor gravado volta
    /// ao formato redigido, sem credenciais nem fragmento. Valor já redigido (<c>?*</c> do .NET 9 ou superior, <c>?a=Redacted</c>)
    /// não muda.
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

    /// <summary><c>true</c> para hosts de cofres de segredos do Azure (Key Vault / Managed HSM).</summary>
    internal static bool IsSecretStoreHost(Uri? uri) => uri is { IsAbsoluteUri: true } && IsSecretStoreHost(uri.IdnHost);

    internal static bool IsSecretStoreHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
            return false;

        foreach (var suffix in SecretStoreHostSuffixes)
        {
            if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Caminho de um recurso de cofre sem o nome e a versão: mantém só o primeiro segmento (o tipo de recurso). <c>/secrets/nome/versão</c>
    /// vira <c>/secrets/***</c>; <c>/secrets</c> (listagem) e <c>/</c> ficam como estão.
    /// </summary>
    internal static string RedactSecretStorePath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
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
/// Age no início (as tags do Azure SDK já existem quando o span começa) e no fim, antes dos exportadores registrados depois da
/// biblioteca; a reescrita é idempotente. Sem nada a redigir, não aloca: só confere o nome de cada atributo.
/// </remarks>
internal sealed class SpanRedactionProcessor : BaseProcessor<Activity>
{
    public override void OnStart(Activity data) => Redact(data);

    public override void OnEnd(Activity data) => Redact(data);

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
        else if (!TelemetryRedaction.IsSecretStoreHost(uri))
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

    /// <summary>URL de saída que não é de cofre: só query string, credenciais e fragmento saem (instrumentações de terceiros, .NET 8).</summary>
    private static void RedactOrdinaryUrls(Activity activity, string? full, string? legacy)
    {
        if (full is not null)
            TelemetryRedaction.RedactUrlTag(activity, TelemetryRedaction.UrlFull);
        if (legacy is not null)
            TelemetryRedaction.RedactUrlTag(activity, TelemetryRedaction.HttpUrl);
    }

    /// <summary>Atributos de nome sensível viram <c>Redacted</c>; nos spans do Key Vault, os <c>az.keyvault.*</c> viram <c>***</c>.</summary>
    private static void RedactAttributes(Activity activity, bool keyVault)
    {
        List<(string Key, string Value)>? changes = null;
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
            else if (tag.Value is string text && Internal.SensitiveText.Redact(text) is var redacted && !ReferenceEquals(redacted, text))
            {
                (changes ??= []).Add((tag.Key, redacted));
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
        // Só URLs de cofre chegam ao parse: a conferência barata evita custo nos demais spans de saída.
        if (value is null || (value.IndexOf(".vault.", StringComparison.OrdinalIgnoreCase) < 0
            && value.IndexOf(".managedhsm.", StringComparison.OrdinalIgnoreCase) < 0))
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
