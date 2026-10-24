namespace TEC.Observability.Internal;

/// <summary>Motivo de uma URL não servir como endereço de coletor ou de sondagem.</summary>
internal enum HttpUrlProblem
{
    /// <summary>URL absoluta http ou https, sem usuário e senha.</summary>
    None = 0,

    /// <summary>Relativa, ou de outro esquema (<c>file</c>, <c>ftp</c>...).</summary>
    NotHttp,

    /// <summary>Traz usuário e senha (<c>https://usuario:senha@host</c>), que iriam parar em logs e traces.</summary>
    HasUserInfo,
}

/// <summary>Regra única das URLs configuradas (endpoint OTLP e health checks HTTP): absoluta, http ou https, sem credenciais.</summary>
internal static class HttpUrl
{
    public static HttpUrlProblem Check(Uri uri)
    {
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return HttpUrlProblem.NotHttp;

        return string.IsNullOrEmpty(uri.UserInfo) ? HttpUrlProblem.None : HttpUrlProblem.HasUserInfo;
    }
}
