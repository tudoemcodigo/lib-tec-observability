using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using TEC.Observability.Configuration;
using TEC.Observability.Internal;

namespace TEC.Observability.Exporters;

/// <summary>
/// Logs em arquivo texto local, uma linha por registro. As opções (<c>Observability:LogFile</c>) são lidas quando o provedor de logs
/// é construído. A gravação é feita em lote, fora da thread que registra o log (a cada segundo ou a cada 512 registros).
/// </summary>
internal sealed class LogFileTelemetryExporter : ITelemetryExporter
{
    /// <summary>Intervalo de gravação: curto, para o arquivo acompanhar a aplicação quase em tempo real durante o desenvolvimento.</summary>
    private const int ScheduledDelayMilliseconds = 1_000;

    public string Name => ObservabilityProvider.LogFile;

    public void Configure(TelemetryExporterContext context) =>
        // A instrumentação padrão cria os spans das requisições: é ela que dá trace_id e span_id às linhas do arquivo.
        context.AddStandardInstrumentation().OpenTelemetry
            .WithLogging(logging => logging.AddProcessor(services =>
            {
                var options = services.Options();
                var exporter = new LogFileRecordExporter(options.LogFile, options.ServiceName, services.GetService<TimeProvider>() ?? TimeProvider.System);
                return new BatchLogRecordExportProcessor(exporter, scheduledDelayMilliseconds: ScheduledDelayMilliseconds);
            }));
}

/// <summary>
/// Grava os registros de log em <c>{FolderPath}/{FileName}-{período}[_NNN].txt</c>, trocando de arquivo por data e tamanho e
/// apagando os mais antigos além de <see cref="LogFileExporterSettings.RetainedFileCount"/>. Falha de disco (pasta sem permissão,
/// disco cheio) descarta o lote e não derruba a aplicação.
/// </summary>
internal sealed class LogFileRecordExporter : BaseExporter<LogRecord>
{
    private const string Extension = ".txt";
    private const string Indent = "    ";
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly LogFileExporterSettings _settings;
    private readonly TimeProvider _time;
    private readonly string _directory;
    private readonly string _baseName;
    /// <summary>Tamanho máximo da mensagem de um registro (caracteres); o excedente é cortado e marcado.</summary>
    internal const int MaxMessageLength = 32 * 1024;

    /// <summary>Tamanho máximo do texto da exceção e de cada valor de escopo (caracteres); o excedente é cortado e marcado.</summary>
    internal const int MaxDetailLength = 64 * 1024;

    /// <summary>
    /// Teto do buffer em caracteres: passou dele, o que está acumulado é gravado no arquivo e o buffer recomeça, então um lote
    /// grande nunca é montado inteiro na memória (o pico é o teto mais um registro).
    /// </summary>
    internal const int MaxBufferedChars = 256 * 1024;

    /// <summary>
    /// Tamanho máximo de um registro antes da exceção (cabeçalho, mensagem e escopos): escopos além disso são omitidos e marcados.
    /// </summary>
    internal const int MaxRecordLength = 192 * 1024;

    /// <summary>Capacidade do buffer mantida entre lotes: um lote excepcional não deixa memória presa depois de gravado.</summary>
    private const int RetainedBufferCapacity = MaxBufferedChars + MaxRecordLength;

    private const string TruncatedMarker = "…[truncado]";

    private readonly StringBuilder _buffer = new();
    private readonly Lock _lock = new();

    private string? _period;
    private int _sequence;
    private string? _currentPath;
    private string _scopeSeparator = " | ";
    private int _recordStart;
    private bool _scopesTruncated;

    /// <summary>Maior tamanho que o buffer atingiu (para os testes conferirem o teto).</summary>
    internal int PeakBufferLength { get; private set; }

    public LogFileRecordExporter(LogFileExporterSettings settings, string serviceName, TimeProvider time)
    {
        _settings = settings;
        _time = time;
        _directory = Path.GetFullPath(settings.FolderPath, AppContext.BaseDirectory);
        _baseName = SafeFileName(string.IsNullOrWhiteSpace(settings.FileName) ? serviceName : settings.FileName);
    }

