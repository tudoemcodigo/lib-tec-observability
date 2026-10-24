using System.Diagnostics;

namespace TEC.Observability.LoadTests.Infrastructure;

/// <summary>Medições de memória e recursos do processo.</summary>
public static class MemoryProbe
{
    /// <summary>
    /// Memória gerenciada <b>retida</b> (após coleta completa): o que continua vivo, sem o lixo ainda não coletado.
    /// É a métrica certa para "memória constante" em streaming e para vazamentos.
    /// </summary>
    public static long RetainedBytes()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(forceFullCollection: false);
    }

    /// <summary>Handles abertos pelo processo (detecta vazamento de recursos nativos, como sockets, arquivos e timers não descartados).</summary>
    public static int HandleCount()
    {
        using var process = Process.GetCurrentProcess();
        return process.HandleCount;
    }

    /// <summary>Formata bytes em MB.</summary>
    public static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):F1} MB";
}
