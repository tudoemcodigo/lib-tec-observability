using Microsoft.Extensions.Logging;
using TEC.Observability.Internal;

namespace TEC.Observability.Configuration;

/// <summary>
/// Nomes de destino da telemetria (<c>Observability:Provider</c>), comparados sem diferenciar maiúsculas. Trocar o valor no
/// <c>appsettings.json</c> troca o backend sem mudar código, e vários destinos podem ser ligados juntos separando os nomes por
/// vírgula ou ponto e vírgula (ex.: <c>"Console, LogFile"</c> ou <c>"Otlp;LogFile"</c>). <see cref="None"/>, <see cref="Otlp"/>,
/// <see cref="Console"/> e <see cref="LogFile"/> vêm no pacote base; os demais são exportadores registrados no retorno do
/// <c>AddTecObservability</c> (ex.: <see cref="Azure"/>, do pacote TEC.Observability.Azure, ou um exportador próprio com
/// <c>AddExporter</c>).
/// </summary>
public static class ObservabilityProvider
{
    /// <summary>Telemetria desligada (health checks e correlation id continuam ativos). Não pode ser combinado com outros destinos.</summary>
    public const string None = "None";

    /// <summary>
    /// Azure Monitor / Application Insights. Exige o pacote <c>TEC.Observability.Azure</c> e a chamada
    /// <c>.AddAzureMonitorExporter()</c> no retorno do <c>AddTecObservability</c>.
    /// </summary>
    public const string Azure = "Azure";

    /// <summary>Qualquer backend OTLP: AWS ADOT, GCP OTel Collector, Jaeger, Grafana, collector on-premises.</summary>
    public const string Otlp = "Otlp";

    /// <summary>Saída no console, para desenvolvimento.</summary>
    public const string Console = "Console";

    /// <summary>
    /// Logs em arquivo texto local (<c>.txt</c>), uma linha por registro, com troca de arquivo por data e tamanho e retenção.
    /// Opções em <c>Observability:LogFile</c>.
    /// </summary>
    public const string LogFile = "LogFile";

    private static readonly char[] Separators = [',', ';'];

    /// <summary><c>true</c> para os provedores que não precisam de exportador registrado (<see cref="None"/>, <see cref="Otlp"/>, <see cref="Console"/>, <see cref="LogFile"/>).</summary>
    internal static bool IsBuiltIn(string? provider) =>
        Is(provider, None) || Is(provider, Otlp) || Is(provider, Console) || Is(provider, LogFile);

    /// <summary>Compara nomes de provedor sem diferenciar maiúsculas.</summary>
    internal static bool Is(string? provider, string name) => string.Equals(provider, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Nome aceito: letras, dígitos, <c>.</c>, <c>-</c> e <c>_</c>, até 64 caracteres.</summary>
    internal static bool IsValidName(string? provider) => AsciiToken.IsValid(provider, 64, "._-");

    /// <summary>
    /// Destinos de <c>Observability:Provider</c>: nomes separados por vírgula ou ponto e vírgula, sem espaços nas pontas, sem
    /// vazios e sem repetição (fica a primeira grafia). Vazio equivale a <see cref="None"/>.
    /// </summary>
    internal static IReadOnlyList<string> Parse(string? provider)
    {
        var names = new List<string>();
        foreach (var name in (provider ?? string.Empty).Split(Separators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!names.Exists(n => Is(n, name)))
                names.Add(name);
        }

        return names.Count == 0 ? [None] : names;
    }

    /// <summary><c>true</c> quando <paramref name="name"/> está entre os destinos de <paramref name="provider"/>.</summary>
    internal static bool Includes(string? provider, string name) => Parse(provider).Any(n => Is(n, name));

    /// <summary>Problema na lista de destinos (nome inválido ou <see cref="None"/> combinado), ou <c>null</c> se ela é válida.</summary>
    internal static string? Check(IReadOnlyList<string> providers)
    {
        if (providers.FirstOrDefault(n => !IsValidName(n)) is { } invalid)
            return $"Provider inválido: {invalid}.";
        if (providers.Count > 1 && providers.Any(n => Is(n, None)))
            return $"Provider '{None}' desliga a telemetria e não pode ser combinado com outros destinos.";
        return null;
    }
}

/// <summary>Protocolo de transporte do OTLP.</summary>
public enum OtlpProtocol
{
    /// <summary>OTLP sobre gRPC (porta padrão 4317).</summary>
    Grpc = 0,

    /// <summary>OTLP sobre HTTP com protobuf (porta padrão 4318).</summary>
    HttpProtobuf,
}

/// <summary>Opções da biblioteca, ligadas à seção <c>Observability</c> do <c>appsettings.json</c>.</summary>
public sealed class ObservabilityOptions
{
    /// <summary>Nome da seção de configuração.</summary>
    public const string SectionName = "Observability";

