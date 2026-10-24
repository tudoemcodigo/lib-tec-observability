using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FsCheck;
using FsCheck.Fluent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using TEC.Observability.Configuration;
using TEC.Observability.Exporters;
using TEC.Observability.HealthChecks;
using TEC.Observability.Tracing;

namespace TEC.Observability.Tests.Security.Fuzzing;

/// <summary>
/// Testes de propriedade (FsCheck) com entradas hostis geradas aleatoriamente para tudo o que recebe valor de fora: correlation id,
/// query string e URLs de spans, caminhos de cofre, rotas, hosts do Baggage, nomes de arquivo de log, JSON de health e
/// configuração. Cada propriedade roda centenas de casos; em caso de falha, a mensagem traz o contraexemplo e a semente.
/// </summary>
public partial class FuzzingTests
{
    /// <summary>Casos por propriedade no fuzzing estendido (TEC_CARGA_FUZZ_CASOS, padrão 20 mil).</summary>
    private static int ExtendedCases =>
        int.TryParse(Environment.GetEnvironmentVariable("TEC_CARGA_FUZZ_CASOS"), NumberStyles.None, CultureInfo.InvariantCulture, out var cases) && cases > 0
            ? cases
            : 20_000;

    /// <summary>Marcador secreto: não pode aparecer em nenhuma saída redigida.</summary>
    private static readonly string Secret = "SEGREDO" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    // ---------- Correlation id ----------

    [Test]
    public void Correlation_id_is_only_accepted_with_safe_characters() => CorrelationIdProperty(Hostile.Config(500));

    private static void CorrelationIdProperty(Config config) =>
        Prop.ForAll(Gen.OneOf(Hostile.AnyText, Hostile.AsciiText, Gen.Constant(new string('a', 128)), Gen.Constant(new string('a', 129))).ToArbitrary(), value =>
        {
            var expected = SafeCorrelationId().IsMatch(value);
            if (CorrelationIdMiddleware.IsSafe(value) != expected)
                throw new InvalidOperationException($"IsSafe({Hostile.Show(value)}) deveria ser {expected}");
        }).Check(config);

    [Test]
    public async Task Hostile_correlation_id_never_returns_in_header_nor_breaks_request()
    {
        await using var app = await TestApp.StartAsync(endpoints: a => a.MapGet("/eco", () => CorrelationId.Current ?? string.Empty));
        var client = app.GetTestClient();
        await CorrelationEndToEndAsync(client, 300);
    }

