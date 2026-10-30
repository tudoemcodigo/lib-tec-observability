# ADR 0005: Health Checks com Liveness e Readiness Separados, Execução Única, Cache e Prazo

## Status
Aprovado

## Data
2026-10-24

## Contexto
Cada serviço costuma ter um `/health` próprio, e dois problemas se repetem:

1. **Confundir processo com dependência.** Um banco fora do ar não deve fazer o orquestrador reiniciar todas as instâncias;
   deve tirá-las do balanceamento.
2. **Endpoints anônimos como alavanca de carga.** O readiness abre conexões de banco e faz chamadas HTTP. Uma rajada de
   sondas (ou um atacante) multiplicaria essa carga nas dependências, e uma verificação que ignora o cancelamento prenderia a
   execução compartilhada e, com ela, todas as sondas seguintes. A revisão técnica (achado B-03) mostrou que, sem teto, uma
   dependência travada acumulava uma execução presa por ciclo.

Havia também o risco de configuração incoerente: um check mais longo que o prazo global faria o endpoint responder
`Unhealthy` sem dizer qual dependência demorou.

## Decisão
1. **Duas tags, dois endpoints.** `/health/live` executa só a tag `live` (o processo; falha reinicia) e `/health/ready` as
   dependências (tag `ready`; falha tira do balanceamento). `Healthy` e `Degraded` respondem 200; `Unhealthy`, prazo
   estourado ou aplicação encerrando respondem 503 ([`HealthCheckTags.cs`](../../TEC.Observability/HealthChecks/HealthCheckTags.cs),
   [`ApplicationBuilderExtensions.cs`](../../TEC.Observability/DependencyInjection/ApplicationBuilderExtensions.cs)).
2. **Execução única por tag, com cache.** O `HealthReportCache` faz requisições simultâneas compartilharem uma só execução
   (mesmo com o cache desligado) e reaproveita o resultado por `CacheDuration` (padrão 5 s, máximo 1 min). A execução não
   herda o contexto de quem a disparou (`ExecutionContext.SuppressFlow`), e o corpo JSON é serializado uma vez por relatório
   ([`HealthReportCache.cs`](../../TEC.Observability/HealthChecks/HealthReportCache.cs)).
3. **Prazo global com abandono e teto.** `HealthChecks:Timeout` (padrão 30 s) limita a execução inteira; uma verificação que
   ignora o cancelamento é abandonada (`WaitAsync`) e o resultado é `Unhealthy`. Com 3 execuções abandonadas ainda presas na
   tag, nenhuma outra é disparada: responde `Unhealthy` direto, com `Error` no log, até alguma terminar.
4. **Orçamento conferido na subida.** Os checks registrados pela biblioteca informam o tempo máximo (banco e TCP: o `timeout`;
   HTTP: `(Timeout + 1s) × (Retries + 1)`), e a validação recusa quem não cabe em `HealthChecks:Timeout`, dizendo qual
   dependência e qual conta ([`HealthCheckBudgets.cs`](../../TEC.Observability/HealthChecks/HealthCheckBudgets.cs)).
5. **Banco sem drivers e com prazo garantido.** O `DatabaseHealthCheck` recebe uma fábrica de conexão (`DbProviderFactory`,
   `Func<DbConnection>` ou conexão do container): a biblioteca não referencia drivers. A consulta recebe `CommandTimeout` e a
   espera é limitada por `WaitAsync`, mesmo com driver que ignora o cancelamento
   ([`DatabaseHealthCheck.cs`](../../TEC.Observability/HealthChecks/DatabaseHealthCheck.cs)).
6. **Sondagens HTTP isoladas.** SSO (documento de descoberta OIDC com `issuer`, lido até 256 KB) e serviço externo usam um
   `HttpClient` nomeado sem redirecionamento, sem cookies, sem cabeçalhos de trace e sem os loggers padrão do
   `IHttpClientFactory`; 4xx não é repetido, falhas transitórias sim
   ([`HttpEndpointHealthCheck.cs`](../../TEC.Observability/HealthChecks/HttpEndpointHealthCheck.cs)). As sondas e as rotas de
   health não geram trace.
7. **Liveness reaproveitável e à prova de falha.** O check `self` da biblioteca não é registrado se já existe um de mesmo
   nome com a tag `live` (ex.: o do .NET Aspire `ServiceDefaults`); sem a tag, a subida falha com a instrução. O início do
   processo só é lido com `ExposeDetails`, uma vez e sem inicializador estático: se a plataforma não o informar, o liveness
   segue `Healthy`, sem os campos de horário ([`LivenessHealthCheck.cs`](../../TEC.Observability/HealthChecks/LivenessHealthCheck.cs)).

Detalhes em [health-checks.md](../health-checks.md).

## Consequências
### Positivas
- Uma rajada de sondas não vira carga nas dependências, e uma dependência travada não derruba o endpoint nem acumula
  execuções sem limite.
- Queda de uma dependência tira a instância do balanceamento sem reiniciá-la.
- Configuração incoerente de prazo é detectada na subida.
- O mesmo JSON em todos os serviços (ver [ADR 0006](0006-exposicao-minima-dos-endpoints-de-health-e-porta-de-gerencia.md)).

### Neutras / Negativas
- O resultado pode ter até `CacheDuration` de idade, e o horário vem do `TimeProvider` do container.
- Checks registrados direto no `IHealthChecksBuilder` não entram na conferência de prazo da subida: o autor garante que
  terminem antes de `HealthChecks:Timeout`.
- Durante o encerramento da aplicação o readiness responde 503 de propósito.
