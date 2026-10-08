namespace TEC.Observability.HealthChecks;

/// <summary>Tags padronizadas dos health checks.</summary>
public static class HealthCheckTags
{
    /// <summary>Liveness: só a saúde do processo. Falha aqui faz o orquestrador reiniciar o contêiner.</summary>
    public const string Live = "live";

    /// <summary>Readiness: dependências. Falha aqui tira a instância do balanceamento, sem reiniciar.</summary>
    public const string Ready = "ready";

    /// <summary>Banco de dados.</summary>
    public const string Database = "database";

    /// <summary>SSO / provedor de identidade.</summary>
    public const string Sso = "sso";

    /// <summary>Serviço externo ou microsserviço downstream.</summary>
    public const string External = "external";
}
