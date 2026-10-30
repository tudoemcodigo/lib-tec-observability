using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using TEC.Observability.Tracing;

namespace TEC.Observability.Internal;

/// <summary>
/// Nomes de atributo (de log ou de span) que indicam valor sensível: senha, segredo, token, chave de API, connection string,
/// cabeçalho de autorização, cookie, credencial, id de sessão, JWT, chave de assinatura de API, em inglês e em português. A
/// comparação ignora maiúsculas, acentos (<c>á</c>, <c>ç</c>, <c>õ</c>...) e os separadores <c>_</c>, <c>-</c>, <c>.</c>,
/// <c>:</c> e espaço, aceita o plural com <c>s</c> e olha o <b>fim</b> do nome: <c>DbPassword</c>, <c>access_token</c>,
/// <c>Secrets</c>, <c>.AspNetCore.Cookies</c>, <c>http.request.header.authorization</c>, <c>db.connection_string</c>,
/// <c>NovaSenha</c> e <c>ChaveDeAcesso</c> casam; <c>TokenCount</c> e <c>PasswordPolicy</c> não.
/// </summary>
/// <remarks>
/// <para>
/// Critério dos nomes terminados em <c>Key</c>: só casam com um qualificador que indica segredo na frente (<c>ApiKey</c>,
/// <c>SecretKey</c>, <c>SigningKey</c>, <c>EncryptionKey</c>, <c>MasterKey</c>...). <c>Key</c> sozinho e nomes comuns como
/// <c>PartitionKey</c>, <c>CacheKey</c>, <c>IdempotencyKey</c>, <c>RoutingKey</c>, <c>PublicKey</c>, <c>PrimaryKey</c> e
/// <c>ForeignKey</c> não casam: a chave de partição ou de cache é dado de diagnóstico, não segredo.
/// </para>
/// <para>
/// Português: em nomes compostos o substantivo vem primeiro (<c>SenhaAtual</c>, <c>SegredoCliente</c>), então <c>senha</c>,
/// <c>segredo</c> e <c>credencial</c> casam também no <b>início</b> do nome (<c>SenhaExpirada</c> é redigido junto; valores
/// <c>bool</c> nunca são redigidos). <c>chave</c> só casa como nome inteiro (<c>Chave</c>, <c>chaves</c>) ou com um qualificador
/// de segredo no fim (<c>ChaveDeAcesso</c>, <c>chave_privada</c>, <c>ChaveApi</c>, <c>ChavePix</c>): <c>ChaveNfe</c>,
/// <c>ChavePrimaria</c> e <c>ChaveCache</c> não.
/// </para>
/// <para>
/// Nomes próprios do serviço entram por <c>Observability:AdditionalSensitiveKeys</c> (<see cref="Register"/>), com o mesmo
/// critério das terminações embutidas. Sem alocação na comparação: roda para cada atributo de cada log e span exportado.
/// </para>
/// </remarks>
internal static class SensitiveKeys
{
    /// <summary>Tamanho mínimo e máximo (normalizado) de um nome de <c>AdditionalSensitiveKeys</c>.</summary>
    internal const int MinAdditionalKeyLength = 3;
    internal const int MaxAdditionalKeyLength = 128;

    /// <summary>Teto de nomes adicionais no processo (a lista é unida a cada recarga da configuração).</summary>
    internal const int MaxAdditionalKeys = 256;

