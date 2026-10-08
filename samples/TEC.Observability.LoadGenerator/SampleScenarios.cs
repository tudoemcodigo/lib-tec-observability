using System.Globalization;
using System.Net.Http.Json;

namespace TEC.Observability.LoadGenerator;

/// <summary>Cenários da API de exemplo (TEC.Observability.SampleApi), com os pesos da mistura padrão.</summary>
public static class SampleScenarios
{
    /// <summary>Marcador dos valores secretos (o mesmo da API de exemplo): a telemetria nunca pode contê-lo.</summary>
    public const string SecretMarker = "segredo-";

    /// <summary>Todos os cenários.</summary>
    public static IReadOnlyList<LoadScenario> All { get; } =
    [
        new("consultar", 30, r => WithCorrelation(Get($"/pedidos/{r.Next(1, 100_000)}"), r)),
        new("criar", 15, r => WithCorrelation(Post("/pedidos", new { cliente = $"Cliente {r.Next(1, 1000)}", itens = r.Next(1, 100) }), r), ExpectedStatus: 201),
        new("externo", 10, r => WithCorrelation(Get($"/pedidos/{r.Next(1, 100_000)}/externo"), r)),
        new("busca", 10, r => Get(string.Create(CultureInfo.InvariantCulture, $"/busca?token={SecretMarker}{r.Next()}&q=pedido-{r.Next(1000)}"))),
        new("correlacao-insegura", 5, r =>
        {
            // Valor que tentaria injetar marcação no log e no cabeçalho de resposta: a biblioteca troca por um id gerado
            var request = Get($"/pedidos/{r.Next(1, 100_000)}");
            request.Headers.TryAddWithoutValidation("X-Correlation-ID", $"<script>{r.Next()}</script> {new string('x', r.Next(0, 300))}");
            return request;
        }),
        new("invalido", 5, _ => Post("/pedidos", new { cliente = "", itens = 0 }), ExpectedStatus: 400),
        new("erro", 5, _ => Get("/erro"), ExpectedStatus: 500),
        new("live", 10, _ => Get("/health/live")),
        new("ready", 10, _ => Get("/health/ready")),
    ];

    /// <summary>
    /// Seleciona cenários por nome, com peso opcional: <c>"consultar,criar:50,ready:5"</c>. Vazio: mistura padrão.
    /// </summary>
    public static IReadOnlyList<LoadScenario> Parse(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
            return All;

        var selected = new List<LoadScenario>();
        foreach (var item in selection.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':', 2);
            var scenario = All.FirstOrDefault(s => s.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Cenário desconhecido: {parts[0]}. Disponíveis: {string.Join(", ", All.Select(s => s.Name))}.");

            int weight = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : Math.Max(scenario.Weight, 1);
            selected.Add(scenario with { Weight = weight });
        }

        return selected;
    }

    private static HttpRequestMessage WithCorrelation(HttpRequestMessage request, Random random)
    {
        request.Headers.Add("X-Correlation-ID", string.Create(CultureInfo.InvariantCulture, $"carga-{random.Next():x8}"));
        return request;
    }

    private static HttpRequestMessage Get(string path) => new(HttpMethod.Get, path);

    private static HttpRequestMessage Post<T>(string path, T body) => new(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
}
