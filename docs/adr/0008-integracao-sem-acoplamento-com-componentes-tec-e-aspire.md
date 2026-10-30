# ADR 0008: Integração sem Acoplamento com os Componentes TEC e Convivência com o .NET Aspire

## Status
Aprovado

## Data
2026-10-24

## Contexto
O ecossistema TEC (Core, Vault, Cqrs, Security, ORM) precisa de traces e métricas no mesmo trace da requisição, mas:

1. **Ciclos e versões acopladas.** Se cada componente referenciasse o TEC.Observability (ou o contrário), a ordem de
   publicação e as versões ficariam presas entre si.
2. **Quem não usa o TEC.Observability.** Um serviço pode usar o TEC.Cqrs com o OpenTelemetry puro, ou com o `ServiceDefaults`
   do .NET Aspire.
3. **Aspire.** O `ServiceDefaults` já configura o OpenTelemetry (exportação via `UseOtlpExporter`) e registra um check de
   liveness `self`; dois exportadores OTLP no mesmo container fazem o SDK lançar `NotSupportedException`, e um liveness
   duplicado viraria nome de health check repetido.
4. **Chamadas a cofres.** Os componentes de cofre (TEC.Vault) fazem chamadas HTTP cuja URL traz nome e versão do segredo.

## Decisão
1. **Zero dependência entre os pacotes.** O TEC.Observability não referencia nenhum `TEC.*` em produção, e nenhum componente
   referencia o TEC.Observability. A única referência a um componente TEC é de **teste** (`TecReference` para o
   `TEC.Vault.AzureKeyVault` em `TEC.Observability.Azure.Tests`), e não vai para os pacotes
   ([`TEC.Observability.csproj`](../../TEC.Observability/TEC.Observability.csproj),
   [`Directory.Packages.props`](../../Directory.Packages.props)).
2. **Integração por nome.** Cada componente emite `ActivitySource` e `Meter` da BCL com o nome `TEC.<Componente>`; o
   TEC.Observability assina o prefixo `TEC.*` (`TecTelemetryPrefix`) e o nome igual ao `ServiceName`. Quem não usa o
   TEC.Observability assina os nomes no próprio OpenTelemetry.
3. **Fontes, medidores e redação valem para qualquer provedor do container.** São registrados mesmo com `Provider: None`, no
   provedor montado por outra biblioteca (ex.: Aspire `ServiceDefaults`); os processadores de redação entram antes dos
   exportadores dele (ver [ADR 0003](0003-redacao-de-dados-sensiveis-antes-de-qualquer-exportador.md)).
4. **Liveness reaproveitável e filtros compostos.** Um check de mesmo nome com a tag `live` (o `self` do Aspire) é usado em
   vez de duplicado; os filtros de rota e de sondagem são compostos por pós-configuração com os da distro do Azure, do
   `ServiceDefaults` e do serviço, sem sobrescrever nenhum
   ([`ServiceCollectionExtensions.cs`](../../TEC.Observability/DependencyInjection/ServiceCollectionExtensions.cs)).
5. **Um só exporta.** Com o `ServiceDefaults` exportando, o `Provider` é `None`; com o TEC.Observability exportando, remove-se
   o `UseOtlpExporter`/`UseAzureMonitor` do `ServiceDefaults`. As variáveis `OTEL_SERVICE_NAME` e `OTEL_EXPORTER_OTLP_*`
   continuam valendo.
6. **Cofres fora dos traces, sem conhecer o TEC.Vault.** Chamadas do `HttpClient` a cofres de segredos não geram span: os
   de nuvem (Azure, AWS, GCP) são reconhecidos pelo host; os próprios (HashiCorp Vault, Infisical...), pelos hosts de
   `SecretStoreHosts`, configurados pelo serviço. A biblioteca não presume os caminhos de nenhum componente de cofre; as
   operações continuam visíveis pelos spans do próprio TEC.Vault (fontes `TEC.*`)
   ([`TelemetryRedaction.cs`](../../TEC.Observability/Tracing/TelemetryRedaction.cs)). Segredos de telemetria podem vir do
   TEC.Vault como provedor de configuração (ver [ADR 0007](0007-configuracao-validada-na-subida-e-lida-na-criacao-dos-componentes.md)),
   e o health check do cofre entra no `/health/ready`.

Detalhes em [integracoes-tec.md](../integracoes-tec.md) e [aspire.md](../aspire.md).

## Consequências
### Positivas
- Cada componente evolui e é publicado de forma independente, sem ciclo de dependência.
- Os spans dos componentes TEC aparecem no mesmo trace da requisição sem código de integração.
- Convive com o .NET Aspire sem health check duplicado nem redação perdida.

### Neutras / Negativas
- A integração depende do contrato de nomes (`TEC.<Componente>`): um componente que renomear sua fonte deixa de ser assinado.
- Um pacote-ponte só faria sentido para uma integração que exigisse código dos dois lados.
- Com `Provider: None`, `IgnoredPaths` e o filtro das sondagens **não** são aplicados no pipeline do `ServiceDefaults`:
  as rotas ignoradas precisam ser configuradas também lá.
