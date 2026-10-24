# ADR 0004: Correlation ID Ligado ao Trace W3C e Baggage Restrito por Host

## Status
Aprovado

## Data
2026-10-24

## Contexto
Correlacionar chamadas entre serviços exige um identificador que atravesse a cadeia. Os riscos são:

1. **Injeção:** o `X-Correlation-ID` vem do cliente e vai para log e cabeçalho de resposta (*log forging*, *header
   injection*).
2. **Vazamento para terceiros:** o `baggage` W3C leva o correlation id e qualquer item que o cliente tenha enviado; se for
   propagado a toda chamada de saída, chega a APIs de terceiros.
3. **Propagação paralela:** o `HttpClient` do .NET escreve o Baggage do `Activity` por fora do propagador do OpenTelemetry
   (`DistributedContextPropagator`), então filtrar só um dos dois deixaria o outro vazar.
4. **Sem pipeline de tracing:** com `Provider: None` e nenhum outro assinante, o ASP.NET Core e o `HttpClient` não criam
   `Activity`; sem ela, `traceparent` e `baggage` não seguem e cada serviço gera o seu próprio id (achado B-01 da
   [revisão técnica](../revisao-tecnica.md#b-01--com-provider-none-o-correlation-id-não-se-propaga)).

## Decisão
1. **Validação estrita.** O correlation id é aceito só com até 128 caracteres: letras e dígitos ASCII, `-`, `_`, `.` e `:`.
   Inválido é descartado sem erro. Ordem de resolução: cabeçalho `X-Correlation-ID`, Baggage `correlation.id` do chamador,
   TraceId do `Activity` e, sem `Activity`, um GUID
   ([`CorrelationIdMiddleware.cs`](../../TEC.Observability/Tracing/CorrelationIdMiddleware.cs)).
2. **Ligado ao trace.** O valor vira atributo do span (`correlation.id`), escopo de log, cabeçalho de resposta e item de
   Baggage (do OpenTelemetry e do `Activity`), sem alocar dicionário por requisição.
3. **Baggage de entrada descartado.** Outros itens de `baggage` recebidos são retirados dos dois lugares; só
   `AllowInboundBaggage: true` os mantém (serviços com tráfego exclusivamente interno).
4. **Baggage de saída só para hosts permitidos.** `BaggageAllowedHosts` (padrão vazio: nenhum host) aceita nome exato,
   `*.sufixo` (que não inclui o próprio sufixo) ou `*`. O `traceparent` segue sempre; o serviço chamado usa o TraceId como
   correlation id quando não recebe Baggage.
5. **Filtro nos dois propagadores.** `HostFilteringPropagator` e `HostFilteringDistributedContextPropagator` envolvem os
   propagadores padrão do OpenTelemetry e do .NET e deixam de escrever `baggage` (e `Correlation-Context`) para hosts fora
   da política da requisição atual ([`BaggagePropagation.cs`](../../TEC.Observability/Tracing/BaggagePropagation.cs)).
6. **Propagação mesmo sem exportar.** Com `Provider: None`, um `ContextPropagationListener` mínimo escuta só
   `Microsoft.AspNetCore` e `System.Net.Http` com `PropagationData` (cria `Activity` sem gravar nem exportar)
   ([`ContextPropagationListener.cs`](../../TEC.Observability/Tracing/ContextPropagationListener.cs)).

Detalhes em [correlation-id.md](../correlation-id.md).

## Consequências
### Positivas
- Sem injeção em logs ou cabeçalhos pelo correlation id, e sem vazamento do id nem de Baggage do cliente para terceiros.
- A correlação entre serviços funciona com qualquer `Provider`, inclusive `None`.
- O serviço chamado correlaciona mesmo sem Baggage, pelo TraceId do `traceparent`.

### Neutras / Negativas
- Os propagadores são globais do processo: o filtro vale para todos os hosts e `HttpClient`s, e a política usada fora de uma
  requisição é a do último host que subiu (relevante só com dois hosts no mesmo processo, em geral testes). Um
  `SocketsHttpHandler` criado antes da instalação do filtro guarda o propagador anterior; use `IHttpClientFactory`.
- O correlation id do cliente é sempre confiado em formato (nunca em conteúdo): um cliente pode reutilizar o id de outra
  requisição (achado S-04, Info, não corrigido).
- `BaggageAllowedHosts: ["*"]` anula a proteção e não deve ser usado em serviços que chamam terceiros.