    /// <summary>Terminações (já normalizadas: minúsculas, sem acento, sem separadores) que tornam o nome sensível.</summary>
    private static readonly string[] Suffixes =
    [
        "password", "passwd", "passphrase", "secret", "token", "connectionstring",
        "authorization", "credential", "cookie", "signature", "sessionid", "jwt",
        "cardnumber", "creditcard", "cvv", "cvc", "assertion",
        // *Key só com qualificador de segredo (ver remarks): ApiKey, SecretKey, SigningKey...
        "apikey", "accesskey", "accountkey", "privatekey", "subscriptionkey", "functionskey", "instrumentationkey",
        "secretkey", "signingkey", "signkey", "encryptionkey", "decryptionkey", "masterkey", "hmackey", "sharedkey", "clientkey",
        "authkey", "authenticationkey", "sessionkey", "cryptokey", "privkey", "saskey", "storagekey", "appkey", "licensekey",
        // Português (sem acento: a comparação remove os acentos do nome)
        "senha", "palavrapasse", "segredo", "credencial", "credenciais",
        "chaveacesso", "chavedeacesso", "chaveapi", "chavedaapi", "chavedeapi", "chaveprivada", "chavesecreta",
        "chaveassinatura", "chavedeassinatura", "chavecriptografia", "chavedecriptografia", "chavemestra", "chavepix",
        "tokenacesso", "tokendeacesso", "tokenatualizacao", "tokendeatualizacao", "tokenrenovacao", "tokenderenovacao",
        "codigoseguranca", "codigodeseguranca", "numerocartao", "numerodocartao", "cartaocredito", "cartaodecredito",
    ];

    /// <summary>Inícios (normalizados) que tornam o nome sensível: substantivos que, em português, vêm antes do qualificador.</summary>
    private static readonly string[] Prefixes = ["senha", "segredo", "credencial"];

    /// <summary>Nomes curtos sensíveis só quando são o nome inteiro (como sufixo casariam com nomes comuns).</summary>
    private static readonly string[] ExactNames = ["pwd", "sig", "otp", "pin", "cpf", "cnpj", "rg", "chave"];

    private static readonly Lock RegisterLock = new();

    /// <summary>Terminações de <c>AdditionalSensitiveKeys</c> (normalizadas), trocadas por cópia: a leitura não usa lock.</summary>
    private static volatile string[] _additional = [];

    /// <summary><c>true</c> quando o nome indica valor sensível (no singular ou no plural com <c>s</c>: <c>Cookies</c>, <c>Secrets</c>).</summary>
    public static bool IsSensitive(ReadOnlySpan<char> key)
    {
        if (key.IsEmpty)
            return false;

        if (Matches(key))
            return true;

        // Plural: o mesmo nome sem o "s" final (separadores no fim são ignorados, como na comparação).
        var last = key.Length - 1;
        while (last >= 0 && IsSeparator(key[last]))
            last--;
        return last > 0 && key[last] is 's' or 'S' && Matches(key[..last]);
    }

    /// <summary>
    /// Acrescenta nomes sensíveis do serviço (<c>Observability:AdditionalSensitiveKeys</c>), com o mesmo critério das terminações
    /// embutidas. Vale para o processo inteiro (a redação é estática, como os propagadores) e só soma: com vários hosts no mesmo
    /// processo, ou a cada recarga da configuração, vale a união das listas — esconder a mais é o lado seguro. Nomes inválidos
    /// (recusados na validação da subida) são ignorados aqui.
    /// </summary>
    internal static void Register(IEnumerable<string?>? names)
    {
        if (names is null)
            return;

        lock (RegisterLock)
        {
            var current = _additional;
            List<string>? added = null;
            foreach (var name in names)
            {
                if (Normalize(name) is not { } normalized || Array.IndexOf(current, normalized) >= 0 || added?.Contains(normalized) == true)
                    continue;
                if (current.Length + (added?.Count ?? 0) >= MaxAdditionalKeys)
                    break;
                (added ??= []).Add(normalized);
            }

            if (added is not null)
                _additional = [.. current, .. added];
        }
    }

