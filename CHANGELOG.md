# 📝 Changelog

Todas as mudanças relevantes do **TEC.Observability** são registradas aqui.
O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto usa
[Versionamento Semântico](https://semver.org/lang/pt-BR/). Os pacotes `TEC.Observability` e `TEC.Observability.Azure`
saem sempre juntos, com a mesma versão.

## [0.0.1] - 2026-10-23

Primeira versão oficial do ecossistema TEC.

### ✨ Adicionado

**TEC.Observability** (`net8.0` e `net10.0`, 100% compatível com Native AOT)

- `AddTecObservability(IConfiguration, Action<ObservabilityOptions>?)`: traces, métricas e logs com OpenTelemetry numa
  única chamada, opções da seção `Observability` validadas na subida (`ValidateOnStart`) e devolução do
  `ObservabilityBuilder` (`AddExporter`, `AddActivitySource`, `AddMeter`, health checks de dependências).
- `UseTecObservability()`, `UseCorrelationId()` e `MapTecHealthChecks()` para o pipeline HTTP.
- Destino escolhido em `Observability:Provider`, um ou vários ao mesmo tempo: `None`, `Otlp` (gRPC ou HTTP/protobuf com
  interoperabilidade universal para Grafana Cloud, Datadog, New Relic, Dynatrace, AWS ADOT, Google Cloud Ops e Zabbix 7+ sem SDKs proprietários),
  `Console` (desenvolvimento), `LogFile` e exportadores plugáveis (`ITelemetryExporter`, `TelemetryExporterContext`).
- `LogFile`: arquivo `.txt` com uma linha por registro, troca por data e tamanho, retenção dos arquivos do próprio serviço,
  gravação em lote, caracteres de controle escapados (sem *log forging*), limites de 32 K (mensagem), 64 K (exceção e escopo)
  e 192 K por registro, buffer com teto de 256 K caracteres e redação dos escopos ao gravar.
- Recurso com `service.*`, `service.instance.id` por processo, `host.name` e `deployment.environment`; fontes e medidores
  com o nome do `ServiceName` e o prefixo `TEC.*` assinados por padrão (sem acoplamento entre pacotes).
- Amostragem `ParentBased(TraceIdRatio(SamplingRatio))`; rotas de `IgnoredPaths`, endpoints de health, sondagens HTTP e
  chamadas a cofres (Azure Key Vault, AWS Secrets Manager, GCP Secret Manager, HashiCorp Vault, Infisical) fora dos traces.
- Redação automática antes de qualquer exportador:
  - Atributos e chaves sensíveis: senhas, segredos, tokens, JWT, chaves de API, credenciais, connection strings, cookies,
    além de proteção LGPD / PCI-DSS com `cpf`, `cnpj`, `rg`, `cvv`, `cvc`, `cardnumber` e `creditcard`.
  - Spans e URLs: redação de caminhos e atributos de chamadas aos cofres de segredos (Azure Key Vault, AWS Secrets Manager,
    Google Secret Manager, HashiCorp Vault e Infisical).
  - Textos livres: query strings, credenciais `Bearer`/`Basic` e pares `chave=valor` sensíveis.
  - Exceções: trocadas por cópia redigida (`RedactedException`) quando contêm dados sensíveis.
  - `ObservabilityOptions.RedactAllLoggerProviders`: estende a redação a provedores fora do OpenTelemetry (`ILoggerFactory`).
- Health checks: `/health/live` e `/health/ready` com JSON padronizado (`HealthCheckResponseWriter`), execução única compartilhada,
  cache com `TimeProvider`, prazo global, porta de gerência (`ManagementPort`), teto contra saturação de execuções presas e redação de detalhes:
  - `AddDatabaseHealthCheck` (qualquer driver ADO.NET)
  - `AddTcpHealthCheck` (conectividade direta por socket TCP para Redis, RabbitMQ, Kafka, etc.)
  - `AddSsoHealthCheck` (validação de documento OIDC discovery)
  - `AddExternalServiceHealthCheck` (HTTP com retries)
- Correlation id (`CorrelationId`, `X-Correlation-ID`): sanitizado contra injeções, propagado via W3C baggage apenas para
  hosts autorizados (`BaggageAllowedHosts`) e mantido mesmo com `Provider: None`.

**TEC.Observability.Azure** (`net8.0` e `net10.0`, sem Native AOT)

- `AddAzureMonitorExporter()` e `AzureMonitorExporterOptions` (`Observability:Azure`): exportador oficial para Azure Monitor /
  Application Insights via distro Microsoft, amostragem fixa e autenticação por chave ou Entra ID (Workload Identity e Managed Identity).

### 📚 Documentação

- Guia completo em `docs/` (configuração, exportadores, traces/métricas/logs, redação, health checks, correlation id,
  .NET Aspire, integrações TEC, segurança, testes e desenvolvimento local), conferido contra o código, e a
  [revisão técnica](docs/revisao-tecnica.md) de 2026-10-19.
- [Registros de Decisões de Arquitetura](docs/adr/README.md) (ADR 0001 a 0008): pacotes, exportadores, redação,
  correlation id, health checks, exposição mínima, configuração validada na subida e integrações.

### 🧪 Qualidade

- 400 testes unitários por TFM (371 em `TEC.Observability.Tests` e 29 em `TEC.Observability.Azure.Tests`), mais 3 de
  integração com o Application Insights e as suítes de carga (11) e de fuzzing estendido, só sob demanda.
- Testes de regressão, resistência a DoS, fuzzing (FsCheck), concorrência e vazamento de memória sob carga.
- Compatibilidade testada em `net10.0`, `net8.0` e sem ICU.
- CI/CD via `tudoemcodigo/tec-workflows@v2`.

[0.0.1]: https://github.com/tudoemcodigo/lib-tec-observability
