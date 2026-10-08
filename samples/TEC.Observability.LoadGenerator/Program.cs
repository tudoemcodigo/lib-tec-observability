using System.Globalization;
using System.Text.Json;
using TEC.Observability.LoadGenerator;

// Gerador de carga da API de exemplo (TEC.Observability.SampleApi). Códigos de saída: 0 = dentro dos limites, 1 = limite violado, 2 = argumentos inválidos.
try
{
    var arguments = ParseArguments(args);
    if (arguments.ContainsKey("ajuda") || !arguments.ContainsKey("url"))
    {
        PrintUsage();
        return arguments.ContainsKey("ajuda") ? 0 : 2;
    }

    var options = new LoadOptions
    {
        Concurrency = GetInt(arguments, "concorrencia", 32),
        Duration = TimeSpan.FromSeconds(GetInt(arguments, "duracao", 30)),
        WarmUp = TimeSpan.FromSeconds(GetInt(arguments, "aquecimento", 5)),
        Seed = GetInt(arguments, "semente", 2026),
        Scenarios = SampleScenarios.Parse(arguments.GetValueOrDefault("cenarios")),
    };
    double maxErrorRate = GetDouble(arguments, "max-erro", 0.01);
    double? maxP95 = arguments.ContainsKey("max-p95") ? GetDouble(arguments, "max-p95", 0) : null;

    using var handler = new SocketsHttpHandler { MaxConnectionsPerServer = options.Concurrency, PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
    using var client = new HttpClient(handler) { BaseAddress = new Uri(arguments["url"]), Timeout = TimeSpan.FromSeconds(30) };
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    Console.WriteLine($"Carga em {client.BaseAddress}: {options.Concurrency} workers, {options.WarmUp.TotalSeconds} s de aquecimento + {options.Duration.TotalSeconds} s medidos...");
    var report = await LoadRunner.RunAsync(client, options, cts.Token);
    Console.WriteLine(report.ToText());

    if (arguments.TryGetValue("json", out var jsonPath))
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

    bool ok = report.Requests > 0 && report.ErrorRate <= maxErrorRate && (maxP95 is null || report.Latency.P95 <= maxP95);
    if (!ok)
        Console.Error.WriteLine($"Limite violado: taxa de erro máxima {maxErrorRate:P2}{(maxP95 is null ? "" : $", p95 máximo {maxP95} ms")}.");
    return ok ? 0 : 1;
}
catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException or OverflowException)
{
    Console.Error.WriteLine(ex.Message);
    PrintUsage();
    return 2;
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Argumento inesperado: {args[i]}");

        var name = args[i][2..];
        result[name] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
    }

    return result;
}

static int GetInt(Dictionary<string, string> arguments, string name, int defaultValue) =>
    arguments.TryGetValue(name, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : defaultValue;

static double GetDouble(Dictionary<string, string> arguments, string name, double defaultValue) =>
    arguments.TryGetValue(name, out var value) ? double.Parse(value, CultureInfo.InvariantCulture) : defaultValue;

static void PrintUsage() => Console.WriteLine($"""
    Uso: dotnet run -c Release --project samples/TEC.Observability.LoadGenerator -f net10.0 -- --url <endereço> [opções]

      --url <endereço>        API de exemplo (ex.: http://127.0.0.1:5080). Obrigatório.
      --duracao <s>           Segundos medidos (padrão 30).
      --aquecimento <s>       Segundos de aquecimento fora das estatísticas (padrão 5).
      --concorrencia <n>      Requisições simultâneas (padrão 32).
      --cenarios <lista>      Cenários e pesos, ex.: consultar,criar:50,ready:5 (padrão: mistura completa).
                              Disponíveis: {string.Join(", ", SampleScenarios.All.Select(s => $"{s.Name} ({s.Weight})"))}
      --semente <n>           Semente dos sorteios (padrão 2026).
      --max-erro <fração>     Taxa de erro máxima para sair com 0 (padrão 0.01).
      --max-p95 <ms>          Latência p95 máxima para sair com 0 (opcional).
      --json <arquivo>        Grava o relatório em JSON.
    """);