    /// <summary>
    /// Forma normalizada de um nome adicional (minúsculas, sem acento, sem separadores), ou <c>null</c> quando ele não é aceito:
    /// vazio, com caractere que não é letra, dígito ou separador, ou fora de <see cref="MinAdditionalKeyLength"/> a
    /// <see cref="MaxAdditionalKeyLength"/> caracteres (um nome curto demais, como <c>id</c>, redigiria quase tudo).
    /// </summary>
    internal static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxAdditionalKeyLength * 2)
            return null;

        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (IsSeparator(c))
                continue;
            if (!char.IsLetterOrDigit(c))
                return null;
            builder.Append(Fold(c));
        }

        return builder.Length is >= MinAdditionalKeyLength and <= MaxAdditionalKeyLength ? builder.ToString() : null;
    }

    private static bool Matches(ReadOnlySpan<char> key)
    {
        // Conferência barata: só as terminações com o mesmo último caractere são comparadas por inteiro.
        var last = LastFolded(key);
        if (last == '\0')
            return false;

        foreach (var suffix in Suffixes)
        {
            if (suffix[^1] == last && EndsWithNormalized(key, suffix, out _))
                return true;
        }

        foreach (var suffix in _additional)
        {
            if (suffix[^1] == last && EndsWithNormalized(key, suffix, out _))
                return true;
        }

        foreach (var prefix in Prefixes)
        {
            if (StartsWithNormalized(key, prefix))
                return true;
        }

        foreach (var name in ExactNames)
        {
            if (name[^1] == last && EndsWithNormalized(key, name, out var consumed) && IsOnlySeparators(key[..^consumed]))
                return true;
        }

        return false;
    }

    /// <summary>Último caractere do nome que não é separador, normalizado; <c>'\0'</c> se só há separadores.</summary>
    private static char LastFolded(ReadOnlySpan<char> key)
    {
        for (var k = key.Length - 1; k >= 0; k--)
        {
            if (!IsSeparator(key[k]))
                return Fold(key[k]);
        }

        return '\0';
    }

    /// <summary>
    /// Compara o fim de <paramref name="key"/> com <paramref name="normalized"/> pulando separadores e ignorando maiúsculas e
    /// acentos. <paramref name="consumed"/> é quantos caracteres do fim de <paramref name="key"/> foram usados.
    /// </summary>
    private static bool EndsWithNormalized(ReadOnlySpan<char> key, string normalized, out int consumed)
    {
        var k = key.Length - 1;
        for (var n = normalized.Length - 1; n >= 0; n--)
        {
            while (k >= 0 && IsSeparator(key[k]))
                k--;
            if (k < 0 || Fold(key[k]) != normalized[n])
            {
                consumed = 0;
                return false;
            }

            k--;
        }

        consumed = key.Length - 1 - k;
        return true;
    }

    /// <summary>O mesmo que <see cref="EndsWithNormalized"/>, pelo início do nome.</summary>
    private static bool StartsWithNormalized(ReadOnlySpan<char> key, string normalized)
    {
        var k = 0;
        foreach (var expected in normalized)
        {
            while (k < key.Length && IsSeparator(key[k]))
                k++;
            if (k == key.Length || Fold(key[k]) != expected)
                return false;
            k++;
        }

        return true;
    }

    /// <summary>Minúscula sem acento (letras acentuadas do português e do espanhol); os demais caracteres só em minúscula.</summary>
    internal static char Fold(char c)
    {
        if (char.IsAscii(c))
            return char.ToLowerInvariant(c);

        return char.ToLowerInvariant(c) switch
        {
            'á' or 'à' or 'â' or 'ã' or 'ä' => 'a',
            'é' or 'è' or 'ê' or 'ë' => 'e',
            'í' or 'ì' or 'î' or 'ï' => 'i',
            'ó' or 'ò' or 'ô' or 'õ' or 'ö' => 'o',
            'ú' or 'ù' or 'û' or 'ü' => 'u',
            'ç' => 'c',
            'ñ' => 'n',
            var lower => lower,
        };
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
        result = RedactBracketPairs(result);
        return result;
    }

    /// <summary>URL absoluta sem credenciais, sem fragmento e com cada valor da query trocado (<c>?a=Redacted</c>).</summary>
    public static string RedactUrl(Uri uri)
    {
        var query = NeedsQueryRedaction(uri.Query) ? TelemetryRedaction.RedactQuery(uri.Query) : uri.Query;
        var authority = uri.Authority;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var at = authority.IndexOf('@');
            if (at >= 0)
                authority = authority[(at + 1)..];
        }

        return $"{uri.Scheme}{Uri.SchemeDelimiter}{authority}{uri.AbsolutePath}{query}";
    }

    /// <summary><c>true</c> quando a URL tem algo a redigir: query string, fragmento ou usuário e senha.</summary>
    public static bool NeedsRedaction(Uri uri) =>
        uri is { IsAbsoluteUri: true }
        && (NeedsQueryRedaction(uri.Query) || uri.Fragment.Length > 1 || !string.IsNullOrEmpty(uri.UserInfo));

    /// <summary>Query string ainda não redigida (já redigida: vazia, <c>?*</c> ou só valores <c>Redacted</c>).</summary>
    private static bool NeedsQueryRedaction(string query) =>
        TelemetryRedaction.HasQuery(query) && !string.Equals(TelemetryRedaction.RedactQuery(query), query, StringComparison.Ordinal);

    /// <summary>Cada URL do texto com algo a redigir vira o formato redigido.</summary>
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

                // Conferência barata antes do parse (que aloca o trecho e o Uri): só URLs com query, fragmento ou usuário podem ter
                // algo a redigir.
                if (text.AsSpan(start, end - start).IndexOfAny('?', '#', '@') >= 0
                    && Uri.TryCreate(text.Substring(start, end - start), UriKind.Absolute, out var uri) && NeedsRedaction(uri))
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

    /// <summary>Início do esquema (RFC 3986) imediatamente antes de <c>://</c>, ou -1.</summary>
    private static int SchemeStart(string text, int delimiter)
    {
        if (delimiter <= 0)
            return -1;

        var start = delimiter - 1;
        while (start >= 0 && (char.IsAsciiLetterOrDigit(text[start]) || text[start] is '+' or '-' or '.'))
            start--;

        var schemeStart = start + 1;
        if (schemeStart < delimiter && char.IsAsciiLetter(text[schemeStart]))
        {
            if (schemeStart == 0 || !char.IsAsciiLetterOrDigit(text[schemeStart - 1]))
                return schemeStart;
        }

        return -1;
    }

    private static bool IsUrlTerminator(char c) => char.IsWhiteSpace(c) || char.IsControl(c) || c is '"' or '\'' or '<' or '>' or '`' or '|';

    /// <summary>
    /// Valores de chaves sensíveis em pares <c>chave=valor</c> ou <c>chave: valor</c> (connection strings, JSON, cabeçalhos,
    /// argumentos): <c>Server=x;Password=abc;</c> vira <c>Server=x;Password=Redacted;</c>.
    /// </summary>
    internal static string RedactKeyValues(string text) => RedactKeyValues(text, out _);

    /// <summary>
    /// <see cref="RedactKeyValues(string)"/> informando quantos caracteres as buscas à frente (fim de linha e <c>;</c>)
    /// percorreram: no máximo duas vezes o tamanho do texto, qualquer que seja a quantidade de pares (os testes conferem a
    /// linearidade por aqui, sem depender de tempo).
    /// </summary>
    internal static string RedactKeyValues(string text, out int scannedChars)
    {
        scannedChars = 0;
        var index = text.AsSpan().IndexOfAny('=', ':');
        if (index < 0)
            return text;

        StringBuilder? builder = null;
        var lookahead = new Lookahead(text);
        var copied = 0;
        while (index >= 0 && index < text.Length)
        {
            var next = index + 1;
            if (text[index] == ':' && next < text.Length && text[next] == '/')
            {
                // "://" de URL (já tratada) ou "C:/": não é separador de par.
            }
            else if (KeyBefore(text, index) is { IsEmpty: false } key && SensitiveKeys.IsSensitive(key)
                && ValueAfter(text, next, IsCookieHeader(key), IsAuthorizationHeader(key), text[index] == '=', ref lookahead) is var (valueStart, valueEnd)
                && valueEnd > valueStart
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

        scannedChars = lookahead.Scanned;
        return builder is null ? text : builder.Append(text, copied, text.Length - copied).ToString();
    }

    /// <summary>
    /// Próxima quebra de linha e próximo <c>;</c> a partir de uma posição, com a última resposta guardada. As posições consultadas
    /// só avançam, então cada trecho do texto é percorrido no máximo uma vez por busca: sem isso, <c>token=a&amp;token=a&amp;...</c>
    /// sem quebra de linha procurava o fim do texto inteiro a cada par (custo quadrático).
    /// </summary>
    private struct Lookahead(string text)
    {
        private int _lineEndFrom = int.MaxValue;
        private int _lineEnd;
        private int _semicolonFrom = int.MaxValue;
        private int _semicolon;

        /// <summary>Caracteres percorridos pelas buscas.</summary>
        public int Scanned { get; private set; }

        /// <summary>Posição da próxima quebra de linha a partir de <paramref name="start"/>, ou o tamanho do texto.</summary>
        public int LineEnd(int start)
        {
            // A resposta guardada vale se foi buscada a partir de um ponto anterior e ainda está à frente de start.
            if (start < _lineEndFrom || _lineEnd < start)
            {
                var relative = text.AsSpan(start).IndexOfAny('\r', '\n');
                Scanned += relative < 0 ? text.Length - start : relative + 1;
                _lineEnd = relative < 0 ? text.Length : start + relative;
                _lineEndFrom = start;
            }

            return _lineEnd;
        }

        /// <summary>Posição do próximo <c>;</c> a partir de <paramref name="start"/>, ou o tamanho do texto.</summary>
        public int Semicolon(int start)
        {
            if (start < _semicolonFrom || _semicolon < start)
            {
                var relative = text.AsSpan(start).IndexOf(';');
                Scanned += relative < 0 ? text.Length - start : relative + 1;
                _semicolon = relative < 0 ? text.Length : start + relative;
                _semicolonFrom = start;
            }

            return _semicolon;
        }
    }

    /// <summary>
    /// Palavra (letras, dígitos e separadores de nome) antes do separador, pulando espaços, tabulações e uma aspa de fechamento.
    /// Letras acentuadas contam (<c>Código=...</c>).
    /// </summary>
    private static ReadOnlySpan<char> KeyBefore(string text, int separator)
    {
        var end = separator;
        while (end > 0 && (text[end - 1] == ' ' || text[end - 1] == '\t'))
            end--;
        if (end > 0 && text[end - 1] is '"' or '\'')
            end--;

        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '-' or '.'))
            start--;

        return text.AsSpan(start, end - start);
    }

    /// <summary><c>Cookie</c>/<c>Set-Cookie</c>: o valor tem vários pares separados por <c>;</c>, todos potencialmente de sessão.</summary>
    private static bool IsCookieHeader(ReadOnlySpan<char> key) =>
        key.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase);

    private static bool IsAuthorizationHeader(ReadOnlySpan<char> key) =>
        key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) || key.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Trecho do valor depois do separador.
    /// </summary>
    private static (int Start, int End) ValueAfter(string text, int start, bool isCookie, bool isAuthorization, bool isEquals, ref Lookahead lookahead)
    {
        while (start < text.Length && (text[start] == ' ' || text[start] == '\t'))
            start++;
        if (start >= text.Length)
            return (start, start);

        if (text[start] is '"' or '\'')
        {
            var quote = text[start];
            var close = start + 1;
            while ((close = text.IndexOf(quote, close)) >= 0 && IsEscaped(text, close))
                close++;
            return close < 0 ? (start + 1, text.Length) : (start + 1, close);
        }

        if (isAuthorization)
        {
            // Esquema (palavra só de letras seguida de espaço): percorre só a palavra, não o resto do texto.
            var word = start;
            while (word < text.Length && char.IsAsciiLetter(text[word]))
                word++;
            if (word > start && word < text.Length && text[word] == ' ')
            {
                start = word + 1;
                while (start < text.Length && (text[start] == ' ' || text[start] == '\t'))
                    start++;
            }

            var endLimit = lookahead.LineEnd(start);
            var sep = text.AsSpan(start, endLimit - start).IndexOfAny(';', ']', '}');
            return (start, sep < 0 ? endLimit : start + sep);
        }

        if (isCookie)
            return (start, lookahead.LineEnd(start));

        if (isEquals)
        {
            var limit = lookahead.LineEnd(start);
            var semi = lookahead.Semicolon(start);
            if (semi < limit)
                return (start, semi);
        }

        if (SchemeLength(text.AsSpan(start)) is > 0 and var schemeLength)
            start += schemeLength;

        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not (';' or ',' or '&' or '"' or '\'' or '}' or ']' or ')'))
            end++;
        return (start, end);
    }

    /// <summary>Caractere precedido por um número ímpar de <c>\</c> (aspa escapada dentro de um texto JSON).</summary>
    private static bool IsEscaped(string text, int index)
    {
        var backslashes = 0;
        while (index - backslashes - 1 >= 0 && text[index - backslashes - 1] == '\\')
            backslashes++;
        return backslashes % 2 == 1;
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

    /// <summary>Pares formatados como <c>[chave, valor]</c> (KeyValuePair.ToString() de dicionários e cabeçalhos).</summary>
    internal static string RedactBracketPairs(string text)
    {
        var index = text.IndexOf('[');
        if (index < 0)
            return text;

        StringBuilder? builder = null;
        var copied = 0;
        while (index >= 0 && index < text.Length)
        {
            var comma = text.IndexOf(',', index);
            var close = comma >= 0 ? text.IndexOf(']', comma) : -1;
            if (comma > index && close > comma)
            {
                var keySpan = text.AsSpan(index + 1, comma - index - 1).Trim();
                if (SensitiveKeys.IsSensitive(keySpan))
                {
                    var valStart = comma + 1;
                    while (valStart < close && (text[valStart] == ' ' || text[valStart] == '\t'))
                        valStart++;

                    if (close > valStart && !text.AsSpan(valStart, close - valStart).Equals(RedactedValue, StringComparison.Ordinal))
                    {
                        builder ??= new StringBuilder(text.Length);
                        builder.Append(text, copied, valStart - copied).Append(RedactedValue);
                        copied = close;
                    }
                }
            }

            index = close > 0 ? text.IndexOf('[', close) : -1;
        }

        return builder is null ? text : builder.Append(text, copied, text.Length - copied).ToString();
    }
}

