namespace TEC.Observability.Internal;

/// <summary>
/// Cópia redigida de uma exceção, para os exportadores de log: mensagem, stack trace, <see cref="ToString"/> e as exceções
/// internas passam pela redação de texto livre (<see cref="SensitiveText"/>). O tipo original aparece em
/// <see cref="OriginalTypeName"/> e no início de <see cref="ToString"/> (o tipo .NET da instância é este, porque não há como
/// redigir a exceção original no lugar). <see cref="Exception.Data"/> não é copiado (pode trazer qualquer valor).
/// </summary>
/// <remarks>
/// Só é criada quando há algo a redigir: exceção sem dado sensível segue como veio, com o tipo original.
/// </remarks>
internal sealed class RedactedException : Exception
{
    private readonly string? _stackTrace;
    private readonly string _text;

    private RedactedException(Exception original, string text)
        : base(SensitiveText.Redact(original.Message), original.InnerException is { } inner ? Create(inner) : null)
    {
        OriginalTypeName = original.GetType().FullName ?? original.GetType().Name;
        HResult = original.HResult;
        _stackTrace = original.StackTrace is { } stack ? SensitiveText.Redact(stack) : null;
        _text = text;
    }

    /// <summary>Nome completo do tipo da exceção original (ex.: <c>Microsoft.Data.SqlClient.SqlException</c>).</summary>
    public string OriginalTypeName { get; }

    /// <inheritdoc />
    public override string? StackTrace => _stackTrace;

    /// <inheritdoc />
    public override string ToString() => _text;

    /// <summary>A própria exceção, quando não há nada a redigir; senão, a cópia redigida (com as internas também redigidas).</summary>
    public static Exception RedactIfNeeded(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is RedactedException)
            return exception;

        // ToString inclui tipo, mensagem, stack trace e as exceções internas: se ele não muda, nada muda.
        var text = exception.ToString();
        var redacted = SensitiveText.Redact(text);
        return ReferenceEquals(text, redacted) ? exception : new RedactedException(exception, redacted);
    }

    private static RedactedException Create(Exception exception) =>
        exception as RedactedException ?? new RedactedException(exception, SensitiveText.Redact(exception.ToString()));
}
