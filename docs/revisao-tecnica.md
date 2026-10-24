[🏠 TEC.Observability](../README.md) › [📚 Documentação](README.md) › 🔎 Revisão técnica

# 🔎 Revisão técnica

> Revisão de segurança, correção, implementação, organização e documentação dos pacotes **TEC.Observability** e
> **TEC.Observability.Azure**, feita sobre o código-fonte. Cada achado cita `arquivo:linha` conferido no fonte; os marcados
> com 🧪 foram reproduzidos num programa à parte (fora do repositório).

> [!NOTE]
> **Documento histórico (2026-10-19).** Números de linha, contagens de testes (323 e 363 por alvo na época; hoje 371 no
> `TEC.Observability.Tests`) e o estado dos achados são os da data da revisão. Para o comportamento atual, vale o código e
> os demais documentos; as decisões que vieram dos achados estão nos [ADRs](adr/README.md).

## 📑 Sumário

- [📋 Escopo](#-escopo)
- [🏗️ Build e testes](#️-build-e-testes)
- [📊 Resumo dos achados](#-resumo-dos-achados)
- [🛡️ Segurança](#️-segurança)
- [🐞 Bugs](#-bugs)
- [⚙️ Implementação e desempenho](#️-implementação-e-desempenho)
- [🗂️ Organização](#️-organização)
- [🩹 Correções aplicadas — 2026-10-19](#-correções-aplicadas--2026-10-19)
- [✅ Pontos fortes](#-pontos-fortes)
- [🚀 Melhorias sugeridas](#-melhorias-sugeridas)
- [📝 Ajustes na documentação](#-ajustes-na-documentação)

---

## 📋 Escopo

| Item | Valor |
|---|---|
| Data | 2026-10-19 |
| Commit analisado | `c1e8ba8 CHANGELOG: 0.0.1 publicada em 2026-10-08 (#3)` (árvore com `.gitignore` modificado localmente, fora do escopo) |
| Pacotes | `TEC.Observability` (OpenTelemetry, OTLP, console, arquivo, health checks, correlation id, redação) e `TEC.Observability.Azure` (distro do Azure Monitor) |
| Também lidos | testes, benchmarks, samples, `build/`, workflows, `docs/`, e a API do TEC.Vault citada em [integracoes-tec.md](integracoes-tec.md) |
| Fora do escopo | `tec-workflows` (só a forma de referência), testes de integração e de carga |

**Os "~19 arquivos `.txt`"** não são `PublicAPI.*.txt`: são as 19 capturas de saída do Test Explorer do Visual Studio em
`.vs/TEC.Observability.slnx/v18/TestStore/OutputMessages/*/StandardOutput.txt`, ignoradas pelo git (`.gitignore:41`,
`.vs/`). Nenhuma traz segredo nem URL com query string. O repositório **não** tem arquivos de API pública (ver
[O-01](#o-01--sem-baseline-de-api-pública)).

---

## 🏗️ Build e testes

| Comando | Resultado |
|---|---|
| `dotnet build TEC.Observability.slnx -c Release` | ❌ Restore falhou: `NU1301` (401) ao buscar `TEC.Vault.AzureKeyVault` no feed `tec-interno` — token do GitHub Packages ausente/expirado na máquina. Ambiente, não código |
| `dotnet build TEC.Observability.slnx -c Release -p:TecUseLocalProjects=true` | ✅ 0 avisos, 0 erros (com `TreatWarningsAsErrors`, analisadores de AOT/trimming e `AnalysisModeSecurity=All` nos pacotes) |
| `TEC.Observability.Tests` (net10.0) | ✅ 323/323 aprovados (44 s) |
| `TEC.Observability.Azure.Tests` sem `Category=Integracao` (net10.0) | ✅ 29/29 aprovados (5 s) |
| Mesmos testes em net8.0 | ⚠️ Não executados: runtime `Microsoft.AspNetCore.App 8.0` não instalado na máquina |

Integração (Application Insights real), carga e benchmarks não foram executados.

---

## 📊 Resumo dos achados

| Severidade | Quantidade |
|---|:-:|
| Crítica | 0 |
| Alta | 0 |
| Média | 3 |
| Baixa | 6 |
| Info | 5 |

| ID | Severidade | Categoria | Local | Resumo | Status |
|---|---|---|---|---|---|
| [S-01](#s-01--objetos-e-arrays-escapam-da-redação) | Média | Segurança | `Tracing/LogRedaction.cs:104-144`, `Tracing/TelemetryRedaction.cs:270` | Valores não-`string` (records, objetos anônimos, arrays) em atributos de log e arrays em spans não passam pela redação 🧪 | ✅ Corrigido |
| [S-02](#s-02--redação-só-vale-para-o-pipeline-do-opentelemetry) | Média | Segurança / Docs | `DependencyInjection/ServiceCollectionExtensions.cs:232` | Provedores de log nativos (console/stdout, Debug, EventLog) recebem o texto original; a doc diz que "vale para qualquer Provider" | ✅ Corrigido (opção `RedactAllLoggerProviders` + doc) |
| [B-01](#b-01--com-provider-none-o-correlation-id-não-se-propaga) | Média | Bug / Docs | `Tracing/CorrelationIdMiddleware.cs:61,102` | Com `Provider: None` (sem outro pipeline), nem `traceparent` nem `baggage` saem nas chamadas HTTP 🧪 | ✅ Corrigido |
| [S-03](#s-03--lacunas-da-heurística-de-texto-livre-e-de-nomes) | Baixa | Segurança | `Internal/SensitiveData.cs:16-23,229-250` | Plurais (`Cookies`, `Secrets`), segundo cookie de `Cookie:` e aspas escapadas em JSON vazam 🧪 | ✅ Corrigido |
| [B-02](#b-02--validação-do-otlp-ignora-variáveis-por-sinal) | Baixa | Bug | `Configuration/ObservabilityOptionsValidator.cs:110-143` | `OTEL_EXPORTER_OTLP_<SINAL>_*` ignoradas: falso "Endpoint obrigatório" e trava de TLS contornável | ✅ Corrigido |
| [B-03](#b-03--execuções-de-health-abandonadas-sem-teto) | Baixa | Bug / Robustez | `HealthChecks/HealthReportCache.cs:71-84` | Check que ignora cancelamento acumula uma execução presa a cada ciclo | ✅ Corrigido |
| [P-01](#p-01--varredura-de-url-aloca-mesmo-sem-nada-a-redigir) | Baixa | Desempenho / Docs | `Internal/SensitiveData.cs:141`, `Tracing/TelemetryRedaction.cs:180-182` | `Substring` + `Uri` por URL, duas vezes por span, mesmo sem nada a redigir (a doc diz "sem alocação") | ✅ Corrigido |
| [P-02](#p-02--minimumlevel-do-arquivo-filtra-depois-da-fila) | Baixa | Desempenho | `Exporters/LogFileTelemetryExporter.cs:27-32,105` | Registros abaixo de `LogFile:MinimumLevel` ocupam a fila do lote e podem empurrar avisos para o descarte | ✅ Corrigido |
| [O-01](#o-01--sem-baseline-de-api-pública) | Baixa | Organização | `build/Tec.Build.props:78` | Sem `PublicAPI.*.txt` nem `PackageValidationBaselineVersion`: quebra de API pública não é detectada | ❌ Não corrigido (bloqueado) |
| [S-04](#s-04--correlation-id-é-sempre-confiado) | Info | Segurança | `Tracing/CorrelationIdMiddleware.cs:91-103` | `X-Correlation-ID` e `correlation.id` do `baggage` do cliente são aceitos mesmo com `AllowInboundBaggage: false` | ➖ Não corrigido (Info) |
| [S-05](#s-05--endpoints-sem-tls-aceitos-sem-cabeçalhos) | Info | Segurança | `ObservabilityOptionsValidator.cs:138`, `AzureMonitorExporterOptions.cs:51-54` | `http` remoto aceito no OTLP sem cabeçalhos e no `IngestionEndpoint` do Azure | ➖ Não corrigido (Info) |
| [O-02](#o-02--projeto-de-testes-do-azure-acoplado-ao-de-testes-base) | Info | Organização | `TEC.Observability.Azure.Tests/*.cs`, `.csproj:37` | Namespace `TEC.Observability.Tests` e leitura do `appsettings.Local.json` de outro projeto | ➖ Não corrigido (Info) |
| [O-03](#o-03--workflows-reutilizáveis-por-tag) | Info | Organização / CI | `.github/workflows/ci.yml:28` | `tec-workflows/...@v2` (tag móvel); a doc fala em "Actions fixadas por SHA" | ✅ Decisão documentada |
| [O-04](#o-04--ambiente-local-de-build) | Info | Organização | `global.json`, `nuget.config` | Restore padrão exige token do feed; net8 sem runtime local não roda os testes | ✅ Documentado |

---

## 🛡️ Segurança

### S-01 — Objetos e arrays escapam da redação

**Problema.** O `LogRedactionProcessor` só redige atributos `string`, `Uri` e os de **nome** sensível. Qualquer outro tipo
(record, objeto anônimo, classe com `ToString`, `string[]`, `List<string>`) segue como veio; os exportadores OTLP, Azure e
console serializam esses valores com `ToString()` (ou item a item, no caso de arrays). No `SpanRedactionProcessor`, só
valores `string` passam pela varredura de texto livre: arrays de string (tipo nativo de atributo no OpenTelemetry) não.

**Evidência.**

```csharp
// Tracing/LogRedaction.cs:134-143
case string text:
    var redactedText = SensitiveText.Redact(text);
    ...
default:
    return false;   // record, objeto anônimo, string[], List<string>...

// Tracing/TelemetryRedaction.cs:270
else if (tag.Value is string text && Internal.SensitiveText.Redact(text) is var redacted && ...)
```

🧪 Reproduzido com `Provider: None` + exportador em memória:

```text
logger.LogInformation("Login {Request}", new LoginRequest("ana", "S3cr3t-Pass"));
  ATTR Request = LoginRequest { User = ana, Password = S3cr3t-Pass }
activity.SetTag("args", new[] { "--password=S3cr3t-SpanArr" });
  SPAN args = --password=S3cr3t-SpanArr
```

A **mensagem formatada** sai redigida (é refeita e varrida como texto), mas o **atributo** estruturado vai inteiro.

**Impacto.** Registrar um DTO (`record` é o padrão de request/command no .NET moderno) leva senha, token ou CPF ao
backend em claro, no campo estruturado — justamente o que costuma ser indexado e consultável. O cenário é comum em
handlers de CQRS (`logger.LogInformation("Comando {Comando}", comando)`).

**Sugestão.** Para valores que não são primitivos nem `string`/`Uri`: converter para texto (`Convert.ToString`), varrer, e
substituir o valor pelo texto redigido quando mudar (o tipo já se perderia na serialização). Para `IEnumerable<string>`
(logs) e `string[]` (spans), varrer cada item e trocar o array quando algum item mudar.

```csharp
case IEnumerable<string> items:
    string[]? copy = null; var i = 0;
    foreach (var item in items)
    {
        var safe = item is null ? null : SensitiveText.Redact(item);
        if (!ReferenceEquals(safe, item)) copy ??= [.. items];
        if (copy is not null) copy[i] = safe!;
        i++;
    }
    newValue = copy; return copy is not null;
case not (string or Uri) when value.GetType() is { IsPrimitive: false, IsEnum: false } && value is not (decimal or DateTime or DateTimeOffset or TimeSpan or Guid):
    var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    var redacted = SensitiveText.Redact(text);
    newValue = redacted; return !ReferenceEquals(redacted, text);
```

Acrescentar casos em `LeakageTests` com `record` e `string[]`.

### S-02 — Redação só vale para o pipeline do OpenTelemetry

**Problema.** O processador de redação é registrado só no `LoggerProvider` do OpenTelemetry. Os provedores nativos do
`ILogger` que o `WebApplication.CreateBuilder` liga por padrão (Console, Debug, EventSource e, no Windows, EventLog), além
de qualquer outro (`AzureAppServices`, Serilog via `ILoggerProvider`), recebem a mensagem, o estado e a exceção
**originais**.

**Evidência.**

```csharp
// DependencyInjection/ServiceCollectionExtensions.cs:232
RegisterFirst(services, s => s.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddProcessor(new LogRedactionProcessor())));
```

[redacao.md](redacao.md#-uso) dizia "Não há nada a ligar: a redação vale para qualquer `Provider`", sem a ressalva.

**Impacto.** Em contêiner, o stdout é coletado pelo orquestrador (Container Insights, Loki, CloudWatch) e costuma ficar
retido por mais tempo e com acesso mais amplo que o backend de telemetria. Um `Password=` numa exceção de banco, um token
numa URL logada pela aplicação ou o log `Request starting ... ?token=` (quando `Microsoft.AspNetCore.Hosting` está em
`Information`) saem sem redação no console. Quem lê a doc atual conclui que está protegido.

**Sugestão.**

1. Documentar a fronteira (feito nesta revisão em [redacao.md](redacao.md#-uso)) e recomendar em
   [seguranca.md](seguranca.md#-configuração-recomendada-para-produção) `"Logging": { "Console": { "LogLevel": { "Default": "None" } } }`
   (ou `ClearProviders()`) fora de `Development` quando o OpenTelemetry é o canal de logs.
2. Oferecer opção explícita, ex. `Observability:Logging:RedactAllProviders`, que envolve os `ILoggerProvider` registrados
   num decorador que aplica `SensitiveText.Redact` na mensagem formatada e `RedactedException` na exceção:

```csharp
internal sealed class RedactingLoggerProvider(ILoggerProvider inner) : ILoggerProvider
{
    public ILogger CreateLogger(string category) => new RedactingLogger(inner.CreateLogger(category));
    public void Dispose() => inner.Dispose();
}
// RedactingLogger.Log: formatter = (s, e) => SensitiveText.Redact(original(s, e)); exception = RedactedException.RedactIfNeeded(ex)
```

### S-03 — Lacunas da heurística de texto livre e de nomes

**Problema.** Três formatos comuns escapam:

| Caso | Evidência | Resultado 🧪 |
|---|---|---|
| Nomes no plural | `Internal/SensitiveData.cs:16-20` (`cookie`, `secret`, `token`… só no singular; `credentials` é a exceção) | `{Cookies}` = `S3cr3t-Plural`; span `Secrets` = `S3cr3t-SpanPlural`; `.AspNetCore.Cookies` idem |
| Vários cookies em `Cookie:` | `ValueAfter` para no primeiro `;` (`SensitiveData.cs:247`) | `Cookie: Redacted; .AspNetCore.Session=S3cr3t-Cookie` |
| Aspas escapadas em JSON | `ValueAfter` fecha na primeira aspa, sem tratar `\"` (`SensitiveData.cs:236-240`) | `{"password":"Redacted"S3cr3t-Esc"}` |

**Impacto.** Baixo: a doc já trata a redação como rede de segurança heurística, e cabeçalhos `Cookie`/`Authorization`
como **atributo** (`http.request.header.cookie`) são redigidos pelo nome. O risco é o texto livre (logs da aplicação,
mensagens de exceção) com cookies de sessão.

**Sugestão.** Aceitar `s` final opcional na comparação de sufixos (`cookies`, `secrets`, `tokens`, `passwords`,
`apikeys`); para chave `cookie`/`set-cookie`, redigir até o fim da linha; em `ValueAfter` entre aspas, pular `\"`.
Considerar também `sessionid`, `session`, `jwt`, `otp`, `pin` e `subscriptionkey` (`Ocp-Apim-Subscription-Key`).

### S-04 — Correlation id é sempre confiado

**Problema.** O `X-Correlation-ID` do cliente é aceito (só validado em formato) e o `correlation.id` do `baggage` é lido
**antes** do descarte do Baggage de entrada, então vale mesmo com `AllowInboundBaggage: false`.

**Evidência.** `Tracing/CorrelationIdMiddleware.cs:53-56` (resolve antes de `DiscardInboundBaggage`) e `:98-100`.

**Impacto.** Nenhuma injeção (a lista de caracteres é restrita e o tamanho limitado a 128 — ok para log forging e
header injection). Um cliente pode, porém, reutilizar o id de outra requisição e misturar investigações. Como o cabeçalho
já é controlado pelo cliente, ler o `baggage` não amplia o risco.

**Sugestão.** Opção `TrustInboundCorrelationId` (padrão `true`, para não quebrar) que, desligada, ignora cabeçalho e
`baggage` e usa sempre o TraceId — para serviços de borda.

### S-05 — Endpoints sem TLS aceitos sem cabeçalhos

**Problema.** A trava de TLS do OTLP só existe quando há cabeçalhos (`ObservabilityOptionsValidator.cs:138`); um coletor
remoto em `http` recebe a telemetria (já redigida) em claro. No Azure, a connection string não é conferida
(`AzureMonitorExporterOptions.cs:51-54`): um `IngestionEndpoint=http://...` seria aceito.

**Impacto.** Informativo: a telemetria é redigida e a chave de instrumentação do Azure não é segredo forte. Vale um aviso
de log na subida para `http` fora de loopback.

---

## 🐞 Bugs

### B-01 — Com Provider None o correlation id não se propaga

**Problema.** Com `Provider: None` e nenhum outro pipeline de tracing no container (sem Aspire `ServiceDefaults`), não há
assinante de `ActivitySource`: o ASP.NET Core não cria `Activity`, o `HttpClient` não injeta `traceparent`, e o
`correlation.id` gravado só no Baggage do **OpenTelemetry** não é escrito por ninguém (o `DiagnosticsHandler` do .NET
propaga o Baggage do `Activity`, não o do OpenTelemetry).

**Evidência.**

```csharp
// Tracing/CorrelationIdMiddleware.cs:61 — só o Baggage do OpenTelemetry
Baggage.SetBaggage(CorrelationId.AttributeName, correlationId);
// :102 — sem Activity, o fallback é um GUID novo por requisição
return Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
```

🧪 Mesma API, `BaggageAllowedHosts: ["127.0.0.1"]`, `X-Correlation-ID: pedido-42`, chamada de saída por
`IHttpClientFactory` para um endpoint que devolve os cabeçalhos recebidos:

```text
None:    traceparent=                       baggage=
LogFile: traceparent=00-9ddb...-01          baggage=correlation.id=pedido-42
```

**Impacto.** A doc e o XML de `ObservabilityProvider.None` dizem que "health checks e correlation id continuam ativos" e
que o serviço chamado recebe Baggage ou TraceId. Com `None` puro (ex.: ambiente sem coletor, ou desligando a telemetria
para reduzir custo), cada serviço gera o próprio id: a correlação entre serviços — razão de ser do recurso — se perde sem
aviso. Dentro do serviço (escopo de log e cabeçalho de resposta) funciona.

**Sugestão.** Uma de duas:

1. Garantir propagação sem exportação: com `None`, registrar um `ActivityListener` mínimo para `Microsoft.AspNetCore` e
   `System.Net.Http` com `Sample = PropagationData` (cria `Activity` sem gravar) e copiar o correlation id também para o
   Baggage do `Activity`:

```csharp
Activity.Current?.SetBaggage(CorrelationId.AttributeName, correlationId);   // propagado pelo DiagnosticsHandler
```

2. Ou documentar a limitação (feito nesta revisão em [exportadores.md](exportadores.md) e
   [correlation-id.md](correlation-id.md)) e acrescentar um teste que fixe o comportamento escolhido.

### B-02 — Validação do OTLP ignora variáveis por sinal

**Problema.** A validação só olha `OTEL_EXPORTER_OTLP_ENDPOINT` e `OTEL_EXPORTER_OTLP_HEADERS`.

**Evidência.** `Configuration/ObservabilityOptionsValidator.cs:110` e `:137`.

**Impacto.** (a) Quem configura só `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT` etc. (suportadas pelo SDK) não sobe: "Otlp:Endpoint
é obrigatório". (b) `OTEL_EXPORTER_OTLP_TRACES_HEADERS` com endpoint `http` remoto passa pela trava de TLS — a chave do
backend trafega em claro, contrariando a garantia de [seguranca.md](seguranca.md#-modelo-de-ameaças).

**Sugestão.** Considerar as seis variáveis por sinal: aceitar como endpoint presente e aplicar a regra de TLS a cada par
endpoint/cabeçalhos efetivo do sinal.

### B-03 — Execuções de health abandonadas sem teto

**Problema.** Estourado `HealthChecks:Timeout`, a execução é abandonada (`WaitAsync`) e a próxima requisição, passado o
`CacheDuration`, dispara outra. Um check de terceiros que ignora o cancelamento e nunca termina deixa uma tarefa presa
(com conexões/sockets) **por ciclo**.

**Evidência.** `HealthChecks/HealthReportCache.cs:71-84`.

**Impacto.** Com os padrões (30 s + 0,5 s + 5 s), ~100 execuções presas por hora enquanto a dependência travar. Os checks
da própria biblioteca são seguros (banco limita com `WaitAsync` e `CommandTimeout`; HTTP respeita o cancelamento); o risco
é de checks registrados direto no `IHealthChecksBuilder` (ex.: `AddTecVault`).

**Sugestão.** Contar execuções abandonadas por tag; acima de um limite (ex.: 3), responder o último `Unhealthy` sem
disparar outra e registrar `Error` uma vez. Expor o contador numa métrica (`tec.observability.health.abandoned`).

---

## ⚙️ Implementação e desempenho

### P-01 — Varredura de URL aloca mesmo sem nada a redigir

**Problema.** `RedactUrls` faz `Substring` e `Uri.TryCreate` para **toda** URL `http(s)` encontrada no texto, antes de
saber se há query, fragmento ou credencial. O `SpanRedactionProcessor` varre todos os atributos `string` no `OnStart` **e**
no `OnEnd`.

**Evidência.**

```csharp
// Internal/SensitiveData.cs:141
if (Uri.TryCreate(text.Substring(start, end - start), UriKind.Absolute, out var uri) && NeedsRedaction(uri))
// Tracing/TelemetryRedaction.cs:180-182
public override void OnStart(Activity data) => Redact(data);
public override void OnEnd(Activity data) => Redact(data);
```

**Impacto.** Cada span de cliente HTTP (`url.full`) aloca 4 objetos (2 × `string` + `Uri`) sem necessidade, e cada log com
URL na mensagem também. Contradiz "sem alocação quando não há nada a redigir" ([redacao.md](redacao.md)); custo baixo, mas
no caminho quente.

**Sugestão.** Conferência barata antes do parse, como já faz `TelemetryRedaction.RedactUrlText`:

```csharp
var candidate = text.AsSpan(start, end - start);
if (candidate.IndexOfAny('?', '#', '@') >= 0
    && Uri.TryCreate(candidate.ToString(), UriKind.Absolute, out var uri) && NeedsRedaction(uri))
```

E, no `OnStart`, redigir só spans de cofre (as tags do Azure SDK já existem) — o `OnEnd` cobre o resto. Acrescentar ao
`SensitiveTextBenchmarks` um caso "URL sem query" com `[MemoryDiagnoser]`.

### P-02 — MinimumLevel do arquivo filtra depois da fila

**Problema.** `LogFile:MinimumLevel` é aplicado dentro do exportador, depois do `BatchLogRecordExportProcessor`.

**Evidência.** `Exporters/LogFileTelemetryExporter.cs:27-32` (processador em lote sem filtro) e `:105` (filtro no `Export`).

**Impacto.** No exemplo recomendado (`"Azure, LogFile"` com `MinimumLevel: Warning`), todos os `Information` entram na
fila de 2.048 do arquivo, seguram `LogRecord`s do pool e, em rajada, podem fazer a fila descartar justamente os `Warning`.

**Sugestão.** Processador de filtro na frente do lote:

```csharp
internal sealed class MinimumLevelProcessor(LogLevel min, BaseProcessor<LogRecord> inner) : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord r) { if (r.LogLevel >= min && r.LogLevel != LogLevel.None) inner.OnEnd(r); }
    protected override bool OnForceFlush(int t) => inner.ForceFlush(t);
    protected override bool OnShutdown(int t) => inner.Shutdown(t);
    protected override void Dispose(bool d) { if (d) inner.Dispose(); base.Dispose(d); }
}
```

---

## 🗂️ Organização

### O-01 — Sem baseline de API pública

**Problema.** Não há `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` (Microsoft.CodeAnalysis.PublicApiAnalyzers) nem
`PackageValidationBaselineVersion`; `EnablePackageValidation` (`build/Tec.Build.props:78`) só confere a compatibilidade
entre os TFMs do mesmo pacote.

**Impacto.** Com a 0.0.1 publicada, uma mudança de assinatura pública (ex.: `ObservabilityBuilder`, `ITelemetryExporter`,
`HealthCheckResponseWriter`) passa no CI sem aviso.

**Sugestão.** No `tec-workflows` (arquivo canônico), ligar `PackageValidationBaselineVersion` com a última versão
publicada e/ou o `PublicApiAnalyzers` com os dois `.txt` por projeto de pacote.

### O-02 — Projeto de testes do Azure acoplado ao de testes base

`TEC.Observability.Azure.Tests` usa o namespace `TEC.Observability.Tests` (ex.: `IsolatedTelemetryTests.cs:1`) e lê o
`appsettings.Local.json` de `TEC.Observability.Tests` (`TEC.Observability.Azure.Tests.csproj:37`). O arquivo local
existente contém uma connection string real do Application Insights (`InstrumentationKey=8c87…`); está fora do git
(`.gitignore:492`), conforme [testes.md](testes.md). Sugestão: namespace `TEC.Observability.Azure.Tests` e o arquivo local
no próprio projeto; preferir user-secrets.

### O-03 — Workflows reutilizáveis por tag

`ci.yml:28`, `release.yml` e `performance.yml` usam `tudoemcodigo/tec-workflows/...@v2`. Actions de terceiros estão por
SHA (ex.: `integracao-azure.yml:43`), mas a tag `v2` é móvel: quem tiver escrita no `tec-workflows` muda o que roda aqui
com `packages: write` e `id-token: write`. Aceitável por ser da mesma organização; registrar a decisão em
[seguranca.md](seguranca.md#️-build-e-cadeia-de-suprimentos) ou fixar por SHA com Dependabot.

### O-04 — Ambiente local de build

O restore padrão consulta o `tec-interno` mesmo para quem só quer compilar o pacote base (por causa do
`TEC.Vault.AzureKeyVault` dos testes do Azure); sem token, a solução inteira falha. `-p:TecUseLocalProjects=true` resolve
com o TEC.Vault clonado ao lado ([desenvolvimento.md](desenvolvimento.md)). O alvo net8.0 exige o runtime 8 instalado
para rodar os testes.

---

## 🩹 Correções aplicadas — 2026-10-19

Branch `fix/revisao-tecnica-2026-10-19` (sem commit). As correções, inclusive a opção pública nova
`RedactAllLoggerProviders`, foram incorporadas à **0.0.1** (ver [CHANGELOG](../CHANGELOG.md)).

| ID | Status | Arquivos alterados | Teste de regressão |
|---|---|---|---|
| S-01 | ✅ Corrigido | `Internal/SensitiveData.cs` (`SensitiveValue`: `ToString()` de objetos e itens de coleções varridos, sem reflexão — seguro para AOT), `Tracing/LogRedaction.cs`, `Tracing/TelemetryRedaction.cs` | `SensitiveDataRedactionTests.Object_and_collection_log_attributes_are_redacted`, `Span_array_and_object_values_are_redacted` |
| S-02 | ✅ Corrigido | `Configuration/ObservabilityOptions.cs` (`RedactAllLoggerProviders`, padrão `false`), `Tracing/LogRedaction.cs` (`RedactingLoggerFactory`), `DependencyInjection/ServiceCollectionExtensions.cs`; docs `redacao.md`, `seguranca.md` (desligar `Logging:Console` em produção) | `LeakageTests.Other_logger_providers_only_receive_redacted_text_with_RedactAllLoggerProviders` |
| B-01 | ✅ Corrigido | `Tracing/CorrelationIdMiddleware.cs` (correlation id também no Baggage do `Activity`), `Tracing/ContextPropagationListener.cs` (novo, só com `None`), `ServiceCollectionExtensions.cs`, XML de `ObservabilityProvider.None` | `SecurityTests.Correlation_id_and_traceparent_are_propagated_with_any_provider` (None/Otlp, um único `baggage`), `Propagation_listener_is_only_registered_without_telemetry_pipeline` |
| S-03 | ✅ Corrigido | `Internal/SensitiveData.cs` (plural, `sessionid`/`jwt`/`subscriptionkey`/`otp`/`pin`, `Cookie:` até o fim da linha, `\"` em JSON) | Casos novos em `Sensitive_attribute_names_are_detected` e `Sensitive_data_inside_text_is_redacted` |
| B-02 | ✅ Corrigido | `Configuration/ObservabilityOptionsValidator.cs` | `SecurityTests.Otlp_per_signal_variables_count_as_endpoint_and_require_tls_with_headers` (7 casos) |
| B-03 | ✅ Corrigido | `HealthChecks/HealthReportCache.cs` (teto de 3 abandonadas por tag), `HealthChecks/HttpEndpointHealthCheck.cs` (log `Error`, evento 4). A métrica `tec.observability.health.abandoned` sugerida ficou de fora (só o log) | `HealthCheckTests.Abandoned_executions_are_capped_until_hung_checks_finish` |
| P-01 | ✅ Corrigido | `Internal/SensitiveData.cs` (pré-checagem `?`/`#`/`@`), `Tracing/TelemetryRedaction.cs` (`OnStart` só em spans de cofre) | `Text_without_sensitive_data_does_not_allocate`, `Span_start_only_redacts_secret_store_spans` |
| P-02 | ✅ Corrigido | `Exporters/LogFileTelemetryExporter.cs` (`MinimumLevelLogProcessor` antes do lote) | `LogFileExporterTests.File_minimum_level_filters_before_the_batch_queue` |
| O-01 | ❌ Não corrigido | — | A correção é no arquivo canônico `build/Tec.Build.props` (tec-workflows, fora deste repositório); `PackageValidationBaselineVersion` baixa a 0.0.1 do `tec-interno` (401 nesta máquina) e o `PublicApiAnalyzers` exigiria regenerar o `packages.lock.json` do modo pacote — nada disso pôde ser validado aqui |
| S-04 | ➖ Não corrigido | — | Info; exige opção nova (`TrustInboundCorrelationId`) e decisão de comportamento — não trivial |
| S-05 | ➖ Não corrigido | — | Info; aviso de subida para `http` remoto exige logger na validação — não trivial |
| O-02 | ➖ Não corrigido | — | Info; mover o `appsettings.Local.json` (com connection string real, fora do git) é ação manual de cada máquina |
| O-03 | ✅ Documentado | `docs/seguranca.md` (decisão `ref-pin` da mesma organização) | — |
| O-04 | ✅ Documentado | `docs/desenvolvimento.md` (`NU1301` e ASP.NET Core 8 ausente) | — |
| A1 (revisão do CI) | ✅ Corrigido no repositório | `.github/workflows/integracao-azure.yml` (`permissions: {}`, condição com repositório de origem, `main` e eventos `schedule`/`workflow_dispatch`, aviso contra gatilhos de PR), `.github/runner/pre-job.sh` (novo: hook que recusa qualquer outro job no runner), `.github/workflows/README.md` | Hook exercitado à mão (schedule ✅; `pull_request` e outro workflow ❌). **Pendente no GitHub (manual):** *Require approval for all outside collaborators*, runner group restrito ao repositório, runner efêmero, instalar o hook fora do checkout |
| Drift (revisão do CI) | ✅ Corrigido | `TEC.Observability.Tests/TestCategories.cs` (comentário: `Seguranca-Pesada` só no `performance.yml`) | — |

Documentos atualizados: `redacao.md`, `exportadores.md`, `correlation-id.md` (avisos de B-01/S-01/S-02 desta revisão
trocados pelo comportamento novo), `configuracao.md`, `health-checks.md`, `seguranca.md`, `desenvolvimento.md`,
`.github/workflows/README.md`, `CHANGELOG.md`.

| Verificação | Resultado |
|---|---|
| `dotnet build TEC.Observability.slnx -c Release -p:TecUseLocalProjects=true` | ✅ 0 avisos, 0 erros (net8.0 e net10.0) |
| `TEC.Observability.Tests` (net10.0) | ✅ 363/363 (eram 323; +40 casos) |
| `TEC.Observability.Azure.Tests` sem `Category=Integracao` (net10.0) | ✅ 29/29 |
| Testes em net8.0 | ⚠️ Não executados: `Microsoft.AspNetCore.App 8.0` não instalado (só 10.0.12) |
| Restore em modo pacote | ⚠️ Não executado: `NU1301` (401) no `tec-interno`; nenhum lock file alterado |
| `uvx --from actionlint-py actionlint .github/workflows/*.yml` | ✅ sem achados |
| `uvx zizmor --offline .github/workflows` | ✅ sem achados (5 suprimidos pela política do repositório) |
| `shellcheck .github/runner/pre-job.sh` | ✅ sem achados |

---

## ✅ Pontos fortes

- Correlation id com lista branca de caracteres e limite de 128: sem log forging nem header injection; Baggage de entrada
  descartado por padrão; Baggage de saída por lista de hosts nos **dois** propagadores (OpenTelemetry e .NET).
- Health checks: detalhes desligados fora de `Development`, `ExposeDetails` só com `ManagementPort` conferida na conexão
  (não no `Host`), execução única, cache, prazo global, sondas sem redirecionamento/cookies/trace e documento OIDC limitado.
- Redação antes de qualquer exportador (inclusive de outra biblioteca), `RedactedException`, query string mesmo quando a
  distro do Azure desliga a redação da instrumentação, spans de cofre sem nome/versão de segredo.
- Arquivo de log robusto: limites de tamanho, buffer com teto, escape de controles/ANSI, retenção só dos próprios arquivos.
- Validação na subida com mensagens acionáveis; AOT/trimming limpos no pacote base; testes de fuzzing, vazamento e DoS.
- Documentação extensa e, em geral, fiel ao código.

---

## 🚀 Melhorias sugeridas

| Prioridade | Melhoria | Por quê |
|:-:|---|---|
| 1 | Redação de valores não-`string` e arrays ([S-01](#s-01--objetos-e-arrays-escapam-da-redação)) | Fecha o vazamento mais provável (DTOs em log) |
| 2 | Propagação de contexto com `Provider: None` ([B-01](#b-01--com-provider-none-o-correlation-id-não-se-propaga)) | Correlação entre serviços independente de exportar |
| 3 | Opção de redação para todos os `ILoggerProvider` ([S-02](#s-02--redação-só-vale-para-o-pipeline-do-opentelemetry)) | stdout é o canal de log mais comum em contêiner |
| 4 | Nomes sensíveis extras por configuração (`Observability:Redaction:AdditionalKeys`) e plurais ([S-03](#s-03--lacunas-da-heurística-de-texto-livre-e-de-nomes)) | Hoje a lista é fechada; cada domínio tem os seus (`cpf`, `cardNumber`) |
| 5 | Métricas do próprio componente: duração/status por health check, execuções abandonadas, registros descartados/redigidos | Observar o observador; base para alertas |
| 6 | `TrustInboundCorrelationId` e opção de ignorar `traceparent` externo (sampler que trata pai remoto não confiável como raiz) | Hoje a doc manda limpar na borda; nem todo serviço tem gateway |
| 7 | Baseline de API pública ([O-01](#o-01--sem-baseline-de-api-pública)) | Pacote já publicado |
| 8 | Filtro de nível antes da fila do `LogFile` ([P-02](#p-02--minimumlevel-do-arquivo-filtra-depois-da-fila)) e pré-checagem de URL ([P-01](#p-01--varredura-de-url-aloca-mesmo-sem-nada-a-redigir)) | Menos pressão no caminho quente |
| 9 | Variáveis OTLP por sinal ([B-02](#b-02--validação-do-otlp-ignora-variáveis-por-sinal)) | Compatibilidade com a especificação e trava de TLS completa |

---

## 📝 Ajustes na documentação

Corrigidos nesta revisão (o doc dizia X, o código faz Y):

| Arquivo | Ajuste |
|---|---|
| [redacao.md](redacao.md#-uso) | "Não há nada a ligar: a redação vale para qualquer `Provider`" ganhou a ressalva de que só cobre o pipeline do OpenTelemetry (console/stdout e outros `ILoggerProvider` recebem o original) e de que objetos/arrays em atributos não são varridos ([S-01](#s-01--objetos-e-arrays-escapam-da-redação), [S-02](#s-02--redação-só-vale-para-o-pipeline-do-opentelemetry)) |
| [redacao.md](redacao.md) | "Sem alocação" passou a excetuar textos com URL `http(s)` ([P-01](#p-01--varredura-de-url-aloca-mesmo-sem-nada-a-redigir)) |
| [exportadores.md](exportadores.md) | `None`: o correlation id fica local; sem pipeline de tracing de outra biblioteca, `traceparent` e `baggage` não são propagados ([B-01](#b-01--com-provider-none-o-correlation-id-não-se-propaga)) |
| [correlation-id.md](correlation-id.md) | Aviso equivalente sobre `Provider: None` na seção de visão geral |
| [README.md](README.md) | Linha desta revisão na tabela de temas e no mapa |

Pendentes na data da revisão (todos resolvidos depois, junto com as correções acima):

- ✅ XML de `ObservabilityProvider.None` (`Configuration/ObservabilityOptions.cs`): ajustado com a correção de
  [B-01](#b-01--com-provider-none-o-correlation-id-não-se-propaga).
- ✅ [seguranca.md](seguranca.md): "segredo em stdout" e "DTO em atributo de log" no modelo de ameaças e o desligamento do
  `Logging:Console` na configuração de produção.
- ✅ [exportadores.md](exportadores.md): as variáveis OTLP por sinal passaram a ser aceitas na validação
  ([B-02](#b-02--validação-do-otlp-ignora-variáveis-por-sinal)).

---
⬅️ [Desenvolvimento local](desenvolvimento.md) · [📚 Índice](README.md) · [Decisões de arquitetura](adr/README.md) ➡️
