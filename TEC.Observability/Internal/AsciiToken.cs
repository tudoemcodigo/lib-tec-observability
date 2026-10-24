namespace TEC.Observability.Internal;

/// <summary>
/// Regra única dos identificadores aceitos pela biblioteca (nome de provedor, nome de cabeçalho OTLP, correlation id): só
/// letras e dígitos ASCII mais os caracteres extras de cada uso, com tamanho limitado.
/// </summary>
internal static class AsciiToken
{
    /// <summary><c>true</c> quando o valor não é vazio, tem até <paramref name="maxLength"/> caracteres e só letras/dígitos ASCII ou <paramref name="extras"/>.</summary>
    public static bool IsValid(string? value, int maxLength, ReadOnlySpan<char> extras)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
            return false;

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && !extras.Contains(c))
                return false;
        }

        return true;
    }
}
