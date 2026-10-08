namespace TEC.Observability.LoadTests.Infrastructure;

/// <summary>Categorias de teste deste projeto (seleção no CI por <c>--treenode-filter "/*/*/*/*[Category=...]"</c>).</summary>
public static class TestCategories
{
    /// <summary>Concorrência e fumaça de carga (segundos): rodam no CI a cada pull request.</summary>
    public const string LoadCi = "Carga-CI";

    /// <summary>Carga sustentada, soak e volume: [Explicit], só rodam quando selecionados (performance.yml e release).</summary>
    public const string LoadHeavy = "Carga-Pesada";
}
