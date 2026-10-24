# ☁️ TEC.Observability.Azure

Exportador do **Azure Monitor / Application Insights** para o `TEC.Observability`, com a distro oficial do OpenTelemetry
(`Azure.Monitor.OpenTelemetry.AspNetCore`). É ligado só por configuração (`Observability:Provider` com `Azure`) e aceita
ingestão por chave ou autenticada pelo Entra ID — fora de `Development`, só com Workload Identity ou Managed Identity.

## Quando usar

- Quando algum ambiente do serviço envia telemetria para o Application Insights.
- Quem usa só `Otlp`, `Console`, `LogFile` ou `None` **não** precisa deste pacote.

> [!IMPORTANT]
> Use a **mesma versão** do `TEC.Observability`. Este pacote **não é compatível com Native AOT/trimming**: a distro do
> Azure Monitor gera avisos IL2026/IL3050 no próprio código.

## Instalação

Feed GitHub Packages da organização `tudoemcodigo` (PAT *classic* com `read:packages`):

```bash
dotnet nuget add source https://nuget.pkg.github.com/tudoemcodigo/index.json -n tec-interno -u <usuario> -p <PAT>
dotnet add package TEC.Observability.Azure --version 0.0.1
```

## Início rápido

```json
{
  "Observability": {
    "ServiceName": "pedidos-api",
    "Environment": "production",
    "Provider": "Azure",
    "Azure": {
      "ConnectionString": "InstrumentationKey=<chave>;IngestionEndpoint=https://<regiao>.in.applicationinsights.azure.com/",
      "UseManagedIdentity": true
    }
  }
}
```

```csharp
using TEC.Observability.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Mesmo Program.cs em todos os ambientes: o exportador só é ligado quando "Azure" está no Provider
builder.Services.AddTecObservability(builder.Configuration)
    .AddAzureMonitorExporter();

var app = builder.Build();
app.UseTecObservability();
app.Run();
```

A connection string pode vir da variável `APPLICATIONINSIGHTS_CONNECTION_STRING` ou de um cofre adicionado depois do
registro. Com `UseManagedIdentity`, a identidade precisa do papel **Monitoring Metrics Publisher** no recurso.

## Documentação

- [Exportador do Azure Monitor](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/docs/exportadores.md#️-azure-monitor)
- [README e visão geral](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/README.md)
- [Configuração e opções](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/docs/configuracao.md)
- [Segurança](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/docs/seguranca.md)
- [Changelog](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/CHANGELOG.md)

Licença [MIT](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/LICENSE).
