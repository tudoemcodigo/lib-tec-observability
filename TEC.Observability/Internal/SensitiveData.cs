using System.Text;
using TEC.Observability.Tracing;

namespace TEC.Observability.Internal;

/// <summary>
/// Nomes de atributo (de log ou de span) que indicam valor sensível: senha, segredo, token, chave de API, connection string,
/// cabeçalho de autorização, cookie, credencial. A comparação ignora maiúsculas e os separadores <c>_</c>, <c>-</c>, <c>.</c>,
/// <c>:</c> e espaço, e olha o <b>fim</b> do nome: <c>DbPassword</c>, <c>access_token</c>, <c>http.request.header.authorization</c>
/// e <c>db.connection_string</c> casam; <c>TokenCount</c> e <c>PasswordPolicy</c> não.
/// </summary>
/// <remarks>Sem alocação: roda para cada atributo de cada log e span exportado.</remarks>
internal static class SensitiveKeys
{
    /// <summary>Terminações (já normalizadas: minúsculas, sem separadores) que tornam o nome sensível.</summary>
    private static readonly string[] Suffixes =
    [
        "password", "passwd", "passphrase", "secret", "token", "apikey", "accesskey", "accountkey", "privatekey", "connectionstring",
        "authorization", "credential", "credentials", "cookie", "signature",
    ];

    /// <summary>Nomes curtos sensíveis só quando são o nome inteiro (como sufixo casariam com nomes comuns).</summary>
    private static readonly string[] ExactNames = ["pwd", "sig"];

    /// <summary><c>true</c> quando o nome indica valor sensível.</summary>
    public static bool IsSensitive(ReadOnlySpan<char> key)
    {
        if (key.IsEmpty)
            return false;

        foreach (var suffix in Suffixes)
        {
            if (EndsWithNormalized(key, suffix, out _))
                return true;
        }

        foreach (var name in ExactNames)
        {
            if (EndsWithNormalized(key, name, out var consumed) && IsOnlySeparators(key[..^consumed]))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Compara o fim de <paramref name="key"/> com <paramref name="normalized"/> pulando separadores e ignorando maiúsculas (ASCII).
    /// <paramref name="consumed"/> é quantos caracteres do fim de <paramref name="key"/> foram usados.
    /// </summary>
    private static bool EndsWithNormalized(ReadOnlySpan<char> key, string normalized, out int consumed)
    {
        var k = key.Length - 1;
        for (var n = normalized.Length - 1; n >= 0; n--)
        {
            while (k >= 0 && IsSeparator(key[k]))
                k--;
            if (k < 0 || char.ToLowerInvariant(key[k]) != normalized[n])
            {
                consumed = 0;
                return false;
            }

            k--;
        }

        consumed = key.Length - 1 - k;
        return true;
    }

    private static bool IsOnlySeparators(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!IsSeparator(c))
                return false;
        }

        return true;
    }

    internal static bool IsSeparator(char c) => c is '_' or '-' or '.' or ':' or ' ';
}

/// <summary>
/// Redação de dados sensíveis dentro de textos livres (mensagens de log, valores de atributos de texto, exceções gravadas em
/// arquivo): URLs http/https com query string ou usuário e senha, pares <c>chave=valor</c>/<c>chave: valor</c> de chaves
/// sensíveis (connection strings, JSON, cabeçalhos) e credenciais <c>Bearer</c>/<c>Basic</c>.
/// </summary>
/// <remarks>
/// Varredura linear (custo proporcional ao tamanho do texto), sem expressão regular (sem risco de backtracking) e sem alocação quando não há nada a redigir: o caminho
/// comum é uma busca vetorizada por <c>://</c>, <c>=</c>, <c>:</c> e pelas palavras <c>Bearer</c>/<c>Basic</c>.
/// </remarks>
internal static class SensitiveText
{
    /// <summary>Texto que substitui o valor sensível (o mesmo da redação de query string).</summary>
    public const string RedactedValue = TelemetryRedaction.RedactedValue;

