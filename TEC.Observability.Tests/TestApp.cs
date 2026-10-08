using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TEC.Observability.DependencyInjection;
using TEC.Observability.HealthChecks;

namespace TEC.Observability.Tests;

/// <summary>Sobe uma Minimal API em memória com a observabilidade registrada.</summary>
internal static class TestApp
{
    public static Dictionary<string, string?> Settings(params (string Key, string? Value)[] extra)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Observability:ServiceName"] = "testes-api",
            ["Observability:ServiceVersion"] = "1.2.3",
            ["Observability:Environment"] = "test",
        };
        foreach (var (key, value) in extra)
            settings[$"Observability:{key}"] = value;
        return settings;
    }

    public static IConfiguration Configuration(params (string Key, string? Value)[] extra) =>
        new ConfigurationBuilder().AddInMemoryCollection(Settings(extra)).Build();

    public static async Task<WebApplication> StartAsync(
        Action<ObservabilityBuilder>? observability = null,
        FakeHttpHandler? handler = null,
        Action<WebApplication>? endpoints = null,
        Action<IServiceCollection>? services = null,
        string? environment = null,
        Action<IServiceCollection>? servicesBefore = null,
        IReadOnlyDictionary<string, string?>? rootSettings = null,
        IReadOnlyDictionary<string, string?>? lateSettings = null,
        params (string Key, string? Value)[] settings)
    {
        // Development por padrão: o JSON de health traz os detalhes que a maioria dos testes confere.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment ?? Environments.Development });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(Settings(settings));
        if (rootSettings is not null)
            builder.Configuration.AddInMemoryCollection(rootSettings);

        servicesBefore?.Invoke(builder.Services);
        var observabilityBuilder = builder.Services.AddTecObservability(builder.Configuration);
        observability?.Invoke(observabilityBuilder);
        services?.Invoke(builder.Services);

        // Provedor de configuração adicionado depois do registro (como um cofre de segredos carregado mais tarde).
        if (lateSettings is not null)
            builder.Configuration.AddInMemoryCollection(lateSettings);

        if (handler is not null)
            builder.Services.AddHttpClient(HttpEndpointHealthCheck.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);

        var app = builder.Build();
        app.UseTecObservability();
        endpoints?.Invoke(app);

        await app.StartAsync();
        return app;
    }
}

/// <summary>Responde às verificações HTTP sem rede e registra as chamadas recebidas.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    public static FakeHttpHandler Status(HttpStatusCode status, string? json = null) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(json ?? "{}", System.Text.Encoding.UTF8, "application/json") });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
            Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }
}

/// <summary>Coletor OTLP falso: registra endereço e cabeçalho <c>x-api-key</c> de cada exportação.</summary>
internal sealed class CapturingHandler : HttpMessageHandler
{
    private readonly List<(Uri Uri, string? ApiKey)> _requests = [];

    public IReadOnlyList<(Uri Uri, string? ApiKey)> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(Capture(request));

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => Capture(request);

    private HttpResponseMessage Capture(HttpRequestMessage request)
    {
        var apiKey = request.Headers.TryGetValues("x-api-key", out var values) ? string.Join(",", values) : null;
        lock (_requests)
            _requests.Add((request.RequestUri!, apiKey));
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}

/// <summary>
/// Servidor HTTP mínimo sobre TCP em 127.0.0.1: guarda os cabeçalhos de cada requisição e responde 200. Serve para testar o que
/// o <see cref="HttpClient"/> real (com a instrumentação e a propagação do OpenTelemetry) escreve na requisição de saída.
/// </summary>
internal sealed class RawHttpServer : IAsyncDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _requests = [];
    private readonly Task _loop;

    public RawHttpServer()
    {
        _listener.Start();
        _loop = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>Cabeçalhos brutos de cada requisição recebida.</summary>
    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                var text = new System.Text.StringBuilder();
                while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                        break;
                    text.Append(System.Text.Encoding.ASCII.GetString(buffer, 0, read));
                }

                lock (_requests)
                    _requests.Add(text.ToString());
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _loop;
        }
        catch (System.Net.Sockets.SocketException)
        {
        }
        _stop.Dispose();
    }
}
