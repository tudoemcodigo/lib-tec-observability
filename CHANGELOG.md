# 📝 Changelog

Todas as mudanças relevantes do **TEC.Observability** são registradas aqui.
O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto usa
[Versionamento Semântico](https://semver.org/lang/pt-BR/). Os pacotes `TEC.Observability` e `TEC.Observability.Azure`
saem sempre juntos, com a mesma versão.

## [0.0.1] - 2026-10-30

Primeira versão oficial do ecossistema TEC.

### ✨ Adicionado

**TEC.Observability** (`net10.0`, compatível com Native AOT)

- `AddTecObservability(IConfiguration, Action<ObservabilityOptions>?)`: traces, métricas e logs com OpenTelemetry numa
  única chamada, opções da seção `Observability` validadas na subida (`ValidateOnStart`) e devolução do
  `ObservabilityBuilder` (`AddExporter`, `AddActivitySource`, `AddMeter`, health checks de dependências).
- `UseTecObservability()`, `UseCorrelationId()` e `MapTecHealthChecks()` para o pipeline HTTP.
- Destino escolhido em `Observability:Provider`, um ou vários ao mesmo tempo: `None`, `Otlp` (gRPC ou HTTP/protobuf, para qualquer
  coletor OTLP: Grafana, Datadog Agent, New Relic, Dynatrace, AWS ADOT e Google Cloud, sem SDK do fornecedor),
  `Console` (desenvolvimento), `LogFile` e exportadores plugáveis (`ITelemetryExporter`, `TelemetryExporterContext`).
- `LogFile`: arquivo `.txt` com uma linha por registro, troca por data e tamanho, retenção dos arquivos do próprio serviço,
  gravação em lote, caracteres de controle escapados (sem *log forging*), limites de 32 K (mensagem), 64 K (exceção e escopo)
  e 192 K por registro, buffer com teto de 256 K caracteres e redação dos escopos ao gravar. O fim da linha segue a regra
  dos escopos (abaixo): para ver o correlation id no arquivo, ligue `IncludeScopes`.
- `LogFile:InstanceName`: várias instâncias na mesma pasta gravam em arquivos próprios (`servico.instancia-data.txt`), e a
  retenção de uma não apaga os arquivos da outra.
- Escopos de log desligados por padrão: a biblioteca não liga o `IncludeScopes` do OpenTelemetry. Com
  `Observability:IncludeScopes: true`, os escopos saem como atributos, só com as chaves de `Observability:AllowedScopeKeys`
  (o `correlation.id` sempre entra) e com os valores redigidos.
- Recurso com `service.*`, `service.instance.id` por processo, `host.name` e `deployment.environment`; fontes e medidores
  com o nome do `ServiceName` e o prefixo `TEC.*` assinados por padrão (sem acoplamento entre pacotes).
- Amostragem `ParentBased(TraceIdRatio(SamplingRatio))`; rotas de `IgnoredPaths`, endpoints de health e sondagens HTTP
  fora dos traces. O `DisableHttpMetrics` é sempre aplicado às sondas de health.
- Chamadas a cofres fora dos traces, reconhecidas pelo host: os de nuvem (Azure Key Vault, AWS Secrets Manager, GCP
  Secret Manager) de fábrica; HashiCorp Vault, OpenBao, Infisical e afins por `Observability:SecretStoreHosts` (nome exato
  ou `*.sufixo`, validados na subida). O caminho sozinho (`/v1/secret`, `/v1/sys`, `/api/v1/secrets`, `/api/v2/secrets`)
  não esconde a chamada: APIs de terceiros com o mesmo caminho geram span.
- Redação automática antes de qualquer exportador:
  - Atributos e chaves sensíveis: senhas, segredos, tokens, JWT, chaves de API, credenciais, connection strings, cookies,
    além de proteção LGPD / PCI-DSS com `cpf`, `cnpj`, `rg`, `cvv`, `cvc`, `cardnumber` e `creditcard`.
  - Nomes sensíveis em português (`senha`, `segredo`, `credencial`, `palavra-passe`, `chave` como nome inteiro e
    `ChaveDeAcesso`, `ChavePrivada`, `ChaveApi`, `ChavePix`, `TokenDeAcesso`, `CodigoDeSeguranca`, `NumeroDoCartao`...), com e
    sem acento, e variantes `*Key` com qualificador de segredo (`SecretKey`, `SigningKey`, `EncryptionKey`, `MasterKey`,
    `HmacKey`...). Nomes comuns como `PartitionKey`, `CacheKey`, `PublicKey` e `PrimaryKey` ficam fora.
  - `Observability:AdditionalSensitiveKeys`: nomes sensíveis próprios do serviço, validados na subida.
  - Spans e URLs: redação de caminhos e atributos de chamadas aos cofres de segredos.
  - Textos livres: query strings, credenciais `Bearer`/`Basic` e pares `chave=valor` sensíveis, sem custo quadrático
    (`token=a&token=a&...` sem quebra de linha não procura o fim do texto a cada par).
  - Exceções: trocadas por cópia redigida (`RedactedException`) quando contêm dados sensíveis.
  - `ObservabilityOptions.RedactAllLoggerProviders`: estende a redação a provedores fora do OpenTelemetry (`ILoggerFactory`).
- Health checks: `/health/live` e `/health/ready` com JSON padronizado (`HealthCheckResponseWriter`), execução única compartilhada,
  cache com `TimeProvider`, prazo global, porta de gerência (`ManagementPort`), teto contra saturação de execuções presas e redação de detalhes:
  - `AddDatabaseHealthCheck` (qualquer driver ADO.NET)
  - `AddTcpHealthCheck` (conectividade direta por socket TCP para Redis, RabbitMQ, Kafka, etc.)
  - `AddSsoHealthCheck` (validação de documento OIDC discovery)
  - `AddExternalServiceHealthCheck` (HTTP com retries)
- Liveness sem inicializador estático: o início do processo é lido só com `ExposeDetails`, e uma falha da plataforma não
  derruba as sondagens (o liveness segue `Healthy`, sem os campos de horário).
- Correlation id (`CorrelationId`, `X-Correlation-ID`): sanitizado contra injeções, propagado via W3C baggage apenas para
  hosts autorizados (`BaggageAllowedHosts`) e mantido mesmo com `Provider: None` (o valor recebido no Baggage é lido
  também do `Activity` da requisição, onde o ASP.NET Core o coloca quando não há instrumentação do OpenTelemetry).
- Baggage em redirecionamento: para um destino fora de `BaggageAllowedHosts`, os propagadores apagam o cabeçalho `baggage`
  (e `Correlation-Context`) que já estava na requisição, inclusive o escrito à mão pela aplicação.

**TEC.Observability.Azure** (`net10.0`, sem Native AOT)

- `AddAzureMonitorExporter()` e `AzureMonitorExporterOptions` (`Observability:Azure`): exportador oficial para Azure Monitor /
  Application Insights via distro Microsoft, amostragem fixa e autenticação por chave ou Entra ID (Workload Identity e Managed Identity).

### 📚 Documentação

- Guia completo em `docs/` (configuração, exportadores, traces/métricas/logs, redação, health checks, correlation id,
  .NET Aspire, integrações TEC, segurança, testes e desenvolvimento local), conferido contra o código, e a
  [revisão técnica](docs/revisao-tecnica.md) de 2026-10-19.
- [Registros de Decisões de Arquitetura](docs/adr/README.md) (ADR 0001 a 0008): pacotes, exportadores, redação,
  correlation id, health checks, exposição mínima, configuração validada na subida e integrações.

### 🧪 Qualidade

- Testes unitários em `TEC.Observability.Tests` e `TEC.Observability.Azure.Tests`, mais 3 de integração com o Application
  Insights e as suítes de carga e de fuzzing estendido, só sob demanda.
- Testes de regressão, resistência a DoS, fuzzing (FsCheck), concorrência e vazamento de memória sob carga, inclusive
  adversariais: falso positivo de nomes, entrada hostil linear, chave de escopo fora da lista, API de terceiro com caminho
  de cofre e redirecionamento para host não permitido. O teste do processador de logs confere a redação de
  `{OriginalFormat}`, `Body` e mensagem.
- Compatibilidade testada em `net10.0` e sem ICU.
- CI/CD via `tudoemcodigo/tec-workflows@v1`.

[0.0.1]: https://github.com/tudoemcodigo/lib-tec-observability/releases/tag/v0.0.1