    /// <summary>Texto com os dados sensíveis redigidos; a mesma instância quando não há nada a redigir.</summary>
    public static string Redact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = RedactUrls(text);
        result = RedactKeyValues(result);
        result = RedactAuthorizationSchemes(result);
        return result;
    }

    /// <summary>URL absoluta http/https sem credenciais, sem fragmento e com cada valor da query trocado (<c>?a=Redacted</c>).</summary>
    public static string RedactUrl(Uri uri) =>
        $"{uri.Scheme}{Uri.SchemeDelimiter}{uri.Authority}{uri.AbsolutePath}{TelemetryRedaction.RedactQuery(uri.Query)}";

    /// <summary><c>true</c> quando a URL tem algo a redigir: query string, fragmento ou usuário e senha.</summary>
    public static bool NeedsRedaction(Uri uri) =>
        uri is { IsAbsoluteUri: true } && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && (NeedsQueryRedaction(uri.Query) || uri.Fragment.Length > 1 || !string.IsNullOrEmpty(uri.UserInfo));

    /// <summary>Query string ainda não redigida (já redigida: vazia, <c>?*</c> ou só valores <c>Redacted</c>).</summary>
    private static bool NeedsQueryRedaction(string query) =>
        TelemetryRedaction.HasQuery(query) && !string.Equals(TelemetryRedaction.RedactQuery(query), query, StringComparison.Ordinal);

    /// <summary>Cada URL http/https do texto com algo a redigir vira o formato redigido.</summary>
    internal static string RedactUrls(string text)
    {
        var index = text.IndexOf("://", StringComparison.Ordinal);
        if (index < 0)
            return text;

        StringBuilder? builder = null;
        var copied = 0;
        while (index >= 0)
        {
            var start = SchemeStart(text, index);
            var end = index + 3;
            if (start >= 0)
            {
                while (end < text.Length && !IsUrlTerminator(text[end]))
                    end++;
                while (end > index + 3 && text[end - 1] is '.' or ',' or ';' or ')' or ']' or '}' or '!')
                    end--;

                if (Uri.TryCreate(text.Substring(start, end - start), UriKind.Absolute, out var uri) && NeedsRedaction(uri))
                {
                    builder ??= new StringBuilder(text.Length);
                    builder.Append(text, copied, start - copied).Append(RedactUrl(uri));
                    copied = end;
                }
            }

            index = end < text.Length ? text.IndexOf("://", end, StringComparison.Ordinal) : -1;
        }

        return builder is null ? text : builder.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>Início de <c>http</c>/<c>https</c> imediatamente antes de <c>://</c>, ou -1.</summary>
    private static int SchemeStart(string text, int delimiter)
    {
        foreach (var scheme in (ReadOnlySpan<string>)["https", "http"])
        {
            var start = delimiter - scheme.Length;
            if (start >= 0 && text.AsSpan(start, scheme.Length).Equals(scheme, StringComparison.OrdinalIgnoreCase)
                && (start == 0 || !char.IsAsciiLetterOrDigit(text[start - 1])))
            {
                return start;
            }
        }

        return -1;
    }

    private static bool IsUrlTerminator(char c) => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\'' or '<' or '>' or '`' or '|';

    /// <summary>
    /// Valores de chaves sensíveis em pares <c>chave=valor</c> ou <c>chave: valor</c> (connection strings, JSON, cabeçalhos,
    /// argumentos): <c>Server=x;Password=abc;</c> vira <c>Server=x;Password=Redacted;</c>.
    /// </summary>
    internal static string RedactKeyValues(string text)
    {
        var index = text.AsSpan().IndexOfAny('=', ':');
        if (index < 0)
            return text;

        StringBuilder? builder = null;
        var copied = 0;
        while (index >= 0 && index < text.Length)
        {
            var next = index + 1;
            if (text[index] == ':' && next < text.Length && text[next] == '/')
            {
                // "://" de URL (já tratada) ou "C:/": não é separador de par.
            }
            else if (KeyBefore(text, index) is { IsEmpty: false } key && SensitiveKeys.IsSensitive(key)
                && ValueAfter(text, next) is var (valueStart, valueEnd) && valueEnd > valueStart
                && !text.AsSpan(valueStart, valueEnd - valueStart).Equals(RedactedValue, StringComparison.Ordinal))
            {
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, copied, valueStart - copied).Append(RedactedValue);
                copied = valueEnd;
                next = valueEnd;
            }

            var relative = next < text.Length ? text.AsSpan(next).IndexOfAny('=', ':') : -1;
            index = relative < 0 ? -1 : next + relative;
        }

        return builder is null ? text : builder.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>Palavra (letras, dígitos e separadores de nome) antes do separador, pulando espaços e uma aspa de fechamento.</summary>
    private static ReadOnlySpan<char> KeyBefore(string text, int separator)
    {
        var end = separator;
        while (end > 0 && text[end - 1] == ' ')
            end--;
        if (end > 0 && text[end - 1] is '"' or '\'')
            end--;

        var start = end;
        while (start > 0 && (char.IsAsciiLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '-' or '.'))
            start--;

        return text.AsSpan(start, end - start);
    }

    /// <summary>
    /// Trecho do valor depois do separador: entre aspas (sem as aspas) ou até <c>;</c>, <c>,</c>, <c>&amp;</c>, espaço, aspa ou
    /// fecha-chave. O esquema <c>Bearer</c>/<c>Basic</c> depois de <c>Authorization:</c> é mantido e só a credencial sai.
    /// </summary>
    private static (int Start, int End) ValueAfter(string text, int start)
    {
        while (start < text.Length && text[start] == ' ')
            start++;
        if (start >= text.Length)
            return (start, start);

        if (text[start] is '"' or '\'')
        {
            var quote = text[start];
            var close = text.IndexOf(quote, start + 1);
            return close < 0 ? (start + 1, text.Length) : (start + 1, close);
        }

        if (SchemeLength(text.AsSpan(start)) is > 0 and var schemeLength)
            start += schemeLength;

        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not (';' or ',' or '&' or '"' or '\'' or '}' or ']' or ')'))
            end++;
        return (start, end);
    }

    /// <summary>Credenciais depois de <c>Bearer </c> ou <c>Basic </c>, em qualquer lugar do texto.</summary>
    internal static string RedactAuthorizationSchemes(string text)
    {
        if (text.IndexOf("Bearer ", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("Basic ", StringComparison.OrdinalIgnoreCase) < 0)
            return text;

        StringBuilder? builder = null;
        var copied = 0;
        var i = 0;
        while (i < text.Length)
        {
            var length = SchemeLength(text.AsSpan(i));
            if (length == 0 || (i > 0 && char.IsAsciiLetterOrDigit(text[i - 1])))
            {
                i++;
                continue;
            }

            var start = i + length;
            var end = start;
            while (end < text.Length && IsCredentialChar(text[end]))
                end++;

            if (end > start && !text.AsSpan(start, end - start).Equals(RedactedValue, StringComparison.Ordinal))
            {
                builder ??= new StringBuilder(text.Length);
                builder.Append(text, copied, start - copied).Append(RedactedValue);
                copied = end;
            }

            i = Math.Max(end, i + 1);
        }

        return builder is null ? text : builder.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>Tamanho de <c>Bearer </c>/<c>Basic </c> (com os espaços seguintes) no início do trecho, ou zero.</summary>
    private static int SchemeLength(ReadOnlySpan<char> text)
    {
        foreach (var scheme in (ReadOnlySpan<string>)["Bearer", "Basic"])
        {
            if (text.Length > scheme.Length && text.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) && text[scheme.Length] == ' ')
            {
                var length = scheme.Length;
                while (length < text.Length && text[length] == ' ')
                    length++;
                return length;
            }
        }

        return 0;
    }

    /// <summary>Caracteres de token (RFC 6750 <c>b64token</c>) e de Base64.</summary>
    private static bool IsCredentialChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '=';
}
