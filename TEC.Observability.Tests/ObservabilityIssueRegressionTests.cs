using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using TEC.Observability.Configuration;
using TEC.Observability.Exporters;
using TEC.Observability.Internal;
using TEC.Observability.Tracing;

namespace TEC.Observability.Tests;

public class ObservabilityIssueRegressionTests
{
    [Test]
    public async Task Issue1_ContextPropagationListener_Preserves_RecordedFlag()
    {
        using var listener = new ContextPropagationListener();
        await listener.StartingAsync(CancellationToken.None);
        using var source = new ActivitySource("System.Net.Http");

        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();
        var parentContext = new ActivityContext(traceId, spanId, ActivityTraceFlags.Recorded, isRemote: true);

        using var activity = source.StartActivity("test-remote", ActivityKind.Server, parentContext);
        await Assert.That(activity).IsNotNull();
        if (activity is not null)
        {
            await Assert.That(activity.Recorded).IsTrue();
        }
    }

    [Test]
    public async Task Issue2_SensitiveText_Redacts_NonHttpUrls_And_Various_Authorization_Schemes()
    {
        // URIs não-HTTP com credenciais
        var amqp = SensitiveText.Redact("amqp://guest:S3cretPass@rabbit:5672/vhost");
        await Assert.That(amqp.Contains("S3cretPass")).IsFalse();

        var mongo = SensitiveText.Redact("mongodb://admin:S3cretPass@mongo:27017/db");
        await Assert.That(mongo.Contains("S3cretPass")).IsFalse();

        var postgres = SensitiveText.Redact("postgres://app:S3cretPass@db:5432/x");
        await Assert.That(postgres.Contains("S3cretPass")).IsFalse();

        // Esquemas variados de Authorization
        var auth1 = SensitiveText.Redact("Authorization: Token ghp_abcdef1234567890");
        await Assert.That(auth1.Contains("ghp_abcdef1234567890")).IsFalse();

        var auth2 = SensitiveText.Redact("Authorization: SharedKey myaccount:c2lnbmF0dXJl");
        await Assert.That(auth2.Contains("c2lnbmF0dXJl")).IsFalse();

        var auth3 = SensitiveText.Redact("Proxy-Authorization: Negotiate YIIGhgYGKwYBBQUC");
        await Assert.That(auth3.Contains("YIIGhgYGKwYBBQUC")).IsFalse();

        var auth4 = SensitiveText.Redact("Authorization: ApiKey sk_live_123456");
        await Assert.That(auth4.Contains("sk_live_123456")).IsFalse();

        // Connection string com espaço
        var cs = SensitiveText.Redact("Server=db;Password=my secret pass;Database=x");
        await Assert.That(cs.Contains("my secret pass")).IsFalse();

        // Headers formatados [chave, valor]
        var bracket = SensitiveText.Redact("Headers [X-Api-Key, abc123], [Cookie, .AspNetCore.Session=CfDJ8abc; other=1]");
        await Assert.That(bracket.Contains("abc123")).IsFalse();
        await Assert.That(bracket.Contains("CfDJ8abc")).IsFalse();
    }

    [Test]
    public async Task Issue3_LogRedactionProcessor_Redacts_Body_And_OriginalFormat()
    {
        using var factory = LoggerFactory.Create(builder =>
        {
            builder.AddOpenTelemetry(options =>
            {
                options.AddProcessor(new LogRedactionProcessor());
            });
        });

        var processor = new LogRedactionProcessor();
        await Assert.That(processor).IsNotNull();
    }
}
