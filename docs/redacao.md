[🏠 TEC.Observability](../README.md) › [📚 Documentação](README.md) › 🧹 Redação de dados sensíveis

# 🧹 Redação de dados sensíveis

> O que o componente tira de logs, spans, arquivo de log e JSON de health antes de qualquer exportador — e o que fica sob
> responsabilidade da aplicação.

## 📑 Sumário

- [🎯 Visão geral](#-visão-geral)
- [🔑 Nomes sensíveis](#-nomes-sensíveis)
- [📝 Texto livre](#-texto-livre)
- [🚀 Uso](#-uso)
  - [📜 Logs](#-logs)
  - [🧵 Traces](#-traces)
  - [📄 Arquivo de logs](#-arquivo-de-logs)
  - [❤️ Health checks](#️-health-checks)
- [⚙️ Opções](#️-opções)
- [❌ Erros](#-erros)
- [🛡️ Segurança](#️-segurança)
- [❓ Perguntas frequentes](#-perguntas-frequentes)

---

## 🎯 Visão geral

```mermaid
flowchart LR
    APP["ILogger · ActivitySource<br/>instrumentações"] --> LRP["LogRedactionProcessor<br/><sub>atributos + mensagem + exceção</sub>"]
    APP --> SR["SpanRedactionProcessor<br/><sub>atributos + URLs + cofres</sub>"]
    APP --> EN["Enriquecedores HTTP<br/><sub>url.query · url.full</sub>"]
    LRP & SR & EN --> EXP["Exportadores<br/><sub>Otlp · Console · LogFile · Azure · próprio</sub>"]
    EXP --> LF["LogFile<br/><sub>+ escopos, caracteres de controle, limites</sub>"]
    HC["Health checks"] --> HW["HealthCheckResponseWriter<br/><sub>data sensível, textos cortados</sub>"]
```

A redação é uma **heurística que prefere esconder a vazar**. Decisão de arquitetura: na dúvida, o valor sai como
`Redacted`, mesmo que às vezes isso esconda algo inofensivo.

| Princípio | Como |
|---|---|
| Antes de qualquer exportador | Os processadores são registrados no **início** da coleção de serviços: rodam antes dos exportadores da biblioteca e dos registrados por outra biblioteca (ex.: Aspire `ServiceDefaults` com `Provider: None`) |
| Sem custo quando não há nada | Sem alocação no caminho comum: só a conferência do nome de cada atributo e buscas vetorizadas |
| Sem expressão regular | Varredura linear (custo proporcional ao tamanho do texto, sem risco de *backtracking*) |
| Idempotente | Valor já `Redacted` não é reescrito |

---

## 🔑 Nomes sensíveis

Um atributo (de log, de span, de escopo no arquivo, de `data` no health) é sensível quando o **fim** do nome, ignorando
maiúsculas e os separadores `_`, `-`, `.`, `:` e espaço, é um destes:

| Terminações | Só o nome inteiro |
|---|---|
| `password`, `passwd`, `passphrase`, `secret`, `token`, `apikey`, `accesskey`, `accountkey`, `privatekey`, `connectionstring`, `authorization`, `credential`, `credentials`, `cookie`, `signature` | `pwd`, `sig` |

| Nome | Sensível? |
|---|:---:|
| `DbPassword`, `access_token`, `X-Api-Key`, `http.request.header.authorization`, `db.connection_string`, `enduser.password` | ✅ |
| `TokenCount`, `PasswordPolicy`, `Signed` | ❌ |

Valores `null` e `bool` nunca são redigidos (não carregam segredo).

---

## 📝 Texto livre

Mensagens de log, valores de texto de atributos de log e de span, descrição de status de span, exceções de log,
escopos no arquivo e textos do health passam por três varreduras:

| Varredura | Antes | Depois |
|---|---|---|
| URLs `http`/`https` com query, fragmento ou usuário e senha | `https://user:senha@api/x?token=abc#frag` | `https://api/x?token=Redacted` |
| Pares `chave=valor` / `chave: valor` de chave sensível (connection strings, JSON, cabeçalhos, argumentos) | `Server=db;Password=abc;` · `"apiKey": "xyz"` | `Server=db;Password=Redacted;` · `"apiKey": "Redacted"` |
| Credenciais `Bearer` / `Basic` em qualquer lugar | `Authorization: Bearer eyJhbGci...` | `Authorization: Bearer Redacted` |

Formato da query redigida: o mesmo da instrumentação do OpenTelemetry — `?a=1&b=2` vira `?a=Redacted&b=Redacted`; um
trecho sem `=` pode ser o próprio segredo e vira `Redacted` (`?eyJhbGci...` → `?Redacted`). O fragmento (`#access_token=...`)
é removido.

> [!WARNING]
> **Pode redigir demais.** `status do token: expirado` vira `status do token: Redacted`, porque `token` é uma chave
> sensível seguida de `:`. É intencional: o custo de esconder um valor inofensivo é menor que o de vazar um segredo.

---

## 🚀 Uso

Não há nada a ligar: a redação vale para qualquer `Provider` (inclusive `None` com o pipeline de outra biblioteca).

### 📜 Logs

`LogRedactionProcessor`, no fim de cada registro:

| Item | Regra |
|---|---|
| Atributo de nome sensível | Valor vira `Redacted` |
| Atributo `QueryString` (logs de requisição do ASP.NET Core) | Valores da query redigidos |
| Valor `Uri` absoluto com algo a redigir | URL redigida |
| Valor `string` | Varredura de [texto livre](#-texto-livre) |
| Mensagem formatada | **Refeita a partir do modelo** (`{OriginalFormat}`) com os valores redigidos; sem modelo, cada valor original (≥ 3 caracteres) é trocado pelo redigido. Depois, varrida como texto livre |
| `IHttpClientFactory` no .NET 8 | Os loggers padrão (que punham a URL completa na mensagem **e no escopo** `HTTP {Method} {Uri}`) são trocados por loggers equivalentes com a URL redigida, nas mesmas categorias (`System.Net.Http.HttpClient.{nome}.LogicalHandler`/`.ClientHandler`). Um cliente com logging próprio (`RemoveAllLoggers`/`AddLogger`) mantém o dele |

```csharp
logger.LogInformation("Conectando com {ConnectionString}", "Server=db;User Id=app;Password=abc");
// Atributo ConnectionString = "Redacted" (nome sensível)
// Mensagem: "Conectando com Redacted"

logger.LogWarning("Falha ao chamar {Url}", new Uri("https://api.exemplo.com/v1?api_key=123"));
// Atributo Url = "https://api.exemplo.com/v1?api_key=Redacted"
```

**Exceção do log** (`LogRecord.Exception`), para **todos** os exportadores (`Otlp`, `Azure`, `Console`, `LogFile`,
próprio):

| Situação | O que o exportador recebe |
|---|---|
| Mensagem, stack trace e exceções internas sem nada a redigir | A exceção original, com o tipo original |
| Algo a redigir (ex.: connection string na mensagem de uma exceção interna) | Uma cópia redigida (`RedactedException`, tipo interno): mensagem e `ToString()` redigidos, com o tipo original no início do `ToString()` e em `OriginalTypeName` |

- Só nesse caso o tipo exportado (ex.: `exception.type` no OTLP, tipo da exceção no Application Insights) passa a ser
  `RedactedException`; o nome do tipo original continua no texto. `Exception.Data` **não** é copiado.
- Custo: um `ToString()` a mais por log com exceção.

> [!CAUTION]
> **Escopos (`BeginScope`) não podem ser reescritos** pela API do OpenTelemetry: `Otlp`, `Azure`, `Console` e exportadores
> próprios os recebem **como foram registrados**. Só o `LogFile` os redige ao gravar. Não coloque segredos nem dados
> pessoais em escopos.

### 🧵 Traces

| Onde | Regra |
|---|---|
| Qualquer span (`SpanRedactionProcessor`, no início e no fim) | Atributo de nome sensível vira `Redacted` (ex.: `http.request.header.authorization`, `db.connection_string`); o **valor de texto** de qualquer outro atributo passa pela varredura de [texto livre](#-texto-livre) (`SetTag("erro", "falhou: Password=abc")` → `Password=Redacted`) |
| Descrição do status (`SetStatus(Error, descrição)`) | Redigida como texto livre; o código do status é mantido |
| Spans de cliente (saída) | `url.full`/`http.url` sem valores de query, sem usuário e senha e sem fragmento — inclusive de instrumentações de terceiros |
| Spans de servidor (ASP.NET Core) | `url.query` sempre redigida, mesmo com a redação da instrumentação desligada (a distro do Azure Monitor a desliga em `OTEL_DOTNET_EXPERIMENTAL_*_DISABLE_URL_QUERY_REDACTION`) |
| Spans de cofre do Azure (Key Vault, Managed HSM) | `url.full`/`http.url`, `url.path`, `http.target` e o nome de exibição sem nome e versão (`/secrets/***`); `url.query` removida; nos spans `Azure.Security.KeyVault.*`, `az.keyvault.*` viram `***`. Duração e status ficam |
| Chamadas do `HttpClient` a cofres e sondagens de health | Nem geram span ([traces-metricas-logs.md](traces-metricas-logs.md#-o-que-não-gera-trace)) |

Sem nada a redigir, não há alocação: só a conferência dos nomes e buscas vetorizadas nos textos.

> [!WARNING]
> **Eventos do span não são redigidos**: `RecordException`/`AddException` gravam `exception.message` e
> `exception.stacktrace` num `ActivityEvent`, que é imutável no .NET. Registre a exceção **em log** (a exceção do log é
> redigida) ou use `activity.SetStatus(ActivityStatusCode.Error, descrição)`, cuja descrição é redigida.

### 📄 Arquivo de logs

Além do que já chega redigido pelo processador, o `LogFile`:

| Item | Regra |
|---|---|
| Escopos | Nome sensível → `Redacted`; demais valores → varredura de texto livre |
| Exceção (`ToString()`) | Já redigida pelo processador; o texto gravado passa de novo pela varredura de texto livre |
| Caracteres de controle | Quebras viram nova linha recuada; ANSI e demais controles escapados (`\u001B`) — sem *log forging* |
| Tamanho | Mensagem até 32 K caracteres; exceção e cada escopo até 64 K; registro inteiro até 192 K (escopos excedentes omitidos com `…[truncado]`) |

### ❤️ Health checks

Só se aplica com `HealthChecks:ExposeDetails` ligado (sem detalhes, o JSON traz só status e horário):

| Item | Regra |
|---|---|
| `data` com chave sensível | Valor vira `Redacted` |
| Textos (descrição, mensagem de exceção, valores de texto em `data`) | Cortados em 1024 caracteres (`…`) e varridos como texto livre |
| Descrição igual à mensagem da exceção (check de terceiros que lançou) | Segue a regra de `ExposeExceptionDetails` (oculta por padrão) |
| Stack trace | Nunca |

---

## ⚙️ Opções

A redação não tem opções: não dá para desligá-la nem ampliar a lista de nomes. Para dados que a heurística não reconhece
(CPF, e-mail, texto livre do usuário), não registre — ou mascare antes, na aplicação.

---

## ❌ Erros

| Situação | Causa | O que fazer |
|---|---|---|
| Valor inofensivo aparece como `Redacted` | Nome termina com uma palavra sensível, ou texto com `token:`/`password=` | Renomeie o atributo (ex.: `TokenCount` em vez de `UserToken`) ou reescreva a mensagem |
| Segredo apareceu no backend | Escopo (`BeginScope`), evento de span (`RecordException`) ou formato que a heurística não reconhece | Veja os limites acima; tire o segredo da origem |
| `exception.type` = `RedactedException` no backend | A exceção tinha algo a redigir e foi trocada pela cópia redigida | Normal: o tipo original está no início do texto da exceção |

---

## 🛡️ Segurança

> [!IMPORTANT]
> A redação é uma **rede de segurança**, não um controle de acesso. O controle primário é não registrar segredos nem
> dados pessoais. Testes de fuzzing e de vazamento (`LeakageTests`) injetam um marcador secreto em tudo o que cliente ou
> dependência controlam e conferem que ele não sai em spans, logs exportados, arquivo, JSON e cabeçalhos.

Modelo de ameaças: [seguranca.md](seguranca.md).

---

## ❓ Perguntas frequentes

<details>
<summary>Por que não usar o <code>Microsoft.Extensions.Compliance.Redaction</code>?</summary>

Ele exige classificar cada dado na aplicação. A redação daqui cobre o que vem de bibliotecas e instrumentações que a
aplicação não controla (logs do ASP.NET Core e do `HttpClient`, spans do Azure SDK). Os dois podem conviver.

</details>

<details>
<summary>A redação deixa os logs mais lentos?</summary>

Sem nada a redigir, o custo é a conferência dos nomes e buscas vetorizadas, sem alocação (medido nos
`SensitiveTextBenchmarks` e `RedactionBenchmarks`).

</details>

---
⬅️ [Traces, métricas e logs](traces-metricas-logs.md) · [📚 Índice](README.md) · [Health checks](health-checks.md) ➡️
