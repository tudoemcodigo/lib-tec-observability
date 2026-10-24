# ADR 0006: Exposição Mínima dos Endpoints de Health e Porta de Gerência

## Status
Aprovado

## Data
2026-10-24

## Contexto
Os endpoints de health são anônimos por natureza (balanceadores e orquestradores não se autenticam) e, por isso, são um
canal clássico de reconhecimento: nome, versão e ambiente do serviço, o nome de cada dependência, mensagens de exceção
com hosts e usuários de banco e o horário de início do processo ajudam um atacante. Ao mesmo tempo, em desenvolvimento e
em diagnóstico o detalhe é útil.

Restringir por cabeçalho `Host` (`RequireHost`) parece natural, mas o valor do cabeçalho é escolhido pelo cliente: um
`Host: qualquer:8081` enviado pela porta pública passaria.

## Decisão
1. **Resposta mínima por padrão.** O JSON traz só `status` e `timestamp`. A identificação do serviço (`service`, `version`,
   `environment`), o detalhe de cada verificação e o tempo total só saem com `ExposeDetails` ligado
   ([`HealthCheckResponseWriter.cs`](../../TEC.Observability/HealthChecks/HealthCheckResponseWriter.cs)).
2. **`ExposeDetails` decidido pelo ambiente.** Vazio (padrão) liga em `Development` e desliga nos demais. Fora de
   `Development`, `true` só é aceito com `ManagementPort` definida; a combinação inválida derruba a subida
   ([`ObservabilityOptionsValidator.cs`](../../TEC.Observability/Configuration/ObservabilityOptionsValidator.cs),
   [`ObservabilityOptionsSetup.cs`](../../TEC.Observability/Configuration/ObservabilityOptionsSetup.cs)).
3. **Porta de gerência conferida na conexão.** Com `ManagementPort`, os endpoints respondem 404 (sem executar verificação
   alguma) em conexões recebidas por outra porta; a conferência usa `HttpContext.Connection.LocalPort`, não o `Host`
   ([`ApplicationBuilderExtensions.cs`](../../TEC.Observability/DependencyInjection/ApplicationBuilderExtensions.cs)).
   `AllowedHosts` continua existindo, mas é declarado **só roteamento, não segurança**.
4. **Exceção sob outro opt-in.** A mensagem da exceção só entra no JSON com `ExposeExceptionDetails` (padrão `false`); o stack
   trace nunca é exposto (a falha completa vai para o log). Uma descrição igual à mensagem da exceção de um check de
   terceiros segue a mesma regra.
5. **Dados e textos limpos.** Valores de `data` com chave sensível saem `Redacted`; textos são cortados em 1024 caracteres e
   passam pela redação de texto livre (ver [ADR 0003](0003-redacao-de-dados-sensiveis-antes-de-qualquer-exportador.md)).
6. **Liveness sem dados de processo.** `startedAt` e `uptimeSeconds` só entram com detalhes ligados
   ([`LivenessHealthCheck.cs`](../../TEC.Observability/HealthChecks/LivenessHealthCheck.cs)).
7. **Resposta endurecida.** Só `GET`, `AllowAnonymous`, fora do OpenAPI, com `Cache-Control: no-store, no-cache`,
   `Pragma: no-cache`, `Expires` no passado e `X-Content-Type-Options: nosniff`.

Detalhes em [health-checks.md](../health-checks.md#️-segurança) e [seguranca.md](../seguranca.md).

## Consequências
### Positivas
- Um cliente da porta pública só vê o estado agregado (e o código HTTP), sem nome de dependência, versão ou exceção.
- Não há como forjar o acesso ao detalhe pelo cabeçalho `Host`.
- Configuração insegura é recusada na subida, não descoberta em auditoria.

### Neutras / Negativas
- Obter o detalhe em produção exige que o servidor escute em duas portas (ex.: `ASPNETCORE_HTTP_PORTS=8080;8081`) e que a
  porta de gerência não seja publicada no balanceador. Atrás de um proxy que termina a conexão no próprio contêiner, a
  porta local é a que o proxy usa para chegar à aplicação.
- Quem monta um endpoint próprio com `HealthCheckResponseWriter.WriteAsync` não ganha o cache nem a execução única de
  [ADR 0005](0005-health-checks-liveness-readiness-com-execucao-unica-cache-e-prazo.md), mas mantém o mesmo JSON.
