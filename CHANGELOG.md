# 📝 Changelog

Todas as mudanças relevantes do **TEC.Observability** são registradas aqui.
O formato segue o [Keep a Changelog](https://keepachangelog.com/pt-BR/1.1.0/) e o projeto usa
[Versionamento Semântico](https://semver.org/lang/pt-BR/). Os pacotes `TEC.Observability` e `TEC.Observability.Azure`
saem sempre juntos, com a mesma versão.

## [0.0.1] - 2026-10-08

Primeira versão.

### ✨ Adicionado

**TEC.Observability** (`net8.0` e `net10.0`, compatível com Native AOT)

- `AddTecObservability(IConfiguration, Action<ObservabilityOptions>?)`: traces, métricas e logs com OpenTelemetry numa
  única chamada, opções da seção `Observability` validadas na subida (`ValidateOnStart`) e devolução do
  `ObservabilityBuilder` (`AddExporter`, `AddActivitySource`, `AddMeter`, health checks de dependências).
- `UseTecObservability()`, `UseCorrelationId()` e `MapTecHealthChecks()` para o pipeline HTTP.
- Destino escolhido em `Observability:Provider`, um ou vários ao mesmo tempo: `None`, `Otlp` (gRPC ou HTTP/protobuf, com
  `OTEL_EXPORTER_OTLP_*` como padrão), `Console` (desenvolvimento), `LogFile` e exportadores próprios
  (`ITelemetryExporter`, `TelemetryExporterContext`).
- `LogFile`: arquivo `.txt` com uma linha por registro, troca por data e tamanho, retenção só dos arquivos do próprio
  serviço, gravação em lote, caracteres de controle escapados (sem *log forging*), limites de 32 K (mensagem), 64 K
  (exceção e cada escopo) e 192 K por registro, buffer com teto de 256 K caracteres e redação dos escopos ao gravar.
- Recurso com `service.*`, `service.instance.id` por processo, `host.name` e `deployment.environment`; fontes e medidores
  com o nome do `ServiceName` e o prefixo `TEC.*` assinados por padrão (sem dependência de nenhum componente TEC).
- Amostragem `ParentBased(TraceIdRatio(SamplingRatio))`; rotas de `IgnoredPaths`, endpoints de health, sondagens HTTP e
  chamadas a cofres do Azure fora dos traces.
- Redação antes de qualquer exportador: atributos de nome sensível, query string e credenciais em URL, fragmento,
  `Bearer`/`Basic` e pares `chave=valor` sensíveis em logs (com a mensagem refeita pelo modelo e a exceção trocada por
  uma cópia redigida quando necessário, para todos os exportadores) e spans (`SpanRedactionProcessor`: nomes sensíveis,
  valores de texto de qualquer atributo e descrição do status), caminho e atributos de spans do Key Vault; loggers do
  `IHttpClientFactory` com URL redigida no .NET 8. Escopos (`BeginScope`) só são redigidos pelo `LogFile` e eventos de
  span (`RecordException`) não são redigidos.
- Health checks: `/health/live` e `/health/ready` com JSON padronizado (`HealthCheckResponseWriter`), execução única,
  cache, prazo global, porta de gerência, detalhes só em `Development` ou com `ManagementPort`, `data` sensível e textos
  de terceiros redigidos, `TimeProvider` injetável; `AddDatabaseHealthCheck` (qualquer driver ADO.NET),
  `AddSsoHealthCheck` (OpenID Connect) e `AddExternalServiceHealthCheck`; `HealthCheckTags` e `HttpHealthCheckOptions`.
- Correlation id (`CorrelationId`, `X-Correlation-ID`): validado, ligado ao span, ao escopo de log, à resposta e ao
  Baggage; Baggage recebido descartado por padrão (`AllowInboundBaggage`) e enviado só a `BaggageAllowedHosts`.
- Convivência com o .NET Aspire `ServiceDefaults` (`OTEL_SERVICE_NAME`, reaproveitamento do check `self`).

**TEC.Observability.Azure** (`net8.0` e `net10.0`, sem Native AOT)

- `AddAzureMonitorExporter()` e `AzureMonitorExporterOptions` (`Observability:Azure`): Azure Monitor / Application
  Insights pela distro oficial, ligado só com `Azure` no `Provider`, amostragem por fração fixa e ingestão por chave ou
  Entra ID (fora de `Development`, só Workload Identity e Managed Identity).

**Qualidade**

- Testes TUnit em `net10.0`, `net8.0` e sem ICU: unidade, regressão, fuzzing (FsCheck), resistência a DoS e vazamento;
  integração com o Application Insights; carga (`Carga-CI`, `Carga-Pesada`) e fuzzing estendido (`Seguranca-Pesada`);
  benchmarks (BenchmarkDotNet).
- Samples: `TEC.Observability.SampleApi` (exportador `Contagem`) e `TEC.Observability.LoadGenerator`.
- CI/CD com os workflows reutilizáveis do tec-workflows (`ci.yml`, `release.yml`, `performance.yml`) e
  `integracao-azure.yml` para identidade gerenciada real.

[0.0.1]: https://github.com/tudoemcodigo/lib-tec-observability
