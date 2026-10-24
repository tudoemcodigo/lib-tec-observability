# ADR 0007: Configuração Validada na Subida e Lida na Criação dos Componentes

## Status
Aprovado

## Data
2026-10-24

## Contexto
A observabilidade costuma ser configurada por muitas fontes: `appsettings.json`, variáveis de ambiente, ajustes em código e,
cada vez mais, um cofre de segredos (TEC.Vault) adicionado ao `IConfiguration` **depois** do registro dos serviços. Três
problemas aparecem:

1. **Erro tardio.** Uma configuração quebrada (`Provider` sem exportador, endpoint inválido, cabeçalho OTLP em `http`, health
   check mais longo que o prazo) só se manifestaria na primeira requisição, ou como telemetria silenciosamente ausente.
2. **Segredos tardios.** `Otlp:Headers` e `Azure:ConnectionString` são segredos que não devem ficar no repositório; se as
   opções fossem lidas no registro, um provedor de configuração adicionado depois não valeria.
3. **Decisão estrutural cedo demais.** O `Provider` define quais exportadores entram no pipeline do OpenTelemetry, que é
   montado dentro do `AddTecObservability`.

## Decisão
1. **Opções no padrão do .NET.** `ObservabilityOptions` (seção `Observability`) é registrada com `IOptions`,
   `IOptionsMonitor` e `IOptionsSnapshot`, com a ordem: configuração (`appsettings`/variáveis), parâmetro `configure`,
   `Configure<ObservabilityOptions>`, e então os padrões (`ServiceName` de `OTEL_SERVICE_NAME`, `ServiceVersion` do assembly de
   entrada, `ExposeDetails` pelo ambiente)
   ([`ObservabilityOptionsSetup.cs`](../../TEC.Observability/Configuration/ObservabilityOptionsSetup.cs),
   [`ServiceCollectionExtensions.cs`](../../TEC.Observability/DependencyInjection/ServiceCollectionExtensions.cs)).
   `IgnoredPaths` definido no `appsettings.json` **substitui** a lista padrão, em vez de ser acrescentado a ela.
2. **Validação completa na subida.** `ValidateOnStart` com um `IValidateOptions` que acumula todos os problemas numa única
   mensagem (*Configuração inválida na seção 'Observability': ...*), sem repetir o valor recebido, para endpoint com
   credencial, cabeçalho ou nome de arquivo não vazarem pela mensagem
   ([`ObservabilityOptionsValidator.cs`](../../TEC.Observability/Configuration/ObservabilityOptionsValidator.cs)).
3. **Leitura na criação.** Exportadores, recurso, amostragem, health checks e endpoints leem `IOptions<ObservabilityOptions>`
   quando são criados (na construção do provedor de telemetria), não no registro; assim um provedor de configuração
   adicionado depois do `AddTecObservability` vale para cabeçalhos OTLP, connection string do Azure, nome, versão e
   ambiente.
4. **Exceção: o `Provider`.** Uma foto da configuração disponível no registro decide só o pipeline (quais exportadores
   ligar). Se um provedor tardio mudar os destinos, a subida falha explicando a ordem
   (*Provider mudou depois do AddTecObservability*).
5. **Uma chamada só.** `AddTecObservability` lança `InvalidOperationException` na segunda chamada; os métodos de pipeline
   (`UseTecObservability`, `UseCorrelationId`, `MapTecHealthChecks`) falham com instrução se o registro não foi feito.
6. **Binding sem reflexão.** `EnableConfigurationBindingGenerator` gera o binding em tempo de compilação, mantendo o pacote
   base compatível com trimming e Native AOT.

Detalhes em [configuracao.md](../configuracao.md).

## Consequências
### Positivas
- O serviço não sobe com telemetria quebrada ou insegura; todos os problemas aparecem de uma vez.
- Segredos de telemetria podem vir de um cofre sem mudar o registro.
- Mensagens de validação não repetem valores sensíveis (há teste de vazamento para isso).

### Neutras / Negativas
- O `Provider` não pode vir de um cofre nem de `services.Configure` registrado depois: precisa estar no `appsettings.json`,
  em variável de ambiente ou no parâmetro `configure`.
- Um segredo rotacionado aparece em `IOptionsMonitor`, mas o exportador (criado uma vez) só o usa depois de reiniciar.
- Num processo com vários hosts, o estado global dos propagadores (ver [ADR 0004](0004-correlation-id-no-trace-w3c-e-baggage-restrito-por-host.md))
  faz valer a política do último host que subiu fora de uma requisição.