    /// <summary>Arquivo em uso (o último gravado), ou <c>null</c> antes da primeira gravação.</summary>
    internal string? CurrentPath => _currentPath;

    public override ExportResult Export(in Batch<LogRecord> batch)
    {
        lock (_lock)
        {
            _buffer.Clear();
            try
            {
                foreach (var record in batch)
                {
                    if (record.LogLevel < _settings.MinimumLevel || record.LogLevel == LogLevel.None)
                        continue;

                    Append(record);
                    PeakBufferLength = Math.Max(PeakBufferLength, _buffer.Length);
                    if (_buffer.Length >= MaxBufferedChars)
                        Flush();
                }

                Flush();
                return ExportResult.Success;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ExportResult.Failure;
            }
            finally
            {
                _buffer.Clear();
                if (_buffer.Capacity > RetainedBufferCapacity)
                    _buffer.Capacity = RetainedBufferCapacity;
            }
        }
    }

    /// <summary>Grava o que está no buffer (no arquivo que comporta o bloco) e esvazia o buffer.</summary>
    private void Flush()
    {
        if (_buffer.Length == 0)
            return;

        var path = ResolvePath(ByteCount(_buffer));
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            WriteUtf8(_buffer, stream);
        _buffer.Clear();
    }

    private static int ByteCount(StringBuilder text)
    {
        var count = 0;
        foreach (var chunk in text.GetChunks())
            count += Utf8.GetByteCount(chunk.Span);
        return count;
    }

