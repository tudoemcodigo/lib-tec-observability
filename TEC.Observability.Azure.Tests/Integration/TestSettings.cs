using Microsoft.Extensions.Configuration;

namespace TEC.Observability.Tests.Integration;

/// <summary>
/// Configuração dos testes de integração, com a mesma convenção em todos os componentes TEC (o arquivo é repetido em cada
/// projeto de testes: não há projeto compartilhado entre os repositórios).
/// </summary>
/// <remarks>
/// <para>Vale a primeira fonte com valor, nesta ordem:</para>
/// <list type="number">
/// <item><description>variável de ambiente do componente (<c>TEC_TESTES_&lt;COMPONENTE&gt;_*</c>), quando existir;</description></item>
/// <item><description>variável de ambiente comum (<c>TEC_TESTES_*</c>);</description></item>
/// <item><description><c>dotnet user-secrets</c> com o id <see cref="UserSecretsId"/> (o mesmo em todos os projetos de teste),
/// seção <see cref="Section"/>;</description></item>
/// <item><description>arquivo <c>appsettings.Local.json</c> da saída do projeto (ignorado pelo git), seção <see cref="Section"/>.</description></item>
/// </list>
/// <para>Nenhuma chave tem valor padrão apontando para recurso real: sem o cofre configurado, a integração é pulada.</para>
/// </remarks>
internal static class TestSettings
{
    /// <summary>Id do <c>dotnet user-secrets</c> compartilhado por todos os projetos de teste dos componentes TEC.</summary>
    public const string UserSecretsId = "tudoemcodigo-tec-testes";

    /// <summary>Seção das chaves no user-secrets e no <c>appsettings.Local.json</c>.</summary>
    public const string Section = "TecTestes";

    public const string VaultUriVariable = "TEC_TESTES_VAULT_URI";
    public const string TenantIdVariable = "TEC_TESTES_TENANT_ID";

    private static readonly IConfiguration? UserSecrets = Load(builder => builder.AddUserSecrets(UserSecretsId));

    private static readonly IConfiguration? LocalFile = Load(builder => builder
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.Local.json", optional: true));

    /// <summary>
    /// Primeiro valor encontrado: variáveis de ambiente (na ordem dada), depois user-secrets e por fim o
    /// <c>appsettings.Local.json</c> (em cada um, as chaves na ordem dada).
    /// </summary>
    public static string? Read(IEnumerable<string?> variables, IEnumerable<string> keys)
    {
        foreach (string? variable in variables)
        {
            if (variable is not null && Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
                return value.Trim();
        }

        foreach (var source in new[] { UserSecrets, LocalFile })
        {
            foreach (string key in keys)
            {
                if (source?[key] is { } value && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
        }

        return null;
    }

    /// <summary>Lê a variável do componente, a comum e a chave <c>TecTestes:{key}</c>.</summary>
    public static string? Read(string? componentVariable, string commonVariable, string key) =>
        Read([componentVariable, commonVariable], [$"{Section}:{key}"]);

    /// <summary>Tenant do Entra ID (opcional).</summary>
    public static string? TenantId(string? componentVariable) => Read(componentVariable, TenantIdVariable, "TenantId");

    /// <summary>URI do Key Vault de testes, ou <c>null</c> com o motivo em <paramref name="reason"/>.</summary>
    public static Uri? VaultUri(string? componentVariable, out string reason)
    {
        string? value = Read(componentVariable, VaultUriVariable, "VaultUri");
        if (value is null)
        {
            string variables = componentVariable is null ? VaultUriVariable : $"{componentVariable} ou {VaultUriVariable}";
            reason = $"Cofre de testes não configurado: defina {variables}, ou execute " +
                     $"'dotnet user-secrets set {Section}:VaultUri https://<cofre>.vault.azure.net/ --id {UserSecretsId}'.";
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = "A URI do cofre de testes configurada não é uma URL https válida.";
            return null;
        }

        reason = string.Empty;
        return uri;
    }

    private static IConfiguration? Load(Action<IConfigurationBuilder> configure)
    {
        try
        {
            var builder = new ConfigurationBuilder();
            configure(builder);
            return builder.Build();
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or IOException or InvalidDataException)
        {
            // Sem pasta de perfil (user-secrets) ou arquivo inválido: a fonte é ignorada, as demais continuam valendo
            return null;
        }
    }
}