    /// <summary>
    /// Nome do serviço (<c>service.name</c>). Obrigatório; vazio usa a variável <c>OTEL_SERVICE_NAME</c> (definida, por exemplo,
    /// pelo .NET Aspire). Também é o nome padrão do <c>ActivitySource</c> e do <c>Meter</c> registrados.
    /// </summary>
    public string ServiceName { get; set; } = string.Empty;

    /// <summary>
    /// Versão do serviço (<c>service.version</c>). Vazio usa o <c>AssemblyInformationalVersion</c> do assembly de entrada
    /// (a <c>Version</c> do projeto da aplicação).
    /// </summary>
    public string ServiceVersion { get; set; } = string.Empty;

    /// <summary>Ambiente de implantação (<c>deployment.environment</c>). Obrigatório.</summary>
    public string Environment { get; set; } = string.Empty;

    /// <summary>
    /// Destino da telemetria (constantes em <see cref="ObservabilityProvider"/>; sem diferenciar maiúsculas). Aceita um destino ou
    /// vários separados por vírgula ou ponto e vírgula, ligados ao mesmo tempo (ex.: <c>"Console, LogFile"</c> em desenvolvimento,
    /// <c>"Otlp, LogFile"</c> num servidor). Vazio equivale a <see cref="ObservabilityProvider.None"/>, que não pode ser combinado.
    /// Um nome que não é do pacote base (ex.: <c>Azure</c>) exige o exportador correspondente registrado no retorno do
    /// <c>AddTecObservability</c>; sem ele, a subida falha com a instrução.
    /// </summary>
    public string Provider { get; set; } = ObservabilityProvider.None;

    /// <summary>Opções do OTLP (usadas quando <see cref="Provider"/> inclui <see cref="ObservabilityProvider.Otlp"/>).</summary>
    public OtlpExporterSettings Otlp { get; set; } = new();

    /// <summary>Opções do arquivo de logs (usadas quando <see cref="Provider"/> inclui <see cref="ObservabilityProvider.LogFile"/>).</summary>
    public LogFileExporterSettings LogFile { get; set; } = new();

    /// <summary>
    /// Fração de traces amostrados, de 0.0 a 1.0. O sampler efetivo depende do <see cref="Provider"/>: sem
    /// <see cref="ObservabilityProvider.Azure"/>, <c>ParentBased(TraceIdRatio(SamplingRatio))</c>, que respeita a decisão do
    /// chamador no <c>traceparent</c> recebido — inclusive a de um cliente externo, que pode enviar <c>sampled=01</c> e anular a
    /// fração (remova ou ignore o <c>traceparent</c> de tráfego externo na borda). Com <see cref="ObservabilityProvider.Azure"/>
    /// na lista (ex.: <c>"Azure, LogFile"</c>), o <c>ApplicationInsightsSampler</c> da distro do Azure Monitor, que decide por
    /// um hash do TraceId e ignora a decisão do chamador.
    /// </summary>
    public double SamplingRatio { get; set; } = 1.0;

    /// <summary>Prefixos de rota que não geram trace (comparação sem diferenciar maiúsculas). Definir no <c>appsettings.json</c> substitui a lista padrão.</summary>
    public IList<string> IgnoredPaths { get; set; } = ["/health", "/health/live", "/health/ready", "/swagger"];

    /// <summary>
    /// Mantém e repassa aos serviços chamados os itens de Baggage (cabeçalho <c>baggage</c>) recebidos na requisição. Desligado por
    /// padrão: qualquer cliente pode enviar esse cabeçalho, e o conteúdo seguiria para toda a cadeia de serviços e para os logs.
    /// Ligue só em serviços que recebem tráfego apenas de serviços confiáveis.
    /// </summary>
    public bool AllowInboundBaggage { get; set; }

    /// <summary>
    /// Hosts para os quais o cabeçalho <c>baggage</c> (que leva o correlation id) é enviado nas chamadas HTTP de saída. Aceita o
    /// nome exato (<c>pedidos-api</c>), sufixo com curinga (<c>*.svc.cluster.local</c>, que não inclui o próprio sufixo) e
    /// <c>*</c> (qualquer host). Vazio por padrão: nada é enviado, para o correlation id e o Baggage não vazarem para APIs de
    /// terceiros. O <c>traceparent</c> não é afetado, e o serviço chamado usa o TraceId como correlation id quando não recebe Baggage.
    /// </summary>
    public IList<string> BaggageAllowedHosts { get; set; } = [];

