namespace TEC.Observability.Tests;

/// <summary>
/// Categorias de teste deste projeto (seleção no CI por <c>--treenode-filter "/*/*/*/*[Category=...]"</c>). O projeto roda
/// inteiro na matriz de unitários (net10.0, net8.0 e sem ICU): o que é lento fica em <see cref="SecurityHeavy"/>, com
/// <c>[Explicit]</c>, e só roda quando selecionado (performance.yml e release).
/// </summary>
internal static class TestCategories
{
    /// <summary>Segurança longa (fuzzing estendido), fora da matriz de unitários.</summary>
    public const string SecurityHeavy = "Seguranca-Pesada";
}