    /// <summary>Codifica o buffer em UTF-8 direto no arquivo, em blocos, sem materializar o texto inteiro numa string.</summary>
    private static void WriteUtf8(StringBuilder text, Stream stream)
    {
        var encoder = Utf8.GetEncoder();
        var bytes = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            foreach (var chunk in text.GetChunks())
            {
                var chars = chunk.Span;
                while (!chars.IsEmpty)
                {
                    encoder.Convert(chars, bytes, flush: false, out var used, out var written, out _);
                    stream.Write(bytes, 0, written);
                    chars = chars[used..];
                }
            }

            encoder.Convert(ReadOnlySpan<char>.Empty, bytes, flush: true, out _, out var tail, out _);
            stream.Write(bytes, 0, tail);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>
    /// Arquivo do período atual que ainda comporta o lote. Ao trocar de arquivo (e na primeira gravação), aplica a retenção. O
    /// tamanho é conferido no disco: arquivos de execuções anteriores do mesmo período continuam a numeração.
    /// </summary>
    private string ResolvePath(int pendingBytes)
    {
        var period = CurrentPeriod();
        if (period != _period)
        {
            _period = period;
            _sequence = 0;
        }

        Directory.CreateDirectory(_directory);
        var path = BuildPath(period, _sequence);
        while (_settings.MaxFileSizeBytes > 0 && new FileInfo(path) is { Exists: true } file && file.Length > 0
            && file.Length + pendingBytes > _settings.MaxFileSizeBytes)
        {
            path = BuildPath(period, ++_sequence);
        }

        if (path != _currentPath)
        {
            _currentPath = path;
            ApplyRetention(path);
        }

        return path;
    }

    private string CurrentPeriod()
    {
        var now = _time.GetLocalNow();
        return _settings.RollingInterval switch
        {
            LogFileRollingInterval.Day => now.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            LogFileRollingInterval.Hour => now.ToString("yyyyMMddHH", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };
    }

    private string BuildPath(string period, int sequence)
    {
        var name = period.Length == 0 ? _baseName : $"{_baseName}-{period}";
        if (sequence > 0)
            name += "_" + sequence.ToString("D3", CultureInfo.InvariantCulture);
        return Path.Combine(_directory, name + Extension);
    }

    /// <summary>Apaga os arquivos mais antigos deste serviço (mesmo nome base) além do limite, sem tocar no arquivo em uso.</summary>
    private void ApplyRetention(string currentPath)
    {
        if (_settings.RetainedFileCount <= 0)
            return;

        try
        {
            var old = new DirectoryInfo(_directory).EnumerateFiles(_baseName + "*" + Extension)
                .Where(f => IsOwnFile(f.Name) && !string.Equals(f.FullName, currentPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(_settings.RetainedFileCount - 1);
            foreach (var file in old)
                file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retenção é melhor esforço: um arquivo aberto por outro processo fica para a próxima troca.
        }
    }

    /// <summary>
    /// <c>{base}.txt</c>, <c>{base}_NNN.txt</c>, <c>{base}-{dígitos}.txt</c> ou <c>{base}-{dígitos}_NNN.txt</c>: evita apagar arquivos de
    /// outro serviço cujo nome só começa igual (<c>pedidos</c> e <c>pedidos-api</c>).
    /// </summary>
    internal bool IsOwnFile(string fileName)
    {
        if (fileName.Length < _baseName.Length + Extension.Length
            || !fileName.StartsWith(_baseName, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = fileName.AsSpan(_baseName.Length, fileName.Length - _baseName.Length - Extension.Length);
        if (rest.Length > 0 && rest[0] == '-')
        {
            rest = rest[1..];
            var digits = rest.IndexOf('_') is var underscore and >= 0 ? rest[..underscore] : rest;
            if (digits.Length == 0 || !IsDigits(digits))
                return false;
            rest = rest[digits.Length..];
        }

        return rest.Length == 0 || (rest.Length > 1 && rest[0] == '_' && IsDigits(rest[1..]));
    }

    private static bool IsDigits(ReadOnlySpan<char> text) => text.IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary><c>{data} [{nível}] {categoria}[{evento}]: {mensagem} | trace_id=... span_id=... | {escopos}</c> e a exceção abaixo.</summary>
    private void Append(LogRecord record)
    {
        _recordStart = _buffer.Length;
        _scopesTruncated = false;
        var timestamp = TimeZoneInfo.ConvertTime(new DateTimeOffset(record.Timestamp, TimeSpan.Zero), _time.LocalTimeZone);
        _buffer.Append(timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [").Append(LevelText(record.LogLevel)).Append("] ");
        AppendText(record.CategoryName, MaxDetailLength, multiline: false);
        if (record.EventId.Id != 0)
            _buffer.Append('[').Append(record.EventId.Id.ToString(CultureInfo.InvariantCulture)).Append(']');
        _buffer.Append(": ");

        // Só a primeira linha da mensagem fica no cabeçalho, para trace e escopos estarem sempre na linha do registro.
        var message = (record.FormattedMessage ?? record.Body ?? string.Empty).AsSpan();
        var truncated = message.Length > MaxMessageLength;
        if (truncated)
            message = message[..MaxMessageLength];
        var lineBreak = message.IndexOfAny(LineBreaks);
        AppendText(lineBreak < 0 ? message : message[..lineBreak], int.MaxValue, multiline: false);
        if (truncated && lineBreak < 0)
            _buffer.Append(TruncatedMarker);

        if (record.TraceId != default)
            _buffer.Append(" | trace_id=").Append(record.TraceId.ToHexString()).Append(" span_id=").Append(record.SpanId.ToHexString());

        if (_settings.IncludeScopes)
        {
            _scopeSeparator = " | ";
            record.ForEachScope(static (scope, exporter) => exporter.AppendScope(scope), this);
        }

        if (lineBreak >= 0)
        {
            AppendText(message[lineBreak..], int.MaxValue, multiline: true);
            if (truncated)
                _buffer.Append(TruncatedMarker);
        }

        _buffer.Append('\n');
        if (record.Exception is { } exception)
        {
            // A mensagem da exceção pode trazer URL com token ou connection string: mesma redação dos textos de log.
            _buffer.Append(Indent);
            AppendText(SensitiveText.Redact(exception.ToString()), MaxDetailLength, multiline: true);
            _buffer.Append('\n');
        }
    }

    /// <summary>
    /// Itens do escopo como <c>chave=valor</c> (escopos de texto simples, só o valor). Os escopos não passam pelo processador de
    /// redação (não podem ser reescritos no registro): a redação dos valores é feita aqui.
    /// </summary>
    private void AppendScope(LogRecordScope scope)
    {
        foreach (var item in scope)
        {
            if (item.Key == "{OriginalFormat}" || _scopesTruncated)
                continue;

            // Quantidade de escopos não tem limite: passado o tamanho máximo do registro, os demais são omitidos.
            if (_buffer.Length - _recordStart >= MaxRecordLength)
            {
                _buffer.Append(" ").Append(TruncatedMarker);
                _scopesTruncated = true;
                continue;
            }

            _buffer.Append(_scopeSeparator);
            _scopeSeparator = " ";
            if (item.Key.Length > 0)
            {
                AppendText(item.Key, MaxDetailLength, multiline: false);
                _buffer.Append('=');
            }

            var value = item.Value is null or bool || !SensitiveKeys.IsSensitive(item.Key)
                ? SensitiveText.Redact(Convert.ToString(item.Value, CultureInfo.InvariantCulture) ?? string.Empty)
                : SensitiveText.RedactedValue;
            AppendText(value, MaxDetailLength, multiline: true);
        }
    }

    /// <summary>
    /// Texto com no máximo <paramref name="maxLength"/> caracteres (o excedente vira <c>…[truncado]</c>). Com
    /// <paramref name="multiline"/>, quebras de linha (inclusive <c>U+0085</c>, <c>U+2028</c> e <c>U+2029</c>) viram nova linha
    /// recuada: um texto vindo do usuário não consegue forjar uma linha que pareça outro registro (todo registro começa na coluna
    /// zero com a data). Os demais caracteres de controle (ex.: sequências ANSI de terminal) — e as quebras, sem
    /// <paramref name="multiline"/> — são gravados escapados (<c>\u001B</c>).
    /// </summary>
    private void AppendText(ReadOnlySpan<char> text, int maxLength, bool multiline)
    {
        var truncated = text.Length > maxLength;
        if (truncated)
            text = text[..maxLength];

        int index;
        while ((index = text.IndexOfAny(SpecialChars)) >= 0)
        {
            _buffer.Append(text[..index]);
            var c = text[index];
            if (multiline && IsLineBreak(c))
            {
                _buffer.Append('\n').Append(Indent);
                text = c == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? text[(index + 2)..] : text[(index + 1)..];
                continue;
            }

            _buffer.Append('\\').Append('u').Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            text = text[(index + 1)..];
        }

        _buffer.Append(text);
        if (truncated)
            _buffer.Append(TruncatedMarker);
    }

    private static bool IsLineBreak(char c) => c is '\r' or '\n' or (char)0x85 or (char)0x2028 or (char)0x2029;

    /// <summary>Quebras de linha reconhecidas por editores e visualizadores de log.</summary>
    private static readonly SearchValues<char> LineBreaks = SearchValues.Create(['\r', '\n', (char)0x85, (char)0x2028, (char)0x2029]);

    /// <summary>Caracteres de controle C0 (menos a tabulação), DEL, C1 e os separadores de linha e parágrafo do Unicode.</summary>
    private static readonly SearchValues<char> SpecialChars = SearchValues.Create(BuildSpecialChars());

    private static char[] BuildSpecialChars()
    {
        var chars = new List<char>();
        for (var c = 0; c < 0x20; c++)
        {
            if (c != '\t')
                chars.Add((char)c);
        }

        for (var c = 0x7F; c <= 0x9F; c++)
            chars.Add((char)c);
        chars.Add((char)0x2028);
        chars.Add((char)0x2029);
        return [.. chars];
    }


    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    /// <summary>Troca por <c>_</c> o que não vale em nome de arquivo (o <c>ServiceName</c> não tem essa restrição).</summary>
    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        // '*' e '?' também: o nome entra no padrão de busca da retenção.
        var chars = name.Trim().Select(c => Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' or '*' or '?' ? '_' : c).ToArray();
        var safe = new string(chars).Trim('.');
        return safe.Length == 0 ? "log" : safe;
    }
}
