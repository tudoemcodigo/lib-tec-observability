# ADR 0003: Redação de Dados Sensíveis Antes de Qualquer Exportador

## Status
Aprovado

## Data
2026-10-24

## Contexto
Telemetria é um canal de vazamento comum. Bibliotecas e instrumentações que a aplicação não controla gravam, sem pedir:

1. **URLs com segredo:** a query string de uma requisição (`?token=...`), credenciais em `https://usuario:senha@host`, e, no
   .NET 8, a URL completa nos logs padrão do `IHttpClientFactory`.
2. **Connection strings e cabeçalhos** em mensagens de exceção e em atributos (`db.connection_string`,
   `http.request.header.authorization`).
3. **Nome e versão de segredos** nos caminhos das chamadas a cofres (Azure Key Vault, AWS, GCP, HashiCorp Vault, Infisical).
4. **DTOs e `record`s** com senha, gravados em atributos estruturados pelo `ToString()`.

A distro do Azure Monitor ainda desliga a redação de query string da própria instrumentação. A revisão técnica de
2026-10-19 ([S-01, S-02, S-03](../revisao-tecnica.md#-resumo-dos-achados)) mostrou que objetos, arrays, plurais
(`Cookies`, `Secrets`), cookies múltiplos e provedores de log fora do OpenTelemetry escapavam de uma primeira versão da
redação.

## Decisão
1. **Heurística que prefere esconder a vazar.** Na dúvida, o valor sai como `Redacted`, mesmo que isso esconda algo
   inofensivo (`token: expirado` vira `token: Redacted`). Não há opção para desligá-la nem para ampliar a lista de nomes.
2. **Antes de qualquer exportador.** Os processadores de redação (`LogRedactionProcessor`, `SpanRedactionProcessor`) e os
   enriquecedores HTTP são registrados no **início** da coleção de serviços (`RegisterFirst`), para rodar antes dos
   exportadores da biblioteca e dos registrados por outra biblioteca antes do `AddTecObservability`
   ([`ServiceCollectionExtensions.cs`](../../TEC.Observability/DependencyInjection/ServiceCollectionExtensions.cs)).
3. **Nomes sensíveis pelo fim do nome.** Atributo cujo nome termina em `password`, `token`, `apikey`, `connectionstring`,
   `authorization`, `cookie`, `cpf`, `cvv` etc. (singular ou plural com `s`, ignorando maiúsculas e separadores) tem o
   valor trocado; `TokenCount` e `PasswordPolicy` ficam
   ([`SensitiveData.cs`](../../TEC.Observability/Internal/SensitiveData.cs)).
4. **Texto livre sem expressão regular.** Varredura linear de URLs `http`/`https` com query, fragmento ou credenciais,
   pares `chave=valor`/`chave: valor` sensíveis, `Cookie`/`Set-Cookie` até o fim da linha e credenciais `Bearer`/`Basic`:
   custo proporcional ao texto, sem risco de *backtracking* e sem alocação quando não há nada a redigir.
5. **Objetos e coleções sem reflexão.** O texto de `ToString()` (o mesmo que os exportadores gravam) passa pela varredura;
   com algo a redigir, o atributo vira o texto redigido (ou um `string[]` redigido). Seguro para trimming e Native AOT.
6. **Exceções.** Com dado sensível na mensagem, no stack trace ou em exceção interna, a exceção do log é trocada por uma
   `RedactedException`, com o tipo original no texto e em `OriginalTypeName`; sem nada a redigir, a original segue
   ([`RedactedException.cs`](../../TEC.Observability/Internal/RedactedException.cs),
   [`LogRedaction.cs`](../../TEC.Observability/Tracing/LogRedaction.cs)).
7. **Spans de cofres de segredos.** Chamadas do `HttpClient` a cofres não geram span; nos spans do Azure SDK, caminho, nome de
   exibição e atributos `az.keyvault.*` perdem nome e versão do segredo
   ([`TelemetryRedaction.cs`](../../TEC.Observability/Tracing/TelemetryRedaction.cs)).
8. **Provedores de log fora do OpenTelemetry sob opt-in.** `RedactAllLoggerProviders` (padrão `false`) envolve o
   `ILoggerFactory` para entregar mensagem formatada e exceção redigidas a todos os provedores (console, Debug, EventLog...).
9. **Limites assumidos e documentados:** escopos de log (`BeginScope`) e eventos de span (`RecordException`) não podem ser
   reescritos pela API do OpenTelemetry/`Activity`; só o `LogFile` redige os escopos ao gravar.

Detalhes em [redacao.md](../redacao.md).

## Consequências
### Positivas
- Token, senha, connection string e nome de segredo não chegam ao backend pelos caminhos mais comuns, seja qual for o
  `Provider` e mesmo com o pipeline montado por outra biblioteca (.NET Aspire).
- Sem custo de alocação no caminho comum e sem risco de DoS por expressão regular.
- A mesma regra serve a logs, spans, arquivo e JSON de health.

### Neutras / Negativas
- Falsos positivos são aceitos de propósito; quem precisa do valor deve renomear o atributo.
- A redação é rede de segurança, não controle de acesso: CPF em texto livre, e-mail e segredos em escopos ou em eventos de
  span não são cobertos. A aplicação continua responsável por não registrar segredos.
- Com `RedactAllLoggerProviders`, cada log paga uma *closure* e a varredura do texto; a opção não vale se outra biblioteca
  substituir o `ILoggerFactory` depois (ex.: `UseSerilog`).
- Uma exceção redigida aparece no backend como `RedactedException`.
