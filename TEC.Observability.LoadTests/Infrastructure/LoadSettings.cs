using System.Globalization;

namespace TEC.Observability.LoadTests.Infrastructure;

/// <summary>
/// Parâmetros dos testes de carga (as categorias ficam em <see cref="TestCategories"/>). Os volumes e durações dos testes pesados podem ser ajustados por variáveis de
/// ambiente, sem recompilar (ex.: TEC_CARGA_SOAK_SEGUNDOS=600 para um soak de 10 minutos). Mesmos nomes do TEC.Core.
/// </summary>
public static class LoadSettings
{
    /// <summary>
    /// Chave de [NotInParallel] de todas as classes: um host de cada vez no processo. A instrumentação do OpenTelemetry
    /// (ASP.NET Core e HttpClient) escuta o processo inteiro, então dois hosts ao mesmo tempo contariam os spans um do outro;
    /// e os testes pesados medem memória e vazão do processo inteiro.
    /// </summary>
    public const string Exclusive = "Carga-Exclusiva";

    /// <summary>Duração do soak (padrão 120 s).</summary>
    public static int SoakSeconds => GetInt("TEC_CARGA_SOAK_SEGUNDOS", 120);

    /// <summary>Duração da carga pesada na API de exemplo (padrão 60 s).</summary>
    public static int ApiSeconds => GetInt("TEC_CARGA_API_SEGUNDOS", 60);

    /// <summary>Requisições simultâneas na carga pesada da API (padrão 64).</summary>
    public static int ApiConcurrency => GetInt("TEC_CARGA_API_CONCORRENCIA", 64);

    /// <summary>Sondas simultâneas na rajada contra os endpoints de health (padrão 256).</summary>
    public static int HealthConcurrency => GetInt("TEC_CARGA_HEALTH_CONCORRENCIA", 256);

    /// <summary>Registros gravados no teste de volume do arquivo de logs (padrão 1 milhão).</summary>
    public static int LogRecords => GetInt("TEC_CARGA_LOGS", 1_000_000);

    /// <summary>
    /// Pasta onde os testes gravam os relatórios em Markdown (o workflow de performance publica no resumo do CI).
    /// Sem a variável, os relatórios vão só para a saída do teste.
    /// </summary>
    public static string? ReportDirectory => Environment.GetEnvironmentVariable("TEC_CARGA_RELATORIOS");

    /// <summary>Escreve o relatório na saída do teste e, se configurado, em arquivo.</summary>
    public static void Report(string title, string body)
    {
        Console.WriteLine($"## {title}{Environment.NewLine}{body}");
        if (ReportDirectory is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            lock (ReportLock)
                File.AppendAllText(Path.Combine(directory, "carga.md"), $"### {title}{Environment.NewLine}{Environment.NewLine}{body}{Environment.NewLine}");
        }
    }

    private static readonly object ReportLock = new();

    private static int GetInt(string name, int defaultValue) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value
            : defaultValue;
}
