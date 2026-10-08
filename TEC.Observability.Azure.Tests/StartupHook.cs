/// <summary>
/// Startup hook do .NET (<c>DOTNET_STARTUP_HOOKS</c>): só é carregado no processo filho de
/// <see cref="TEC.Observability.Tests.IsolatedProcess"/>. O runtime exige este nome, fora de namespace (por isso o arquivo próprio).
/// </summary>
#pragma warning disable CA1050 // Exigência do runtime para startup hooks.
internal static class StartupHook
#pragma warning restore CA1050
{
    public static void Initialize() => TEC.Observability.Tests.IsolatedProcess.RunRequestedScenario();
}
