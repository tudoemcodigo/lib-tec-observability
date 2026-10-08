using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TEC.Observability.SampleApi;

namespace TEC.Observability.LoadTests.Infrastructure;

/// <summary>
/// API de exemplo hospedada no próprio processo, em Kestrel real (sockets TCP em 127.0.0.1, porta livre escolhida pelo SO), com o
/// TEC.Observability ligado (padrão <c>Provider: Contagem</c>, que conta a telemetria sem enviá-la).
/// </summary>
public sealed class SampleApiHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SampleApiHost(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    /// <summary>Endereço da API (ex.: http://127.0.0.1:53817/).</summary>
    public Uri BaseAddress { get; }

    /// <summary>Container da aplicação.</summary>
    public IServiceProvider Services => _app.Services;

    /// <summary>Contadores do exportador <c>Contagem</c>.</summary>
    public TelemetryCounters Counters => _app.Services.GetRequiredService<TelemetryCounters>();

    /// <summary>Inicia a API.</summary>
    /// <param name="configure">Ajustes extras no builder (ex.: exportador em memória).</param>
    /// <param name="settings">Configuração com chave completa (ex.: <c>("Observability:Provider", "LogFile")</c>).</param>
    public static async Task<SampleApiHost> StartAsync(Action<WebApplicationBuilder>? configure = null, params (string Key, string? Value)[] settings)
    {
        var app = SampleApiApp.Create([], builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            // Sem console: escrever cada log no terminal mediria o console, não a biblioteca. O provedor do OpenTelemetry
            // é registrado depois (no AddTecObservability) e continua ligado.
            builder.Logging.ClearProviders();
            // Logs de requisição do ASP.NET Core ligados (Request starting ... ?token=...): sem eles, a conferência de que nenhum
            // log exportado contém o segredo da query string (LeakedLogs) não teria o que conferir.
            builder.Configuration["Logging:LogLevel:Microsoft.AspNetCore.Hosting.Diagnostics"] = "Information";
            builder.Configuration.AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value));
            configure?.Invoke(builder);
        });

        await app.StartAsync();
        return new SampleApiHost(app, new Uri(app.Urls.First()));
    }

    /// <summary>Cliente HTTP com uma conexão por worker.</summary>
    public HttpClient CreateClient(int concurrency) =>
        new(new SocketsHttpHandler { MaxConnectionsPerServer = concurrency }, disposeHandler: true)
        {
            BaseAddress = BaseAddress,
            Timeout = TimeSpan.FromSeconds(30)
        };

    /// <summary>
    /// Espera os spans em andamento terminarem (o span de servidor termina depois de a resposta chegar ao cliente): devolve a
    /// foto dos contadores quando nenhum span termina por 300 ms.
    /// </summary>
    public async Task<TelemetrySnapshot> SettledCountersAsync(TimeSpan? timeout = null)
    {
        var watch = Stopwatch.StartNew();
        var snapshot = Counters.Snapshot();
        while (watch.Elapsed < (timeout ?? TimeSpan.FromSeconds(10)))
        {
            await Task.Delay(300);
            var next = Counters.Snapshot();
            if (next == snapshot)
                break;
            snapshot = next;
        }

        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
