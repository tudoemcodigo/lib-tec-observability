# Instruções para agentes — TEC.Observability

Este arquivo complementa as instruções herdadas de `AGENT_BASE.md`. O TEC.Observability deixa cada serviço observável do mesmo jeito: rastreia as requisições, coleta métricas e logs, correlaciona chamadas entre serviços e informa a saúde do serviço e das dependências, com o destino da telemetria escolhido só por configuração.

## Escopo e segurança de alterações

- O escopo padrão é este repositório. Não altere outros repositórios `TEC.*`, não publique pacotes/tags/releases e não rode sincronizações que escrevam fora daqui sem pedido explícito.
- Antes de editar, confira `git status` e `git diff`. Preserve alterações não commitadas; não use reset, checkout destrutivo ou limpeza para descartar trabalho existente.
- Mudanças em pipeline OpenTelemetry, exportadores (OTLP, Console, LogFile, Azure Monitor), health checks (liveness/readiness/banco/SSO/TCP/API), correlation id, redação de dados sensíveis ou alvos .NET exigem revisão de compatibilidade, documentação, changelog e consumidores.
- Testes devem atravessar contratos públicos e verificar resultados observáveis. Para mudanças de comportamento, cubra entradas inválidas, limites, falhas e concorrência quando aplicável.
- Trate todo dado externo como não confiável. Não exponha segredos, PII ou conteúdo controlado pelo usuário em exceções, logs, URLs ou sumários.
- Redação de dados sensíveis é heurística que prefere esconder a vazar: query string, credenciais em URL, atributos sensíveis, `Bearer`/`Basic`, pares `chave=valor` sensíveis em spans e logs.
- Configuração validada na subida (`ValidateOnStart`): `Provider` sem exportador, endpoint inválido, cabeçalho OTLP em `http` ou health check mais longo que o prazo derrubam a inicialização.
- Seguro por padrão: fora de `Development` o JSON de health traz só status e horário; Baggage do cliente descartado; arquivo de log à prova de *log forging*.

## Mapa da implementação

- `TEC.Observability/`: biblioteca em `net10.0`, pacote base compatível com Native AOT.
  - `DependencyInjection/`: `AddTecObservability`, `ObservabilityBuilder` (encadeia `AddExporter`, `AddActivitySource`, `AddMeter`, `AddDatabaseHealthCheck`, `AddSsoHealthCheck`, `AddExternalServiceHealthCheck`, `AddTcpHealthCheck`), `UseTecObservability` (correlation id + `/health/live` + `/health/ready`).
  - `Configuration/`: `ObservabilityOptions` com validação na subida, `Provider` combinável (`"Otlp, LogFile"`), `OtlpExporterSettings`, `LogFileExporterSettings`, `ObservabilityHealthChecksOptions`.
  - `Internal/`: redação de texto livre e de valores (`SensitiveData.cs`: `SensitiveKeys`, `SensitiveText`, `SensitiveValue`), `RedactedException`, `AsciiToken`, `HttpUrl`.
  - `Tracing/`: correlation id (`CorrelationIdMiddleware`, nomes `X-Correlation-ID` e `correlation.id`), filtro de Baggage por host (`BaggagePropagation.cs`: `BaggageAllowedHosts`; `AllowInboundBaggage` é lida no middleware), `ContextPropagationListener` (só com `Provider: None`), `RequestPathFilter` e os processadores de redação de logs e spans (`LogRedaction.cs`, `TelemetryRedaction.cs`).
  - `HealthChecks/`: liveness (só processo), readiness (dependências com cache, execução única, prazo), JSON padronizado, rotas ignoradas no trace.
  - `Exporters/`: `ITelemetryExporter` (ponto de extensão para backends próprios), exportadores embutidos `Otlp`, `Console` e `LogFile`.
  - Fontes e medidores: o pacote não cria `ActivitySource` nem `Meter` próprios; registra o nome do `ServiceName` e assina o prefixo `TEC.*` dos outros componentes (`ServiceCollectionExtensions.TecTelemetryPrefix`).