/// <summary>
/// Redação de valores de atributo (de log ou de span) que não são texto: objetos (<c>record</c>, tipo anônimo, classe com
/// <c>ToString</c>), <see cref="Uri"/> em spans e coleções (<c>string[]</c>, <c>List&lt;string&gt;</c>, arrays de objetos). Os
/// exportadores (OTLP, console, Azure Monitor, arquivo) gravam esses valores pelo <c>ToString</c> ou item a item: um DTO com
/// <c>Password</c> chegaria inteiro ao backend.
/// </summary>
/// <remarks>
/// <para>
/// Objetos: o texto de <c>ToString</c> (cultura invariante, o mesmo que os exportadores gravam) passa pela redação de texto
/// livre (<see cref="SensitiveText"/>); se muda, o valor vira o texto redigido (o tipo se perderia na serialização de qualquer
/// forma). Coleções materializadas (<see cref="ICollection"/>, exceto dicionários): cada item vira texto e é varrido; se algum
/// muda, o valor vira um <c>string[]</c> com os itens redigidos (tipo de atributo nativo do OpenTelemetry).
/// </para>
/// <para>
/// Sem reflexão sobre as propriedades (seguro para trimming e Native AOT): só o que o próprio tipo expõe no <c>ToString</c> é
/// varrido — o mesmo texto que sairia no backend. Tipos escalares (primitivos, enums, <see cref="decimal"/>, datas,
/// <see cref="Guid"/>) e arrays deles não são convertidos. Custo: um <c>ToString</c> por valor não escalar (o exportador faz o
/// mesmo); sem nada a redigir, o valor original segue e um <c>string[]</c> não aloca.
/// </para>
/// </remarks>
internal static class SensitiveValue
{
    /// <summary>
    /// Valor redigido de um atributo que não é <see cref="string"/> (texto é tratado pelo chamador); <c>false</c> quando não há nada
    /// a redigir e o valor original deve seguir.
    /// </summary>
    public static bool TryRedact(object value, [NotNullWhen(true)] out object? redacted)
    {
        redacted = null;
        switch (value)
        {
            case string:
                return false;

            case IDictionary dict:
                return TryRedactDictionary(dict, out redacted);

            case string[] texts:
                return TryRedactItems(texts, texts.Length, out redacted);

            case ICollection items:
                return !IsScalarArray(items) && TryRedactItems(items, items.Count, out redacted);

            default:
                if (IsScalar(value) || Convert.ToString(value, CultureInfo.InvariantCulture) is not { } text)
                    return false;

                var safe = SensitiveText.Redact(text);
                if (ReferenceEquals(safe, text))
                    return false;

                redacted = safe;
                return true;
        }
    }

