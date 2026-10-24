namespace TEC.Observability.Tests;

/// <summary>Categorias de teste deste projeto (seleção no CI por <c>--treenode-filter "/*/*/*/*[Category=...]"</c>).</summary>
internal static class TestCategories
{
    /// <summary>Integração com o Application Insights de testes (pula com o motivo quando o ambiente não existe).</summary>
    public const string Integration = "Integracao";
}
