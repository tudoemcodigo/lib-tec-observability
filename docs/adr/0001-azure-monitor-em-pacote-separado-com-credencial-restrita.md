# ADR 0001: Azure Monitor em Pacote Separado, com Credencial Restrita Fora de Desenvolvimento

## Status
Aprovado

## Data
2026-10-24

## Contexto
O TEC.Observability precisa servir serviços que exportam para o Azure Monitor / Application Insights e serviços que
exportam para qualquer outro coletor (OTLP, arquivo, console). A forma oficial de enviar telemetria ao Azure Monitor com
OpenTelemetry é a distro `Azure.Monitor.OpenTelemetry.AspNetCore`, que traz consigo `Azure.Identity` e uma árvore grande
de dependências. Isso gera três tensões:

1. **Native AOT e trimming:** o pacote base é compatível com Native AOT (binding de configuração gerado em compilação, JSON
   com `Utf8JsonWriter`). A distro do Azure Monitor, na versão usada (1.6.0), emite avisos `IL2026`/`IL3050` no próprio
   código (binding de `AzureMonitorOptions` por reflexão, instrumentação de SqlClient, EventSource de profiling).
2. **Superfície de dependências:** quem exporta só para `Otlp` não deveria carregar SDKs do Azure (tamanho do binário,
   supply chain, vulnerabilidades transitivas).
3. **Credencial:** o `DefaultAzureCredential` tenta uma cadeia longa, que inclui segredo em variável de ambiente e
   credenciais de ferramentas de desenvolvedor presentes na máquina. Em produção isso é indesejável.

## Decisão
1. **Dois pacotes, uma versão.** `TEC.Observability` não referencia o Azure; `TEC.Observability.Azure` referencia o pacote
   base (`ProjectReference`, que no pacote vira dependência com a mesma versão), `Azure.Identity` e a distro. Os dois saem
   sempre juntos, e o pacote Azure usa os mesmos namespaces do base (`RootNamespace` igual), para que
   `AddAzureMonitorExporter()` apareça ao lado de `AddTecObservability()` sem `using` novo
   ([`TEC.Observability.Azure.csproj`](../../TEC.Observability.Azure/TEC.Observability.Azure.csproj),
   [`TEC.Observability.csproj`](../../TEC.Observability/TEC.Observability.csproj)).
2. **AOT declarado só onde é verdade.** O base tem `IsAotCompatible=true`; o pacote Azure fica com `IsAotCompatible=false` e
   `IsTrimmable=false`, justificado no próprio `csproj`, até a distro ficar limpa.
3. **Ativação só por configuração.** `AddAzureMonitorExporter()` registra um `ITelemetryExporter` de nome `Azure`
   ([`AzureMonitorObservabilityBuilderExtensions.cs`](../../TEC.Observability.Azure/DependencyInjection/AzureMonitorObservabilityBuilderExtensions.cs));
   ele só é ligado quando `Azure` está em `Observability:Provider`, então o mesmo `Program.cs` serve a todos os ambientes
   (ver [ADR 0002](0002-exportadores-plugaveis-escolhidos-por-nome-no-provider.md)).
4. **Credencial fechada fora de `Development`.** Com `UseManagedIdentity`, fora de `Development` a credencial é
   `ChainedTokenCredential(WorkloadIdentityCredential, ManagedIdentityCredential)`; só em `Development` vale o
   `DefaultAzureCredential` ([`AzureMonitorTelemetryExporter.cs`](../../TEC.Observability.Azure/Exporters/AzureMonitorTelemetryExporter.cs)).
5. **Amostragem por fração fixa.** O limite por traces/segundo, padrão da distro, é desligado (`TracesPerSecond = null`) e
   `SamplingRatio` vira o `SamplingRatio` da distro, como nos demais provedores.
6. **Connection string sempre obrigatória**, inclusive com `UseManagedIdentity` (ela informa o endpoint de ingestão); pode
   vir de `Azure:ConnectionString` ou de `APPLICATIONINSIGHTS_CONNECTION_STRING`, e é validada na subida
   ([`AzureMonitorExporterOptions.cs`](../../TEC.Observability.Azure/Configuration/AzureMonitorExporterOptions.cs)).
7. **A redação não depende da distro.** A distro desliga a redação de query string da instrumentação; a biblioteca a
   reaplica por conta própria (ver [ADR 0003](0003-redacao-de-dados-sensiveis-antes-de-qualquer-exportador.md)).

Detalhes de uso em [exportadores.md](../exportadores.md#️-azure-monitor).

## Consequências
### Positivas
- Quem usa `Otlp`, `Console`, `LogFile` ou `None` mantém o pacote base enxuto e compatível com Native AOT.
- Nenhum SDK de nuvem entra no pacote base: menos superfície de ataque e de atualização.
- Em produção, a ingestão autenticada só aceita identidades de carga de trabalho, sem segredo em variável nem credencial de
  desenvolvedor.
- Trocar de backend continua sendo só configuração.

### Neutras / Negativas
- Dois pacotes para manter na mesma versão; o pacote Azure usa tipos internos do núcleo e não é compatível com versões
  diferentes do base.
- O pacote Azure não pode ser publicado com Native AOT enquanto a distro gerar avisos de trimming.
- Com `Azure` na lista de `Provider`, o sampler efetivo de todos os destinos passa a ser o `ApplicationInsightsSampler` da
  distro, que ignora a decisão `sampled` do chamador ([traces-metricas-logs.md](../traces-metricas-logs.md#-amostragem)).
