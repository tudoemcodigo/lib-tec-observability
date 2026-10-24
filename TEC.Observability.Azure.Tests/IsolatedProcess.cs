using System.Diagnostics;
using System.Text.Json;

namespace TEC.Observability.Tests;

/// <summary>
/// Roda um cenário num processo filho: o próprio executável de testes, iniciado com a variável <see cref="ScenarioVariable"/> e
/// com este assembly como startup hook do .NET (<c>DOTNET_STARTUP_HOOKS</c>), executa o cenário antes do <c>Main</c> do runner
/// de testes e encerra. (Um inicializador de módulo não serve: ele roda dentro do construtor do módulo, e as continuações
/// assíncronas do cenário em outras threads ficariam esperando por ele.)
/// </summary>
/// <remarks>
/// A instrumentação de ASP.NET Core e HttpClient do OpenTelemetry assina os eventos do processo inteiro: num processo com vários
/// hosts de teste, o span de uma requisição recebe as tags gravadas pela instrumentação de todos os provedores de telemetria
/// vivos, cada um com as suas opções. Conferir o que a configuração de um provedor produz (ex.: a redação desligada pela distro
/// do Azure Monitor) só é confiável com um único host no processo.
/// </remarks>
internal static class IsolatedProcess
{
    internal const string ScenarioVariable = "TEC_TESTES_OBSERVABILITY_CENARIO";
    internal const string ArgumentVariable = "TEC_TESTES_OBSERVABILITY_ARGUMENTO";

    /// <summary>Prefixo das linhas de resultado na saída do processo filho (o resto é ignorado).</summary>
    private const string ResultPrefix = "@@resultado ";

    private static readonly TimeSpan ChildTimeout = TimeSpan.FromSeconds(90);

    /// <summary>Chamado pelo <see cref="StartupHook"/> no processo filho.</summary>
    internal static void RunRequestedScenario()
    {
        var scenario = Environment.GetEnvironmentVariable(ScenarioVariable);
        if (string.IsNullOrEmpty(scenario))
            return;

        var exitCode = 0;
        try
        {
            var argument = Environment.GetEnvironmentVariable(ArgumentVariable) ?? string.Empty;
            var results = IsolatedScenarios.RunAsync(scenario, argument).GetAwaiter().GetResult();
            foreach (var result in results)
                Console.Out.WriteLine(ResultPrefix + JsonSerializer.Serialize(result));
        }
#pragma warning disable CA1031 // Qualquer falha do cenário vira código de saída e texto para o teste que o iniciou.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Console.Error.WriteLine(ex);
            exitCode = 1;
        }

        Console.Out.Flush();
        Console.Error.Flush();
        Environment.Exit(exitCode);
    }

    /// <summary>Inicia o processo filho e devolve os resultados do cenário.</summary>
    public static async Task<IReadOnlyList<Dictionary<string, string?>>> RunAsync(string scenario, string argument)
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("Caminho do processo de testes indisponível.");
        var start = new ProcessStartInfo(process)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        // Rodando como "dotnet TEC.Observability.Azure.Tests.dll" (em vez do executável), o assembly vai como argumento.
        if (string.Equals(Path.GetFileNameWithoutExtension(process), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(IsolatedProcess).Assembly.Location);
        start.Environment[ScenarioVariable] = scenario;
        start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(IsolatedProcess).Assembly.Location;
        start.Environment[ArgumentVariable] = argument;

        using var child = Process.Start(start) ?? throw new InvalidOperationException("Processo filho não iniciou.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        using (var timeout = new CancellationTokenSource(ChildTimeout))
        {
            try
            {
                await child.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                child.Kill(entireProcessTree: true);
                throw new TimeoutException($"Cenário '{scenario}' ({argument}) não terminou em {ChildTimeout.TotalSeconds}s. Erro: {await error}");
            }
        }

        var lines = (await output).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (child.ExitCode != 0)
            throw new InvalidOperationException($"Cenário '{scenario}' ({argument}) falhou ({child.ExitCode}): {await error}");

        return [.. lines
            .Where(l => l.StartsWith(ResultPrefix, StringComparison.Ordinal))
            .Select(l => JsonSerializer.Deserialize<Dictionary<string, string?>>(l[ResultPrefix.Length..])!)];
    }
}

