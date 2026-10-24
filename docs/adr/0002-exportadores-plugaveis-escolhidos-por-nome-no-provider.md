# ADR 0002: Exportadores Plugáveis Escolhidos por Nome em Observability:Provider

## Status
Aprovado

## Data
2026-10-24

## Contexto
Cada ambiente exporta a telemetria para um destino diferente: console no desenvolvimento, coletor OTLP (Grafana, Datadog,
New Relic, Dynatrace, AWS ADOT, GCP, Jaeger) ou Azure Monitor em produção, e às vezes uma cópia local dos logs. Sem um
ponto de extensão único, trocar de backend exigiria mudar código e dependências, e cada serviço montaria o pipeline do
OpenTelemetry do seu jeito (recurso, filtros, amostragem e redação diferentes).

Além disso:

1. Um nome de destino digitado errado, ou um destino sem o exportador correspondente instalado, só apareceria como
   telemetria ausente em produção.
2. Cabeçalhos OTLP costumam carregar a chave de API do backend; enviá-los por `http` para outra máquina vaza a chave.
3. O `UseOtlpExporter` do OpenTelemetry não aceita `Headers` definidos em código, e o SDK lê variáveis `OTEL_EXPORTER_OTLP_*`
   (gerais e por sinal) que precisam continuar valendo (por exemplo, as definidas pelo .NET Aspire).

## Decisão
1. **Um contrato, vários destinos.** Todo destino é um `ITelemetryExporter` (`Name` + `Configure(TelemetryExporterContext)`).
   O `Provider` aceita um ou vários nomes separados por vírgula ou ponto e vírgula (`"Otlp, LogFile"`), sem diferenciar
   maiúsculas, sem repetição e sem importar a ordem. `None` desliga a telemetria e não pode ser combinado
   ([`ITelemetryExporter.cs`](../../TEC.Observability/Exporters/ITelemetryExporter.cs),
   [`ObservabilityOptions.cs`](../../TEC.Observability/Configuration/ObservabilityOptions.cs)).
2. **Embutidos e registrados.** `None`, `Otlp`, `Console` e `LogFile` vêm no pacote base e são ligados dentro do
   `AddTecObservability`. Qualquer outro nome (como `Azure`) é registrado com `ObservabilityBuilder.AddExporter` e só é ligado
   se estiver no `Provider`; registrar um exportador que não está na lista não tem efeito
   ([`ObservabilityBuilder.cs`](../../TEC.Observability/DependencyInjection/ObservabilityBuilder.cs),
   [`TelemetryExporterStrategies.cs`](../../TEC.Observability/Exporters/TelemetryExporterStrategies.cs)).
3. **Pipeline comum.** Todos os exportadores ligados compartilham o mesmo `OpenTelemetryBuilder` (recurso, redação, filtros,
   fontes `TEC.*`), e a instrumentação padrão de ASP.NET Core e HttpClient com `ParentBased(TraceIdRatio(SamplingRatio))` é
   registrada uma única vez (`AddStandardInstrumentation`).
4. **Falha na subida, não em produção.** Um nome sem exportador registrado, um `Provider` alterado depois do registro e nomes
   inválidos ou repetidos derrubam a inicialização com a instrução do que instalar ou chamar
   ([`ObservabilityOptionsValidator.cs`](../../TEC.Observability/Configuration/ObservabilityOptionsValidator.cs)).
5. **OTLP por sinal.** O exportador OTLP usa `AddOtlpExporter` em traces, métricas e logs, para aceitar `Headers` vindos da
   configuração; sem `Otlp:Endpoint` (nem `Otlp:Headers`) valem as variáveis `OTEL_EXPORTER_OTLP_*`, inclusive as por sinal.
6. **Cabeçalhos protegidos.** Nome de cabeçalho restrito a letras, dígitos, `-` e `_`, valor sem vírgula nem quebra de
   linha, e `Headers` só com `https` ou endereço local, salvo `AllowInsecureTransport` explícito. A regra vale para o par
   endpoint/cabeçalhos efetivo de cada sinal, inclusive o vindo das variáveis do OpenTelemetry.
7. **Arquivo de logs como destino local.** O `LogFile` grava só logs, em lote, fora da thread da aplicação, com limites de
   tamanho e escape de caracteres de controle, para servir de cópia local junto com outro destino.

Detalhes de uso em [exportadores.md](../exportadores.md).

## Consequências
### Positivas
- Trocar de backend, ou ligar um segundo destino, é só configuração; o `Program.cs` é o mesmo em todos os ambientes.
- Configuração errada (destino sem exportador, chave em `http`, cabeçalho malformado) é recusada na subida com mensagem
  acionável.
- O exportador próprio recebe a telemetria já redigida e o pipeline comum, sem reimplementar nada.

### Neutras / Negativas
- Com vários destinos, a instrumentação padrão e o sampler são compartilhados: o que um exportador decide vale para todos
  (por exemplo, `Azure` impõe o sampler da distro).
- O exportador `Console` é síncrono e serve apenas ao desenvolvimento.
- Segredos rotacionados em `Otlp:Headers` só valem depois de reiniciar, porque o exportador é criado uma vez.