    /// <summary>Opções dos endpoints de health check.</summary>
    public ObservabilityHealthChecksOptions HealthChecks { get; set; } = new();
}

/// <summary>Opções do exporter OTLP.</summary>
public sealed class OtlpExporterSettings
{
    /// <summary>
    /// Endereço base do coletor, ex.: <c>http://otel-collector:4317</c> (gRPC) ou <c>http://otel-collector:4318</c> (HTTP). Vazio
    /// usa as variáveis padrão do OpenTelemetry (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c> e <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>, definidas,
    /// por exemplo, pelo .NET Aspire); nesse caso <see cref="Protocol"/> é ignorado.
    /// </summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Protocolo de transporte (usado quando <see cref="Endpoint"/> está definido).</summary>
    public OtlpProtocol Protocol { get; set; } = OtlpProtocol.Grpc;

    /// <summary>
    /// Cabeçalhos enviados em cada exportação (ex.: chave de API do backend). Trate os valores como segredo. Vazio usa a variável
    /// <c>OTEL_EXPORTER_OTLP_HEADERS</c>. São lidos quando o exportador é criado (na construção do provedor de telemetria), então
    /// podem vir de um provedor de configuração adicionado depois do <c>AddTecObservability</c> (ex.: cofre de segredos).
    /// </summary>
    public IDictionary<string, string> Headers { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Aceita enviar <see cref="Headers"/> por <c>http</c> (sem TLS) a um endereço que não é a própria máquina. Desligado por
    /// padrão; ligue só quando o tráfego até o coletor já é protegido pela rede (ex.: service mesh com mTLS).
    /// </summary>
    public bool AllowInsecureTransport { get; set; }
}

/// <summary>Periodicidade com que o arquivo de logs troca de nome.</summary>
public enum LogFileRollingInterval
{
    /// <summary>Um arquivo por dia (<c>servico-20261005.txt</c>).</summary>
    Day = 0,

    /// <summary>Um arquivo por hora (<c>servico-2026100514.txt</c>).</summary>
    Hour,

    /// <summary>Sem troca por data (<c>servico.txt</c>); só a troca por tamanho.</summary>
    None,
}

/// <summary>
/// Opções do arquivo de logs (<c>Observability:LogFile</c>). Cada registro vira uma linha:
/// <c>2026-10-05 14:03:22.123 -03:00 [INF] Categoria: mensagem | trace_id=... span_id=... | CorrelationId=...</c>; a exceção,
/// quando houver, e as quebras de linha da mensagem vão nas linhas seguintes, recuadas.
/// </summary>
public sealed class LogFileExporterSettings
{
    /// <summary>Pasta dos arquivos. Caminho relativo é resolvido a partir da pasta da aplicação (<c>AppContext.BaseDirectory</c>).</summary>
    public string FolderPath { get; set; } = "logs";

    /// <summary>
    /// Início do nome dos arquivos, sem extensão (ex.: <c>pedidos-api</c> gera <c>pedidos-api-20261005.txt</c>). Vazio usa o
    /// <c>ServiceName</c>. Não aceita separadores de pasta.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Troca de arquivo por data (padrão: um por dia).</summary>
    public LogFileRollingInterval RollingInterval { get; set; } = LogFileRollingInterval.Day;

    /// <summary>
    /// Tamanho a partir do qual um novo arquivo é aberto no mesmo período (<c>servico-20261005_001.txt</c>). Padrão: 10 MB;
    /// mínimo 1 KB; zero desliga a troca por tamanho.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Quantidade de arquivos mantidos na pasta; os mais antigos são apagados. Padrão: 31; zero mantém todos.</summary>
    public int RetainedFileCount { get; set; } = 31;

    /// <summary>
    /// Nível mínimo gravado no arquivo, além dos filtros de <c>Logging:LogLevel</c> (ex.: <c>Warning</c> para o arquivo guardar só
    /// avisos e erros enquanto o console mostra tudo). Padrão: <c>Trace</c> (grava tudo o que os filtros de logging deixam passar).
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Trace;

    /// <summary>Inclui os escopos do log (ex.: <c>CorrelationId</c>, <c>RequestPath</c>) no fim da linha. Padrão: ligado.</summary>
    public bool IncludeScopes { get; set; } = true;
}

/// <summary>Opções dos endpoints de health check.</summary>
public sealed class ObservabilityHealthChecksOptions
{
    /// <summary>Registra o liveness e mapeia os endpoints.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Registra a verificação de liveness da biblioteca (tag <c>live</c>). Desligue quando outra biblioteca já registra a sua
    /// (ex.: o <c>AddServiceDefaults</c> do .NET Aspire, com o check <c>self</c>).
    /// </summary>
    public bool RegisterLivenessCheck { get; set; } = true;

