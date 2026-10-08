# 📡 TEC.Observability

Deixa cada serviço ASP.NET Core observável do mesmo jeito: traces, métricas e logs com OpenTelemetry, destino escolhido por
configuração (`Otlp`, `Console`, `LogFile`, `None` ou um exportador próprio, combináveis), endpoints de liveness e
readiness com JSON padronizado, correlation id ligado ao trace W3C e redação de dados sensíveis antes de qualquer
exportador. Compatível com Native AOT, `net8.0` e `net10.0`.

## Quando usar

- Em todo serviço ASP.NET Core que precisa de telemetria e sondas de saúde padronizadas.
- Para trocar de backend (AWS ADOT, GCP, Jaeger, Grafana, coletor on-premises) só mudando o `appsettings.json`.
- Junto com o .NET Aspire `ServiceDefaults` (com `Provider: None`, ele cuida só de health checks, correlation id e redação).

O pacote **não depende de nenhum componente TEC**: as fontes `TEC.*` (TEC.Vault, TEC.Cqrs, TEC.Security, TEC.ORM) são
apenas assinadas. Para o Azure Monitor, instale também o `TEC.Observability.Azure` (mesma versão).

## Instalação

Feed GitHub Packages da organização `tudoemcodigo` (PAT *classic* com `read:packages`):

```bash
dotnet nuget add source https://nuget.pkg.github.com/tudoemcodigo/index.json -n tec-interno -u <usuario> -p <PAT>
dotnet add package TEC.Observability --version 0.0.1
```

## Início rápido

```json
{
  "Observability": {
    "ServiceName": "pedidos-api",
    "Environment": "development",
    "Provider": "Console"
  }
}
```

```csharp
using Microsoft.Data.SqlClient;
using TEC.Observability.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTecObservability(builder.Configuration)
    .AddDatabaseHealthCheck("PrimaryDb", SqlClientFactory.Instance, builder.Configuration.GetConnectionString("Default")!)
    .AddExternalServiceHealthCheck("PaymentGateway", new Uri("https://pagamentos.empresa.com/health/live"));

var app = builder.Build();
app.UseTecObservability();   // correlation id + /health/live + /health/ready

app.MapGet("/", () => "ok");
app.Run();
```

A configuração é validada na subida: um erro derruba a inicialização, não a primeira requisição. Em produção, troque o
`Provider` (ex.: `"Otlp, LogFile"` com `Observability:Otlp:Endpoint`).

## Documentação

- [README e visão geral](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/README.md)
- [Configuração e opções](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/docs/configuracao.md)
- [Exportadores](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/docs/exportadores.md)
- [Health checks](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/docs/health-checks.md)
- [Redação de dados sensíveis](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/docs/redacao.md)
- [Segurança](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/docs/seguranca.md)
- [Changelog](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/CHANGELOG.md)

Licença [MIT](https://github.com/tudoemcodigo/lib-tec-observability/blob/main/LICENSE).
