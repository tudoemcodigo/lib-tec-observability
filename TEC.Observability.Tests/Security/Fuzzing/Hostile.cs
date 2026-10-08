using FsCheck;
using FsCheck.Fluent;

namespace TEC.Observability.Tests.Security.Fuzzing;

/// <summary>
/// Geradores de entradas hostis para fuzzing: textos montados com pedaços que costumam quebrar validadores de cabeçalho, URLs,
/// caminhos e nomes de arquivo (quebras de linha, NUL, separadores de query e de caminho, travessia de pasta, codificação
/// percentual, homóglifos Unicode, caracteres invisíveis e de direção, emojis, surrogates).
/// </summary>
internal static class Hostile
{
    private static readonly string[] Tokens =
    [
        "a", "Z", "0", "9", "-", "_", ".", ":", " ", "\t", "\r", "\n", "\r\n", "\0", "\u0085", "\u2028", "\u202E", "\uFEFF", "\u00A0",
        "<script>", "\"", "'", ",", ";", "=", "&", "?", "#", "/", "\\", "..", "../", "..\\", "*", "%", "%0d%0a", "%2F", "%2e%2e", "%00",
        "\u00E7", "\u00C9", "\uFF21", "\uFF11", "\u0661", "\u0130", "\u0131", "\u212A", "\U0001F600", "\U0001F1E7\U0001F1F7", "{", "}", "[", "]", "|", "<", ">", "@", "+",
        "health", "HEALTH", "secrets", "vault.azure.net", "correlation.id", "baggage", "CON", "NUL", "token", "Redacted", "***",
    ];

    // Caracteres isolados (sem surrogates, que não existem sozinhos em UTF-16 válido)
    private static readonly char[] Chars = [.. Tokens.Where(t => t.Length == 1).Select(t => t[0])];

    /// <summary>Texto UTF-16 válido (sem surrogates isolados), de 0 a ~100 pedaços.</summary>
    public static Gen<string> Text { get; } = Gen.Elements(Tokens).ListOf().Select(string.Concat);

    /// <summary>Texto não vazio.</summary>
    public static Gen<string> NonEmptyText { get; } = Gen.Elements(Tokens).NonEmptyListOf().Select(string.Concat);

    /// <summary>Texto que pode conter surrogates isolados (UTF-16 inválido), como vem de entradas corrompidas ou truncadas.</summary>
    public static Gen<string> AnyText { get; } = Gen.OneOf(
        Text,
        Text.Select(s => s + "\uD800"),
        Text.Select(s => "\uDC00" + s),
        Gen.Elements(Tokens).ListOf().Select(parts => string.Join("\uD83D", parts)));

    /// <summary>Texto ASCII imprimível (o que um cabeçalho HTTP carrega sem codificação).</summary>
    public static Gen<string> AsciiText { get; } =
        Gen.Choose(0x20, 0x7E).Select(c => (char)c).ListOf().Select(chars => new string([.. chars]));

    /// <summary>Caractere isolado hostil.</summary>
    public static Gen<char> Char { get; } = Gen.Elements(Chars);

    /// <summary>Configuração padrão: falha com o contraexemplo na mensagem; <paramref name="maxTest"/> casos por propriedade.</summary>
    public static Config Config(int maxTest = 300) => FsCheck.Config.QuickThrowOnFailure.WithMaxTest(maxTest).WithQuietOnSuccess(true);

    /// <summary>Texto visível no relatório de falha (caracteres de controle e invisíveis escapados).</summary>
    public static string Show(string? value) =>
        value is null ? "null" : string.Concat(value.Select(c => c is < ' ' or > '~' ? $"\\u{(int)c:X4}" : c.ToString()));
}
