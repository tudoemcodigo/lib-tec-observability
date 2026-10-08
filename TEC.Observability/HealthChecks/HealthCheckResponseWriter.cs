using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TEC.Observability.Configuration;
using TEC.Observability.Internal;

namespace TEC.Observability.HealthChecks;

/// <summary>
/// Saída JSON padronizada dos endpoints de health check: status geral, identificação do serviço, duração total e, para cada
/// verificação, status, descrição, duração, tags, dados e exceção.
/// </summary>
/// <example>
/// <code>
/// {
///   "status": "Unhealthy",
///   "service": "pedidos-api", "version": "1.4.2", "environment": "production",
///   "timestamp": "2026-10-01T12:00:00.0000000+00:00",
///   "totalDurationMs": 41.7,
///   "checks": [
///     { "name": "PrimaryDb", "status": "Unhealthy", "description": "Banco de dados inacessível.",
///       "durationMs": 40.2, "tags": ["ready", "database"], "data": {}, "exception": { "type": "SqlException" } }
///   ]
/// }
/// </code>
/// </example>
public static class HealthCheckResponseWriter
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    /// <summary>Tamanho máximo de cada texto vindo das verificações (descrição, mensagem, dados); o excedente é cortado.</summary>
    internal const int MaxTextLength = 1024;

    /// <summary>Escreve o relatório como <c>application/json</c>. Compatível com <c>HealthCheckOptions.ResponseWriter</c>.</summary>
    /// <param name="context">Requisição em atendimento.</param>
    /// <param name="report">Relatório das verificações.</param>
    /// <returns>Tarefa que termina quando o corpo foi escrito.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> ou <paramref name="report"/> nulo.</exception>
    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        var time = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
        return WriteBodyAsync(context, Serialize(report, GetOptions(context), time.GetUtcNow()));
    }

    /// <summary>Opções efetivas da biblioteca, ou as padrão (sem detalhes) fora do <c>AddTecObservability</c>.</summary>
    internal static ObservabilityOptions GetOptions(HttpContext context) =>
        context.RequestServices.GetService<IOptions<ObservabilityOptions>>()?.Value ?? new ObservabilityOptions();

    internal static async Task WriteBodyAsync(HttpContext context, byte[] body)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    internal static byte[] Serialize(HealthReport report, ObservabilityOptions options, DateTimeOffset timestamp)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("status", report.Status.ToString());

            // Sem decisão explícita (ou fora de AddTecObservability, onde o ambiente é desconhecido), sem detalhes.
            if (options.HealthChecks?.ExposeDetails == true)
            {
                json.WriteString("service", options.ServiceName);
                json.WriteString("version", options.ServiceVersion);
                json.WriteString("environment", options.Environment);
                json.WriteString("timestamp", timestamp);
                json.WriteNumber("totalDurationMs", Milliseconds(report.TotalDuration));

                json.WriteStartArray("checks");
                foreach (var (name, entry) in report.Entries)
                    WriteEntry(json, name, entry, options.HealthChecks.ExposeExceptionDetails);
                json.WriteEndArray();
            }
            else
            {
                json.WriteString("timestamp", timestamp);
            }

            json.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteEntry(Utf8JsonWriter json, string name, HealthReportEntry entry, bool exposeExceptionDetails)
    {
        json.WriteStartObject();
        json.WriteString("name", name);
        json.WriteString("status", entry.Status.ToString());

        // Quando um check de terceiros lança, o framework usa a mensagem da exceção como descrição: ela segue a mesma regra de exposição.
        var descriptionIsExceptionMessage = entry.Exception is not null && entry.Description == entry.Exception.Message;
        json.WriteString("description", descriptionIsExceptionMessage && !exposeExceptionDetails ? null : Text(entry.Description));

        json.WriteNumber("durationMs", Milliseconds(entry.Duration));

        json.WriteStartArray("tags");
        foreach (var tag in entry.Tags)
            json.WriteStringValue(tag);
        json.WriteEndArray();

        json.WriteStartObject("data");
        foreach (var (key, value) in entry.Data)
        {
            // Mesmo com detalhes ligados, valores de nome sensível (senha, token, connection string...) não saem.
            json.WritePropertyName(key);
            WriteValue(json, value is null or bool || !SensitiveKeys.IsSensitive(key) ? value : SensitiveText.RedactedValue);
        }
        json.WriteEndObject();

        if (entry.Exception is { } exception)
        {
            json.WriteStartObject("exception");
            json.WriteString("type", exception.GetType().Name);
            if (exposeExceptionDetails)
                json.WriteString("message", Text(exception.Message));
            json.WriteEndObject();
        }

        json.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter json, object? value)
    {
        switch (value)
        {
            case null: json.WriteNullValue(); break;
            case bool b: json.WriteBooleanValue(b); break;
            case int i: json.WriteNumberValue(i); break;
            case long l: json.WriteNumberValue(l); break;
            case double d when double.IsFinite(d): json.WriteNumberValue(d); break;
            case decimal m: json.WriteNumberValue(m); break;
            case DateTimeOffset dto: json.WriteStringValue(dto); break;
            case DateTime dt: json.WriteStringValue(dt); break;
            case TimeSpan ts: json.WriteNumberValue(Milliseconds(ts)); break;
            default: json.WriteStringValue(Text(Convert.ToString(value, CultureInfo.InvariantCulture))); break;
        }
    }

    /// <summary>Texto de terceiros com dados sensíveis redigidos (URL com token, connection string) e no máximo <see cref="MaxTextLength"/> caracteres.</summary>
    internal static string? Text(string? value)
    {
        if (value is null)
            return null;

        var text = value.Length > MaxTextLength ? string.Concat(value.AsSpan(0, MaxTextLength), "…") : value;
        return SensitiveText.Redact(text);
    }

    private static double Milliseconds(TimeSpan duration) => Math.Round(duration.TotalMilliseconds, 1);
}
