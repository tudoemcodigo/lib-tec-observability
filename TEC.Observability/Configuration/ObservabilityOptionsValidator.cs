using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TEC.Observability.Exporters;
using TEC.Observability.HealthChecks;
using TEC.Observability.Internal;

namespace TEC.Observability.Configuration;

/// <summary>
/// Validação das opções efetivas (configuração completa + ajustes em código), executada na subida do host
/// (<c>ValidateOnStart</c>) e a cada leitura de <c>IOptions</c>: configuração errada derruba o serviço na subida, não na primeira
/// requisição.
/// </summary>
internal sealed class ObservabilityOptionsValidator(IConfiguration configuration, IHostEnvironment? environment, TelemetryExporterRegistry exporters,
    HealthCheckBudgets budgets)
    : IValidateOptions<ObservabilityOptions>
{
    internal const string OtlpEndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    internal const string OtlpHeadersVariable = "OTEL_EXPORTER_OTLP_HEADERS";

    /// <summary>Pacote e chamada que trazem o exportador do Azure Monitor (citados na mensagem quando faltam).</summary>
    internal const string AzurePackageHint =
        "instale o pacote TEC.Observability.Azure e chame .AddAzureMonitorExporter() no retorno do AddTecObservability";

    public ValidateOptionsResult Validate(string? name, ObservabilityOptions options)
    {
        var errors = GetErrors(options, configuration, environment?.IsDevelopment() == true);
        // Health checks registrados pela biblioteca precisam terminar dentro do prazo da execução inteira.
        if (options.HealthChecks is { Enabled: true } healthChecks)
            errors.AddRange(budgets.Exceeding(healthChecks.Timeout));
        // O pipeline do OpenTelemetry (quais exportadores existem) é montado no registro: o Provider precisa estar definido até ali.
        if (!exporters.Matches(options.Provider))
        {
            errors.Add($"Provider mudou depois do AddTecObservability ({string.Join(", ", exporters.RegisteredProviders)} no registro, "
                + $"{options.Provider} agora). Defina Observability:Provider num provedor de configuração adicionado antes da chamada.");
        }
        else
        {
            errors.AddRange(exporters.Missing.Select(missing => MissingExporterMessage(missing, exporters.Available)));
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Configuração inválida na seção '{ObservabilityOptions.SectionName}': {string.Join(" ", errors)}");
    }

    /// <summary>Provider sem exportador registrado: diz o que instalar e chamar.</summary>
    internal static string MissingExporterMessage(string provider, IEnumerable<string> available)
    {
        if (ObservabilityProvider.Is(provider, ObservabilityProvider.Azure))
            return $"Provider '{provider}' exige o exportador do Azure Monitor: {AzurePackageHint}.";

        var names = string.Join(", ", available);
        return $"Provider '{provider}' não tem exportador registrado: registre-o com AddExporter(...) no retorno do AddTecObservability "
            + $"(embutidos: {ObservabilityProvider.None}, {ObservabilityProvider.Otlp}, {ObservabilityProvider.Console}, {ObservabilityProvider.LogFile}"
            + (names.Length > 0 ? $"; registrados: {names})." : ").");
    }

    public static List<string> GetErrors(ObservabilityOptions options, IConfiguration configuration, bool isDevelopment)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        // Coleções e seções nulas (ex.: "Observability:Otlp": null ou ajuste em código) virariam NullReferenceException mais adiante.
        if (options.Otlp is null)
            errors.Add("Otlp não pode ser nulo.");
        else if (options.Otlp.Headers is null)
            errors.Add("Otlp:Headers não pode ser nulo (use uma lista vazia).");
        if (options.LogFile is null)
            errors.Add("LogFile não pode ser nulo.");
        if (options.HealthChecks is null)
            errors.Add("HealthChecks não pode ser nulo.");
        else if (options.HealthChecks.AllowedHosts is null)
            errors.Add("HealthChecks:AllowedHosts não pode ser nulo (use uma lista vazia).");
        if (options.BaggageAllowedHosts is null)
            errors.Add("BaggageAllowedHosts não pode ser nulo (use uma lista vazia).");
        else if (!options.BaggageAllowedHosts.All(IsHostPattern))
            errors.Add("BaggageAllowedHosts aceita só nomes de host, '*.sufixo' ou '*'.");
        if (errors.Count > 0)
            return errors;

        if (string.IsNullOrWhiteSpace(options.ServiceName))
            errors.Add("ServiceName é obrigatório (ou a variável OTEL_SERVICE_NAME).");
        if (string.IsNullOrWhiteSpace(options.ServiceVersion))
            errors.Add("ServiceVersion é obrigatório (o padrão vem do AssemblyInformationalVersion do assembly de entrada).");
        if (string.IsNullOrWhiteSpace(options.Environment))
            errors.Add("Environment é obrigatório.");
        if (ObservabilityProvider.Check(ObservabilityProvider.Parse(options.Provider)) is { } providerProblem)
            errors.Add(providerProblem);
        if (double.IsNaN(options.SamplingRatio) || options.SamplingRatio is < 0.0 or > 1.0)
            errors.Add("SamplingRatio deve estar entre 0.0 e 1.0.");

        // "/" (ou vazio) casaria com todas as rotas e desligaria o tracing inteiro sem aviso.
        if (options.IgnoredPaths is null)
            errors.Add("IgnoredPaths não pode ser nulo (use uma lista vazia).");
        else if (options.IgnoredPaths.Any(p => string.IsNullOrWhiteSpace(p) || p.Trim('/', ' ').Length == 0))
            errors.Add("IgnoredPaths não aceita itens vazios nem '/'.");

        // Nulos já recusados acima.
        var otlp = options.Otlp!;
        var healthChecks = options.HealthChecks!;

        if (ObservabilityProvider.Includes(options.Provider, ObservabilityProvider.LogFile))
            AddLogFileErrors(options.LogFile!, errors);

        if (ObservabilityProvider.Includes(options.Provider, ObservabilityProvider.Otlp))
        {
            // Sem Otlp:Endpoint vale a variável padrão do OpenTelemetry, que o próprio SDK lê e valida.
            var environmentEndpoint = configuration[OtlpEndpointVariable];
            if (otlp.Endpoint is { } ownEndpoint)
            {
                var problem = HttpUrl.Check(ownEndpoint);
                if (problem == HttpUrlProblem.NotHttp)
                    errors.Add("Otlp:Endpoint deve ser uma URL absoluta http ou https.");
                else if (problem == HttpUrlProblem.HasUserInfo)
                    errors.Add("Otlp:Endpoint não aceita usuário e senha na URL; use Otlp:Headers.");
            }
            else if (string.IsNullOrWhiteSpace(environmentEndpoint))
            {
                errors.Add($"Otlp:Endpoint é obrigatório (ou a variável {OtlpEndpointVariable}).");
            }
            if (!Enum.IsDefined(otlp.Protocol))
                errors.Add($"Otlp:Protocol inválido: {otlp.Protocol}.");

            // Os cabeçalhos vão ao SDK como "chave=valor,chave=valor", e ele separa por vírgula depois de decodificar: um separador
            // no nome ou uma vírgula no valor mudaria o que é enviado (ou derrubaria a exportação, sem erro visível para a aplicação).
            if (otlp.Headers!.Keys.Any(key => !IsHeaderName(key)))
                errors.Add("Otlp:Headers tem nome de cabeçalho inválido (use só letras, dígitos, '-' e '_').");
            if (otlp.Headers.Values.Any(value => value is null || value.AsSpan().IndexOfAny('\r', '\n', ',') >= 0))
                errors.Add("Otlp:Headers tem valor nulo, com vírgula ou com quebra de linha.");

            // Cabeçalhos costumam levar a chave do backend: fora da própria máquina (sidecar), só trafegam com TLS. Vale também
            // para os da variável padrão do OpenTelemetry, que o SDK envia quando Otlp:Headers está vazio.
            var effectiveEndpoint = otlp.Endpoint
                ?? (Uri.TryCreate(environmentEndpoint, UriKind.Absolute, out var parsed) ? parsed : null);
            var hasHeaders = otlp.Headers.Count > 0 || !string.IsNullOrWhiteSpace(configuration[OtlpHeadersVariable]);
            if (hasHeaders && !otlp.AllowInsecureTransport
                && effectiveEndpoint is { IsAbsoluteUri: true, IsLoopback: false } endpoint && endpoint.Scheme == Uri.UriSchemeHttp)
            {
                errors.Add($"Otlp:Headers (ou a variável {OtlpHeadersVariable}) com endpoint http (sem TLS) enviaria os valores em claro; "
                    + "use https ou, em rede confiável, Otlp:AllowInsecureTransport.");
            }
        }

        if (healthChecks.Enabled)
        {
            if (!IsRoute(healthChecks.LiveEndpoint))
                errors.Add("HealthChecks:LiveEndpoint deve começar com '/'.");
            if (!IsRoute(healthChecks.ReadyEndpoint))
                errors.Add("HealthChecks:ReadyEndpoint deve começar com '/'.");
            if (string.Equals(healthChecks.LiveEndpoint, healthChecks.ReadyEndpoint, StringComparison.OrdinalIgnoreCase))
                errors.Add("HealthChecks:LiveEndpoint e ReadyEndpoint devem ser rotas diferentes.");
            if (healthChecks.CacheDuration < TimeSpan.Zero || healthChecks.CacheDuration > TimeSpan.FromMinutes(1))
                errors.Add("HealthChecks:CacheDuration deve estar entre 0 e 1 minuto.");
            if (healthChecks.Timeout < TimeSpan.FromSeconds(1) || healthChecks.Timeout > TimeSpan.FromMinutes(5))
                errors.Add("HealthChecks:Timeout deve estar entre 1 segundo e 5 minutos.");
            if (healthChecks.ManagementPort is < 1 or > 65535)
                errors.Add("HealthChecks:ManagementPort deve estar entre 1 e 65535.");
            if (healthChecks.AllowedHosts!.Any(string.IsNullOrWhiteSpace))
                errors.Add("HealthChecks:AllowedHosts não aceita itens vazios.");
            if (healthChecks.RegisterLivenessCheck && string.IsNullOrWhiteSpace(healthChecks.LivenessCheckName))
                errors.Add("HealthChecks:LivenessCheckName é obrigatório quando RegisterLivenessCheck está ligado.");

            // Os endpoints são anônimos: fora de Development, nome/versão/ambiente e o detalhe das dependências só saem
            // quando os endpoints estão restritos à porta de gerência. AllowedHosts não conta: confere o cabeçalho Host,
            // que o cliente escolhe.
            if (!isDevelopment && healthChecks.ExposeDetails == true && healthChecks.ManagementPort is null)
            {
                errors.Add("HealthChecks:ExposeDetails=true fora de Development exige HealthChecks:ManagementPort (ex.: 8081), "
                    + "para o detalhe não ficar acessível a qualquer cliente (HealthChecks:AllowedHosts não basta: o cabeçalho Host é escolhido pelo cliente).");
            }
        }

        return errors;
    }

    private static void AddLogFileErrors(LogFileExporterSettings file, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(file.FolderPath) || file.FolderPath.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            errors.Add("LogFile:FolderPath deve ser um caminho de pasta válido.");

        // O nome vira parte do caminho do arquivo: separador ou ".." escreveria fora da pasta configurada.
        if (!string.IsNullOrEmpty(file.FileName)
            && (string.IsNullOrWhiteSpace(file.FileName) || file.FileName.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || file.FileName.Trim('.').Length == 0))
        {
            errors.Add("LogFile:FileName deve ser só um nome de arquivo, sem pasta nem extensão (ou vazio, para usar o ServiceName).");
        }

        if (!Enum.IsDefined(file.RollingInterval))
            errors.Add($"LogFile:RollingInterval inválido: {file.RollingInterval}.");
        if (file.MaxFileSizeBytes is < 0 or > 0 and < 1024)
            errors.Add("LogFile:MaxFileSizeBytes deve ser 0 (sem limite) ou ao menos 1024.");
        if (file.RetainedFileCount < 0)
            errors.Add("LogFile:RetainedFileCount não pode ser negativo (0 mantém todos os arquivos).");
        if (!Enum.IsDefined(file.MinimumLevel))
            errors.Add($"LogFile:MinimumLevel inválido: {file.MinimumLevel}.");
    }

    /// <summary><c>*</c>, <c>*.sufixo</c> ou um nome de host sem curinga.</summary>
    private static bool IsHostPattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Any(char.IsWhiteSpace))
            return false;
        if (pattern == "*")
            return true;

        var name = pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern;
        return name.Length > 0 && !name.Contains('*', StringComparison.Ordinal);
    }

    private static bool IsHeaderName(string? name) => AsciiToken.IsValid(name, int.MaxValue, "-_");

    private static bool IsRoute(string? value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith('/');
}
