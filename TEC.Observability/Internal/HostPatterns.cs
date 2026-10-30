namespace TEC.Observability.Internal;

/// <summary>
/// Lista de hosts no formato das opções <c>BaggageAllowedHosts</c> e <c>SecretStoreHosts</c>: nome exato (<c>pedidos-api</c>),
/// sufixo com curinga (<c>*.svc.cluster.local</c>, que não inclui o próprio sufixo) e <c>*</c> (qualquer host). A comparação
/// não diferencia maiúsculas.
/// </summary>
internal sealed class HostPatterns
{
    private readonly bool _any;
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _suffixes = [];

    public HostPatterns(IEnumerable<string?> patterns)
    {
        foreach (var raw in patterns)
        {
            var pattern = raw?.Trim();
            if (string.IsNullOrEmpty(pattern))
                continue;
            if (pattern == "*")
                _any = true;
            else if (pattern.StartsWith("*.", StringComparison.Ordinal))
                _suffixes.Add(pattern[1..]); // ".svc.cluster.local"
            else
                _hosts.Add(pattern);
        }
    }

    /// <summary><c>true</c> quando a lista não tem nenhum padrão.</summary>
    public bool IsEmpty => !_any && _hosts.Count == 0 && _suffixes.Count == 0;

    /// <summary><c>true</c> quando o host casa com algum padrão.</summary>
    public bool Matches(string? host)
    {
        if (string.IsNullOrEmpty(host))
            return false;
        if (_any || _hosts.Contains(host))
            return true;

        foreach (var suffix in _suffixes)
        {
            if (host.Length > suffix.Length && host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
