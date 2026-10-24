using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using TEC.Observability.LoadTests.Infrastructure;

namespace TEC.Observability.LoadTests.Volume;

/// <summary>
/// Volume do exportador <c>LogFile</c>: milhões de registros com troca de arquivo por tamanho e retenção, conferindo que nada se
/// perde no ritmo que o processador em lote comporta, que a pasta nunca passa do limite de arquivos e que a memória não cresce.
/// Quantidade em TEC_CARGA_LOGS (padrão 1 milhão).
/// </summary>
[Explicit]
[Category(TestCategories.LoadHeavy)]
[NotInParallel(LoadSettings.Exclusive)]
public class VolumeTests
{
    private const long MaxFileSize = 4L * 1024 * 1024;
    private const int RetainedFiles = 5;

    [Test]
    public async Task LogFile_MillionsOfRecords_RotateRetainAndKeepMemoryConstant()
    {
        int total = LoadSettings.LogRecords;
        // Abaixo da fila do processador em lote (2.048): a cada bloco o teste espera a gravação, então nenhum registro é descartado
        const int Block = 1_500;
        var folder = Path.Combine(Path.GetTempPath(), "tec-obs-volume-" + Guid.NewGuid().ToString("N"));
        try
        {
            var memory = new List<long>();
            var watch = Stopwatch.StartNew();
            int maxFilesSeen = 0;
            await using (var host = await SampleApiHost.StartAsync(settings:
            [
                ("Observability:Provider", "LogFile"),
                ("Observability:LogFile:FolderPath", folder),
                ("Observability:LogFile:RollingInterval", "None"),
                ("Observability:LogFile:MaxFileSizeBytes", MaxFileSize.ToString(CultureInfo.InvariantCulture)),
                ("Observability:LogFile:RetainedFileCount", RetainedFiles.ToString(CultureInfo.InvariantCulture)),
            ]))
            {
                var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Carga.Volume");
                var provider = host.Services.GetRequiredService<LoggerProvider>();
                for (int written = 0; written < total;)
                {
                    int count = Math.Min(Block, total - written);
                    for (int i = 0; i < count; i++)
                        VolumeLog.Record(logger, written + i, "pedido processado com sucesso pelo serviço de exemplo");
                    written += count;
                    provider.ForceFlush(30_000);

                    if (written % (Block * 100) == 0)
                    {
                        memory.Add(MemoryProbe.RetainedBytes());
                        maxFilesSeen = Math.Max(maxFilesSeen, Directory.GetFiles(folder, "*.txt").Length);
                    }
                }
            }
            var elapsed = watch.Elapsed;

            var files = new DirectoryInfo(folder).GetFiles("*.txt").OrderBy(f => f.LastWriteTimeUtc).ToArray();
            var lastFile = files[^1];
            var lastLine = File.ReadLines(lastFile.FullName).Last(l => l.Contains(" registro ", StringComparison.Ordinal));
            long totalBytes = files.Sum(f => f.Length);

            var report = new StringBuilder();
            report.AppendLine(CultureInfo.InvariantCulture,
                $"{total:N0} registros em {elapsed.TotalSeconds:F1} s ({total / elapsed.TotalSeconds:N0} registros/s) · {files.Length} arquivos retidos ({MemoryProbe.Megabytes(totalBytes)})");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"Memória retida: {string.Join(" → ", memory.Select(MemoryProbe.Megabytes))}");
            LoadSettings.Report("Volume do arquivo de logs", report.ToString());

            await Assert.That(files.Length).IsLessThanOrEqualTo(RetainedFiles);
            await Assert.That(maxFilesSeen).IsLessThanOrEqualTo(RetainedFiles);
            await Assert.That(files.All(f => f.Length <= MaxFileSize)).IsTrue();
            await Assert.That(lastLine).Contains($"registro {total - 1}:").Because("o último registro precisa estar no arquivo mais recente");
            if (memory.Count >= 2)
                await Assert.That(memory[^1] - memory[0]).IsLessThan(32L * 1024 * 1024).Because(report.ToString());
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task LogFile_ContinuousNumbering_NoRecordLostAcrossRotations()
    {
        // Volume menor, conferido linha a linha: com a retenção desligada, todos os arquivos ficam e cada número aparece uma vez
        int total = Math.Min(LoadSettings.LogRecords, 200_000);
        const int Block = 1_500;
        var folder = Path.Combine(Path.GetTempPath(), "tec-obs-volume-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var host = await SampleApiHost.StartAsync(settings:
            [
                ("Observability:Provider", "LogFile"),
                ("Observability:LogFile:FolderPath", folder),
                ("Observability:LogFile:RollingInterval", "None"),
                ("Observability:LogFile:MaxFileSizeBytes", "1048576"),
                ("Observability:LogFile:RetainedFileCount", "0"),
            ]))
            {
                var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Carga.Volume");
                var provider = host.Services.GetRequiredService<LoggerProvider>();
                for (int written = 0; written < total; written += Block)
                {
                    for (int i = written; i < Math.Min(written + Block, total); i++)
                        VolumeLog.Record(logger, i, "texto com quebra\r\nde linha e acentuação: ção");
                    provider.ForceFlush(30_000);
                }
            }

            var seen = new bool[total];
            int duplicates = 0, records = 0;
            foreach (var file in Directory.GetFiles(folder, "*.txt"))
            {
                foreach (var line in File.ReadLines(file))
                {
                    var marker = line.IndexOf("registro ", StringComparison.Ordinal);
                    if (line.StartsWith("    ", StringComparison.Ordinal) || marker < 0)
                        continue;
                    var number = int.Parse(line.AsSpan(marker + 9, line.IndexOf(':', marker) - marker - 9), CultureInfo.InvariantCulture);
                    records++;
                    if (seen[number])
                        duplicates++;
                    seen[number] = true;
                }
            }

            await Assert.That(duplicates).IsEqualTo(0);
            await Assert.That(records).IsEqualTo(total);
            await Assert.That(seen.All(s => s)).IsTrue();
            await Assert.That(Directory.GetFiles(folder, "*.txt").All(f => new FileInfo(f).Length <= 1_048_576)).IsTrue();
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}

internal static partial class VolumeLog
{
    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "registro {Numero}: {Texto}")]
    public static partial void Record(ILogger logger, int numero, string texto);
}