- `TEC.Observability.Azure/`: exportador Azure Monitor / Application Insights (`.AddAzureMonitorExporter()`), ingestão por chave ou Entra ID. **Não compatível com Native AOT**.
- `TEC.Observability.Tests/`: 499 testes unitários (regressão, segurança, fuzzing, DoS, vazamento) em `net10.0` e sem ICU.
- `TEC.Observability.Azure.Tests/`: 29 unitários e 3 de integração (Application Insights real).
- `TEC.Observability.LoadTests/`: 5 de carga rápida (`Carga-CI`) e suítes pesadas (`Carga-Pesada`, `Seguranca-Pesada`) sob demanda.
- `samples/`: API de exemplo e gerador de carga HTTP.
- `build/`: props/targets compartilhados.
- `docs/adr/`: decisões arquiteturais.
- `docs/`: guias por configuração, exportadores, traces/métricas/logs, redação, health checks, correlation id, Aspire, integrações TEC, segurança, testes, desenvolvimento.

## Contratos relevantes

- `Directory.Build.props` é a fonte única de versão do pacote (uma versão para os dois pacotes). A versão inicial é `0.0.1`; não introduza versões nos `.csproj`.
- `Directory.Packages.props` declara uma versão por pacote (alvo único `net10.0`); pacotes do ASP.NET Core acompanham o .NET do alvo.
- Dependências TEC.* são declaradas com `<TecReference Include="..." />`, não com `PackageReference` direto.
- O TEC.Observability **não depende de nenhum TEC.\*** em produção e **nenhum componente depende dele**. Cada componente emite `ActivitySource` e `Meter` da BCL com o nome `TEC.<Componente>`; o TEC.Observability apenas assina o prefixo `TEC.*`.
- `Otlp:Headers` e `Azure:ConnectionString` podem vir do TEC.Vault; health check do cofre no `/health/ready`. A redação não conhece o TEC.Vault: cofres próprios entram por `SecretStoreHosts` (os de nuvem são reconhecidos pelo host).
- Estado do processo (estático, só soma): `AdditionalSensitiveKeys`, `SecretStoreHosts`, propagadores e `BaggageHostPolicy.Default`. Testes que mexem nisso usam nomes únicos ou `[NotInParallel]`.
- Escopos de log: o `IncludeScopes` do OpenTelemetry fica desligado; `Observability:IncludeScopes` + `AllowedScopeKeys` exportam escopos como atributos redigidos (`LogScopeCapture` + `LogRedactionProcessor`).
- Native AOT: ✅ `TEC.Observability` (`IsAotCompatible`, binding de configuração gerado em compilação, JSON com `Utf8JsonWriter`) · ❌ `TEC.Observability.Azure` (SDK do Azure Monitor gera avisos de trimming).
- Workflows dos consumidores chamam `tudoemcodigo/tec-workflows`; confira as tags realmente publicadas antes de alterar referências.
- A licença é Apache-2.0. Mantenha `LICENSE`, `NOTICE`, badge/README e `<PackageLicenseExpression>` coerentes.

## Documentação

- `README.md` e `CHANGELOG.md` têm conteúdo próprio do TEC.Observability: siga o padrão do template, mantenha a marca Tudo em Código em todos os READMEs e não substitua o conteúdo específico por placeholders genéricos.
- Atualize `README.md`, `CHANGELOG.md` e os guias em `docs/` quando uma mudança alterar comportamento ou contrato.
- Registre decisões transversais em um ADR numerado e mantenha `docs/adr/README.md` e `docs/README.md` atualizados.
- Consulte `docs/revisao-tecnica.md` como relatório de auditoria com data e escopo; não trate resultados históricos como estado atual sem nova verificação.
- Workflows e configurações centrais do consumidor estão em `.github/workflows/`; mantenha `.github/workflows/README.md` fiel aos gatilhos, permissões e inputs reais.

## Verificação local

```powershell
dotnet restore TEC.Observability.slnx
dotnet build TEC.Observability.slnx -c Release
dotnet test --solution TEC.Observability.slnx -c Release
$env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1"; dotnet test --solution TEC.Observability.slnx -c Release; Remove-Item Env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT
git diff --check
```

O CI também valida convenções, pacote e workflows reutilizáveis. Não afirme que o CI está funcional apenas com build/testes locais: confirme que as tags chamadas existem e que os `permissions` do chamador incluem o mínimo exigido pelo workflow reutilizável.