    private static bool TryRedactDictionary(IDictionary dict, [NotNullWhen(true)] out object? redacted)
    {
        redacted = null;
        var entries = new List<string>();
        var changed = false;
        foreach (DictionaryEntry entry in dict)
        {
            var keyStr = entry.Key?.ToString() ?? string.Empty;
            var valStr = entry.Value?.ToString() ?? string.Empty;
            if (SensitiveKeys.IsSensitive(keyStr))
            {
                entries.Add($"[{keyStr}, {SensitiveText.RedactedValue}]");
                changed = true;
            }
            else
            {
                var safe = SensitiveText.Redact(valStr);
                if (!ReferenceEquals(safe, valStr))
                    changed = true;
                entries.Add($"[{keyStr}, {safe}]");
            }
        }

        if (!changed)
            return false;

        redacted = string.Join(", ", entries);
        return true;
    }

    /// <summary>Itens como texto, varridos; se algum muda, a cópia com todos os itens como texto (redigidos).</summary>
    private static bool TryRedactItems(IEnumerable items, int count, [NotNullWhen(true)] out object? redacted)
    {
        redacted = null;
        var changed = false;
        foreach (var item in items)
        {
            if (Text(item) is { } text && !ReferenceEquals(SensitiveText.Redact(text), text))
            {
                changed = true;
                break;
            }
        }

        if (!changed)
            return false;

        var copy = new string?[count];
        var i = 0;
        foreach (var item in items)
        {
            if (i == copy.Length)
                break;
            copy[i++] = Text(item) is { } text ? SensitiveText.Redact(text) : null;
        }

        redacted = copy;
        return true;
    }

    private static string? Text(object? item) => item as string ?? (item is null ? null : Convert.ToString(item, CultureInfo.InvariantCulture));

    /// <summary>Tipos que não carregam texto livre: primitivos, enums, <see cref="decimal"/>, datas, durações e <see cref="Guid"/>.</summary>
    private static bool IsScalar(object value) =>
        value.GetType().IsPrimitive || value is Enum or decimal or DateTime or DateTimeOffset or TimeSpan or Guid or DateOnly or TimeOnly;

    private static bool IsScalarArray(ICollection items) =>
        items is Array array && array.GetType().GetElementType() is { } element
        && (element.IsPrimitive || element.IsEnum || element == typeof(decimal) || element == typeof(DateTime)
            || element == typeof(DateTimeOffset) || element == typeof(TimeSpan) || element == typeof(Guid));
}