    private static async Task CorrelationEndToEndAsync(HttpClient client, int cases)
    {
        // Cabeçalhos com quebra de linha são recusados pelo próprio HttpClient; o resto chega ao middleware como veio
        var values = Gen.OneOf(Hostile.Text, Hostile.AsciiText).Where(v => v.IndexOfAny(['\r', '\n', '\0']) < 0).Sample(cases, size: 60);
        foreach (var value in values)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/eco");
            if (!request.Headers.TryAddWithoutValidation(CorrelationId.HeaderName, value))
                continue;

            using var response = await client.SendAsync(request);
            var echoed = response.Headers.TryGetValues(CorrelationId.HeaderName, out var header) ? header.Single() : null;
            var body = await response.Content.ReadAsStringAsync();

            var accepted = CorrelationIdMiddleware.IsSafe(value) && echoed == value;
            var replaced = !CorrelationIdMiddleware.IsSafe(value) && echoed is not null && GeneratedId().IsMatch(echoed);
            if (response.StatusCode != HttpStatusCode.OK || !(accepted || replaced) || body != echoed)
                throw new InvalidOperationException($"{Hostile.Show(value)}: HTTP {(int)response.StatusCode}, cabeçalho {Hostile.Show(echoed)}, corpo {Hostile.Show(body)}");
        }
    }

    // ---------- Redação de query string ----------

    [Test]
    public void Redacted_query_string_follows_instrumentation_format() => RedactQueryModelProperty(Hostile.Config(500));

    private static void RedactQueryModelProperty(Config config) =>
        Prop.ForAll(Hostile.AnyText.ToArbitrary(), query =>
        {
            var redacted = TelemetryRedaction.RedactQuery(query);
            var prefix = query.StartsWith('?') ? 1 : 0;
            var expected = query.Length == prefix
                ? query
                : query[..prefix] + string.Join('&', query[prefix..].Split('&').Select(part =>
                    part.IndexOf('=', StringComparison.Ordinal) is var i and >= 0 ? part[..i] + "=Redacted" : part is "" or "*" ? part : "Redacted"));
            if (redacted != expected || TelemetryRedaction.RedactQuery(redacted) != redacted)
                throw new InvalidOperationException($"{Hostile.Show(query)} → {Hostile.Show(redacted)} (esperado {Hostile.Show(expected)})");
        }).Check(config);

    [Test]
    public void Redacted_query_string_never_exposes_values() => RedactQuerySecretProperty(Hostile.Config(500));

    private static void RedactQuerySecretProperty(Config config)
    {
        var parameter =
            from key in Hostile.Text.Select(k => k.Replace("&", "", StringComparison.Ordinal).Replace("=", "", StringComparison.Ordinal))
            from before in Hostile.Text
            from after in Hostile.Text
            select key + "=" + (before + Secret + after).Replace("&", "", StringComparison.Ordinal);
        var queries = parameter.NonEmptyListOf().Select(p => "?" + string.Join('&', p));

        Prop.ForAll(queries.ToArbitrary(), query =>
        {
            var redacted = TelemetryRedaction.RedactQuery(query);
            if (redacted.Contains(Secret, StringComparison.Ordinal))
                throw new InvalidOperationException($"{Hostile.Show(query)} → {Hostile.Show(redacted)}");
        }).Check(config);
    }

    // ---------- Cofres de segredos ----------

    [Test]
    public void Redacted_vault_path_never_exposes_name_or_version() => SecretStorePathProperty(Hostile.Config(500));

    private static void SecretStorePathProperty(Config config)
    {
        static string NoSlash(string s) => s.Replace("/", "", StringComparison.Ordinal);
        var cases =
            from type in Hostile.NonEmptyText.Select(NoSlash).Where(t => t.Length > 0)
            from name in Hostile.Text.Select(n => NoSlash(n) + Secret)
            from version in Gen.OneOf(Gen.Constant(""), Hostile.Text.Select(v => "/" + NoSlash(v) + Secret))
            from leading in Gen.Elements("/", "//", "")
            select (Type: type, Path: leading + type + "/" + name + version);

        Prop.ForAll(cases.ToArbitrary(), c =>
        {
            var redacted = TelemetryRedaction.RedactSecretStorePath(c.Path);
            if (redacted != $"/{c.Type}/***" || TelemetryRedaction.RedactSecretStorePath(redacted) != redacted)
                throw new InvalidOperationException($"{Hostile.Show(c.Path)} → {Hostile.Show(redacted)}");
        }).Check(config);

        // Qualquer texto: nunca lança e a redação é idempotente
        Prop.ForAll(Hostile.AnyText.ToArbitrary(), path =>
        {
            var once = TelemetryRedaction.RedactSecretStorePath(path);
            if (TelemetryRedaction.RedactSecretStorePath(once) != once)
                throw new InvalidOperationException($"{Hostile.Show(path)} → {Hostile.Show(once)}");
        }).Check(config);
    }

    private static readonly ActivitySource AzureHttp = new("Azure.Core.Http");
    private static readonly ActivitySource KeyVaultSecrets = new("Azure.Security.KeyVault.Secrets");
    private static readonly ActivityListener AzureListener = CreateAzureListener();

    [Test]
    public void Vault_span_with_hostile_url_never_exposes_secret() => SecretStoreSpanProperty(Hostile.Config(400));

    private static void SecretStoreSpanProperty(Config config)
    {
        _ = AzureListener;
        var cases =
            from host in Gen.Elements("cofre.vault.azure.net", "COFRE.VAULT.AZURE.NET", "kv.vault.azure.cn", "kv.vault.usgovcloudapi.net",
                "hsm.managedhsm.azure.net", "Cofre.Vault.Azure.Net")
            from resource in Gen.Elements("secrets", "keys", "certificates", "deletedsecrets")
            // Nomes de segredo, chave e certificado do Key Vault só aceitam letras, dígitos e '-' (e o Azure SDK escapa o nome na URL)
            from prefix in Gen.Elements("a", "Z", "0", "9", "-", "conexao", "BANCO", "--").ListOf().Select(string.Concat)
            from version in Gen.Elements("", "/0123456789abcdef", "/" + Secret)
            from legacy in Gen.Elements(true, false)
            select (Host: host, Resource: resource, Name: prefix + Secret, Version: version, Legacy: legacy);

        Prop.ForAll(cases.ToArbitrary(), c =>
        {
            var path = $"/{c.Resource}/{c.Name}{c.Version}";
            using var activity = AzureHttp.StartActivity("GET", ActivityKind.Client)!;
            activity.SetTag(c.Legacy ? TelemetryRedaction.HttpUrl : TelemetryRedaction.UrlFull, $"https://{c.Host}{path}?api-version=7.5&filtro={Secret}");
            activity.SetTag(TelemetryRedaction.UrlPath, path);
            activity.SetTag(TelemetryRedaction.HttpTarget, path + "?api-version=7.5");
            activity.SetTag(TelemetryRedaction.UrlQuery, "api-version=7.5&filtro=" + Secret);
            activity.SetTag(TelemetryRedaction.ServerAddress, c.Host);
            // Como uma instrumentação faria: o caminho no nome de exibição vem do Uri (escapado), ou só o método se a URL não abre
            activity.DisplayName = Uri.TryCreate($"https://{c.Host}{path}", UriKind.Absolute, out var parsed) ? "GET " + parsed.AbsolutePath : "GET";

            var processor = new SpanRedactionProcessor();
            processor.OnStart(activity);
            processor.OnEnd(activity);

            var leaked = activity.TagObjects.Where(t => t.Value is string text && text.Contains(Secret, StringComparison.OrdinalIgnoreCase)).Select(t => t.Key).ToList();
            if (activity.DisplayName.Contains(Secret, StringComparison.OrdinalIgnoreCase))
                leaked.Add("DisplayName");
            if (leaked.Count > 0)
                throw new InvalidOperationException($"{Hostile.Show(path)} em {c.Host}: segredo em {string.Join(", ", leaked)}");
        }).Check(config);

        Prop.ForAll(Hostile.AnyText.ToArbitrary(), name =>
        {
            using var activity = KeyVaultSecrets.StartActivity("SecretClient.GetSecret", ActivityKind.Internal)!;
            activity.SetTag("az.keyvault.secret.name", name + Secret);
            activity.SetTag("az.keyvault.secret.version", Secret);
            SpanRedactionProcessor.Redact(activity);
            if (activity.TagObjects.Any(t => t.Value is string text && text.Contains(Secret, StringComparison.Ordinal)))
                throw new InvalidOperationException($"az.keyvault.* com {Hostile.Show(name)} não foi redigido");
        }).Check(config);
    }

    private static ActivityListener CreateAzureListener()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source == AzureHttp || source == KeyVaultSecrets,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    // ---------- Arquivo de logs ----------

    [Test]
    public void Log_file_name_never_escapes_folder() => LogFileNameProperty(Hostile.Config(500));

    private static void LogFileNameProperty(Config config)
    {
        var folder = Path.Combine(Path.GetTempPath(), "tec-obs-fuzz");
        Prop.ForAll(Hostile.AnyText.ToArbitrary(), name =>
        {
            var safe = LogFileRecordExporter.SafeFileName(name);
            var full = Path.GetFullPath(Path.Combine(folder, safe + "-20261006_001.txt"));
            var problems = new List<string>();
            if (safe.Length == 0)
                problems.Add("vazio");
            if (safe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || safe.IndexOfAny(['/', '\\', '*', '?']) >= 0)
                problems.Add("caractere inválido");
            if (safe.StartsWith('.') || safe.EndsWith('.'))
                problems.Add("ponto nas pontas");
            if (!IsDirectlyInside(folder, full))
                problems.Add($"fora da pasta: {full}");
            if (problems.Count > 0)
                throw new InvalidOperationException($"{Hostile.Show(name)} → {Hostile.Show(safe)}: {string.Join(", ", problems)}");
        }).Check(config);
    }

    [Test]
    public void Retention_only_recognizes_own_service_files() => LogFileOwnershipProperty(Hostile.Config(500));

    private static void LogFileOwnershipProperty(Config config)
    {
        string[] pieces = ["pedidos", "api", "-", "_", "1", "001", "20261006", "2026100614", "a", "x", ".", ".txt", ".TXT", ".log", "-api", "Pedidos"];
        var cases =
            from service in Gen.Elements("pedidos", "pedidos-api", "Pedidos", "api_1", "a.b")
            from suffix in Gen.Elements(pieces).ListOf().Select(string.Concat)
            from prefix in Gen.Elements("", "", "", "outro-", "x")
            select (Service: service, File: prefix + service + suffix);

        Prop.ForAll(cases.ToArbitrary(), c =>
        {
            var exporter = new LogFileRecordExporter(new LogFileExporterSettings { FileName = c.Service }, c.Service, TimeProvider.System);
            var expected = Regex.IsMatch(c.File, $@"^{Regex.Escape(c.Service)}(-[0-9]+)?(_[0-9]+)?\.txt\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (exporter.IsOwnFile(c.File) != expected)
                throw new InvalidOperationException($"IsOwnFile('{c.File}') do serviço '{c.Service}' deveria ser {expected}");
        }).Check(config);
    }

    // ---------- Baggage e rotas ----------

    [Test]
    public void Baggage_only_goes_to_hosts_allowed_by_exact_name_or_suffix() => BaggagePolicyProperty(Hostile.Config(500));

    private static void BaggagePolicyProperty(Config config)
    {
        var label = Gen.Elements("interno", "INTERNO", "svc", "cluster", "local", "evil", "com", "a", "xinterno", "interno-x", "", "*", "\u0131", "\u0130", "\u212A");
        var host = label.NonEmptyListOf().Select(parts => string.Join('.', parts));
        var cases =
            // Padrão exato: um nome sem curinga ou "*" sozinho ("*.x" é padrão de sufixo, coberto pela outra política)
            from exact in Gen.OneOf(host.Where(h => !h.Contains('*', StringComparison.Ordinal)), Gen.Constant("*"))
            from suffix in host.Where(s => s.Length > 0 && !s.Contains('*', StringComparison.Ordinal))
            from candidate in Gen.OneOf(host, Gen.Constant(exact), host.Select(h => h + "." + suffix), host.Select(h => h + suffix))
            select (Exact: exact, Suffix: suffix, Host: candidate);

        Prop.ForAll(cases.ToArbitrary(), c =>
        {
            var exactPolicy = new BaggageHostPolicy([c.Exact]);
            var suffixPolicy = new BaggageHostPolicy(["*." + c.Suffix]);
            var expectedExact = c.Exact == "*" || string.Equals(c.Host, c.Exact, StringComparison.OrdinalIgnoreCase);
            var expectedSuffix = c.Host.EndsWith("." + c.Suffix, StringComparison.OrdinalIgnoreCase) && c.Host.Length > c.Suffix.Length + 1;
            if (exactPolicy.Allows(c.Host) != expectedExact || suffixPolicy.Allows(c.Host) != expectedSuffix)
                throw new InvalidOperationException($"host {Hostile.Show(c.Host)}, exato {Hostile.Show(c.Exact)}, sufixo {Hostile.Show(c.Suffix)}");
        }).Check(config);
    }

    [Test]
    public void Ignored_routes_are_compared_by_segment() => PathFilterProperty(Hostile.Config(500));

    private static void PathFilterProperty(Config config)
    {
        var filter = new RequestPathFilter(["/health", "swagger/"]);
        var segment = Gen.OneOf(Hostile.Text.Select(t => t.Replace("/", "", StringComparison.Ordinal)), Gen.Elements("health", "HEALTH", "healthcare", "swagger", "Swagger", "health%2Flive", "health."));
        var paths = segment.NonEmptyListOf().Select(parts => "/" + string.Join('/', parts));

        Prop.ForAll(paths.ToArbitrary(), path =>
        {
            static bool Under(string path, string prefix) =>
                string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
            var expected = !Under(path, "/health") && !Under(path, "/swagger");
            if (filter.ShouldTrace(new PathString(path)) != expected)
                throw new InvalidOperationException($"ShouldTrace({Hostile.Show(path)}) deveria ser {expected}");
        }).Check(config);
    }

    // ---------- JSON de health ----------

    [Test]
    public void Health_json_is_always_valid_and_respects_exposure() => HealthJsonProperty(Hostile.Config(400));

    private static void HealthJsonProperty(Config config)
    {
        var entry =
            from description in Hostile.AnyText
            from status in Gen.Elements(HealthStatus.Healthy, HealthStatus.Degraded, HealthStatus.Unhealthy)
            from throws in Gen.Elements(true, false)
            from descriptionIsMessage in Gen.Elements(true, false)
            from dataKey in Hostile.AnyText
            from dataValue in Gen.OneOf(Hostile.AnyText.Select(s => (object?)s), Gen.Constant((object?)double.NaN), Gen.Constant((object?)null),
                Gen.Constant((object?)TimeSpan.MaxValue), Gen.Constant((object?)decimal.MaxValue), Gen.Constant((object?)new Uri("https://h/?token=x")))
            select (Status: status, Exception: throws ? new InvalidOperationException("falha " + Secret + description) : null,
                Description: description, DescriptionIsMessage: descriptionIsMessage, DataKey: dataKey, DataValue: dataValue);
        var cases =
            from entries in Gen.Zip(Hostile.AnyText, entry).ListOf()
            from details in Gen.Elements(true, false)
            from exceptionDetails in Gen.Elements(true, false)
            select (Entries: entries, Details: details, ExceptionDetails: exceptionDetails);

        Prop.ForAll(cases.ToArbitrary(), c =>
        {
            var entries = new Dictionary<string, HealthReportEntry>();
            foreach (var (name, e) in c.Entries)
            {
                var description = e.Exception is not null && e.DescriptionIsMessage ? e.Exception.Message : e.Description;
                var data = new Dictionary<string, object> { [e.DataKey] = e.DataValue! };
                entries.TryAdd(name, new HealthReportEntry(e.Status, description, TimeSpan.FromMilliseconds(1), e.Exception, data, ["ready"]));
            }

            var options = new ObservabilityOptions { ServiceName = "svc", ServiceVersion = "1", Environment = "x" };
            options.HealthChecks.ExposeDetails = c.Details;
            options.HealthChecks.ExposeExceptionDetails = c.ExceptionDetails;
            var report = new HealthReport(entries, HealthStatus.Degraded, TimeSpan.FromMilliseconds(5));

            byte[] json;
            try
            {
                json = HealthCheckResponseWriter.Serialize(report, options, DateTimeOffset.UnixEpoch);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Serialize lançou {ex.GetType().Name}: {ex.Message} (nomes {string.Join(", ", entries.Keys.Select(Hostile.Show))})", ex);
            }

            using var document = JsonDocument.Parse(json);
            var properties = document.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
            if (!c.Details && !properties.SequenceEqual(["status", "timestamp"]))
                throw new InvalidOperationException($"Sem detalhes, o JSON trouxe {string.Join(", ", properties)}");
            if (!c.ExceptionDetails && Encoding.UTF8.GetString(json).Contains(Secret, StringComparison.Ordinal))
                throw new InvalidOperationException("Mensagem de exceção exposta sem ExposeExceptionDetails");
        }).Check(config);
    }

    // ---------- Configuração ----------

    [Test]
    public void Hostile_configuration_is_rejected_with_message_and_no_unexpected_exception() => ConfigurationProperty(Hostile.Config(300));

    private static void ConfigurationProperty(Config config)
    {
        var cases =
            from provider in Gen.OneOf(Hostile.Text, Gen.Elements("Otlp", "LogFile, Console", "None", "Azure"))
            from path in Hostile.Text
            from host in Hostile.Text
            from headerName in Hostile.Text
            from headerValue in Hostile.AnyText.Select(v => v + Secret)
            from fileName in Hostile.Text
            from folder in Hostile.Text
            from endpoint in Gen.OneOf(Hostile.Text, Gen.Elements("http://coletor:4318", $"https://u:{Secret}@coletor", "file:///etc/passwd", $"http://coletor:4318/?chave={Secret}"))
            select new Dictionary<string, string?>
            {
                ["Observability:ServiceName"] = "svc",
                ["Observability:ServiceVersion"] = "1",
                ["Observability:Environment"] = "x",
                ["Observability:Provider"] = provider,
                ["Observability:IgnoredPaths:0"] = path,
                ["Observability:BaggageAllowedHosts:0"] = host,
                [$"Observability:Otlp:Headers:{headerName.Replace(":", "", StringComparison.Ordinal)}"] = headerValue,
                ["Observability:Otlp:Endpoint"] = endpoint,
                ["Observability:LogFile:FileName"] = fileName,
                ["Observability:LogFile:FolderPath"] = folder,
            };

        Prop.ForAll(cases.ToArbitrary(), settings =>
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var options = new ObservabilityOptions();
            try
            {
                ObservabilityOptionsSetup.Bind(configuration.GetSection(ObservabilityOptions.SectionName), options);
            }
            catch (InvalidOperationException)
            {
                return; // valor que nem converte (ex.: Endpoint que não é URI): o binder recusa na leitura
            }

            var errors = ObservabilityOptionsValidator.GetErrors(options, configuration, isDevelopment: false);
            if (errors.Any(e => e.Contains(Secret, StringComparison.Ordinal)))
                throw new InvalidOperationException("Mensagem de validação repete um valor recebido");

            // Nome de arquivo aceito pela validação nunca escreve fora da pasta
            if (ObservabilityProvider.Includes(options.Provider, ObservabilityProvider.LogFile) && errors.Count == 0 && options.LogFile.FileName.Length > 0)
            {
                var folder = Path.GetFullPath(options.LogFile.FolderPath, AppContext.BaseDirectory);
                var file = Path.GetFullPath(Path.Combine(folder, LogFileRecordExporter.SafeFileName(options.LogFile.FileName) + ".txt"));
                if (!IsDirectlyInside(folder, file))
                    throw new InvalidOperationException($"FileName {Hostile.Show(options.LogFile.FileName)} escreveria em {file}");
            }
        }).Check(config);
    }

    /// <summary>
    /// O arquivo fica diretamente na pasta (sem subpasta nem travessia). Pelo caminho relativo, e não comparando textos de pasta:
    /// com a pasta na raiz (<c>C:\</c>, <c>/</c>), o separador final não pode ser removido.
    /// </summary>
    private static bool IsDirectlyInside(string folder, string file)
    {
        var relative = Path.GetRelativePath(folder, file);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
    }

    // ---------- Fuzzing estendido (sob demanda) ----------

    [Test]
    [Explicit]
    [Category(TestCategories.SecurityHeavy)]
    public async Task Extended_fuzzing_of_all_properties()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var config = Hostile.Config(ExtendedCases);
        CorrelationIdProperty(config);
        RedactQueryModelProperty(config);
        RedactQuerySecretProperty(config);
        SecretStorePathProperty(config);
        SecretStoreSpanProperty(config);
        LogFileNameProperty(config);
        LogFileOwnershipProperty(config);
        BaggagePolicyProperty(config);
        PathFilterProperty(config);
        HealthJsonProperty(config);
        ConfigurationProperty(Hostile.Config(Math.Max(ExtendedCases / 10, 300)));

        await using var app = await TestApp.StartAsync(endpoints: a => a.MapGet("/eco", () => CorrelationId.Current ?? string.Empty));
        await CorrelationEndToEndAsync(app.GetTestClient(), Math.Max(ExtendedCases / 4, 300));

        WriteReport(stopwatch.Elapsed);
    }

    /// <summary>Relatório em Markdown na pasta TEC_CARGA_RELATORIOS (o CI publica no resumo da execução), quando definida.</summary>
    private static void WriteReport(TimeSpan elapsed)
    {
        var body = string.Create(CultureInfo.InvariantCulture,
            $"| Propriedades | Casos por propriedade | Duração |{Environment.NewLine}|---|---|---|{Environment.NewLine}| 11 + ponta a ponta | {ExtendedCases} | {elapsed.TotalSeconds:0.0} s |{Environment.NewLine}");
        Console.WriteLine(body);
        if (Environment.GetEnvironmentVariable("TEC_CARGA_RELATORIOS") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "seguranca.md"), $"### Fuzzing estendido da redação{Environment.NewLine}{Environment.NewLine}{body}");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9\-_.:]{1,128}\z")]
    private static partial Regex SafeCorrelationId();

    [GeneratedRegex(@"^[0-9a-f]{32}\z")]
    private static partial Regex GeneratedId();
}