    /// <summary>
    /// Nome da verificação de liveness. Se já existir um health check com esse nome (ex.: o <c>self</c> do .NET Aspire
    /// ServiceDefaults), o da biblioteca não é registrado e o existente é usado — desde que tenha a tag <c>live</c>; sem ela, a
    /// subida do host falha com <see cref="InvalidOperationException"/> (adicione a tag, use outro nome ou desligue
    /// <see cref="RegisterLivenessCheck"/>).
    /// </summary>
    public string LivenessCheckName { get; set; } = "self";

    /// <summary>Rota do liveness (só a saúde do processo).</summary>
    public string LiveEndpoint { get; set; } = "/health/live";

    /// <summary>Rota do readiness (dependências).</summary>
    public string ReadyEndpoint { get; set; } = "/health/ready";

    /// <summary>
    /// Tempo em que o resultado de um endpoint é reaproveitado (padrão: 5 segundos; máximo 1 minuto; zero desliga). Os endpoints são
    /// anônimos e o readiness abre conexões de banco e chamadas HTTP: sem isso, uma rajada de requisições viraria carga nas dependências.
    /// Requisições simultâneas sempre compartilham uma única execução, mesmo com o cache desligado.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Tempo máximo de uma execução das verificações de um endpoint (padrão: 30 segundos; de 1 segundo a 5 minutos). Cada
    /// verificação já tem o seu timeout, mas uma que ignore o cancelamento e nunca termine prenderia a execução compartilhada —
    /// e, com ela, todas as requisições seguintes. Estourado o prazo, o endpoint responde <c>Unhealthy</c> (503), a falha vai
    /// para o log e a próxima requisição (passado o <see cref="CacheDuration"/>) dispara uma execução nova.
    /// Precisa ser maior que o tempo máximo de cada health check registrado pela biblioteca — HTTP (SSO e serviço externo):
    /// <c>(Timeout + 1s) × (Retries + 1)</c>; banco: o <c>timeout</c> do check —, conferido na subida
    /// (<see cref="Microsoft.Extensions.Options.OptionsValidationException"/>): as verificações rodam em paralelo, e uma mais
    /// longa que o prazo seria cortada por ele sem dizer qual dependência demorou.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Porta de gerência: quando definida, os endpoints só respondem em conexões recebidas nessa porta local (as demais recebem
    /// 404). A conferência é feita na conexão (<c>HttpContext.Connection.LocalPort</c>), não no cabeçalho <c>Host</c>, então
    /// o cliente não consegue burlá-la. Faça o servidor escutar também nessa porta (ex.: <c>ASPNETCORE_HTTP_PORTS=8080;8081</c>)
    /// e não a publique no balanceador. Atrás de um proxy que termina a conexão no próprio contêiner (sidecar), a porta local é
    /// a que o proxy usa para chegar à aplicação.
    /// </summary>
    public int? ManagementPort { get; set; }

    /// <summary>
    /// Restringe os endpoints pelo cabeçalho <c>Host</c>, no formato do <c>RequireHost</c> (ex.: <c>*:8081</c>). Vazio aceita
    /// qualquer host. <b>Não é barreira de segurança</b>: o valor do cabeçalho <c>Host</c> é escolhido pelo cliente, que pode
    /// enviar <c>Host: qualquer:8081</c> pela porta pública. Serve para organizar o roteamento; para restringir de fato, use
    /// <see cref="ManagementPort"/> (conferida na conexão) ou a rede.
    /// </summary>
    public IList<string> AllowedHosts { get; set; } = [];

    /// <summary>
    /// Inclui no JSON a identificação do serviço (nome, versão, ambiente) e o detalhe de cada verificação. Sem detalhes, a resposta
    /// tem só o status geral e o horário — o suficiente para sondas e balanceadores, que usam o código HTTP.
    /// Vazio (padrão) decide pelo ambiente: ligado em <c>Development</c>, desligado nos demais. Fora de <c>Development</c>,
    /// <c>true</c> só é aceito com <see cref="ManagementPort"/> definida (endpoints restritos à porta de gerência);
    /// <see cref="AllowedHosts"/> não basta, porque o cabeçalho <c>Host</c> é controlado pelo cliente.
    /// </summary>
    public bool? ExposeDetails { get; set; }

    /// <summary>
    /// Inclui a mensagem da exceção no JSON. Desligado por padrão: endpoints de health costumam ser acessíveis sem autenticação e
    /// mensagens de banco/HTTP podem revelar hosts e usuários. O stack trace nunca é exposto; o detalhe completo vai para o log.
    /// </summary>
    public bool ExposeExceptionDetails { get; set; }
}
