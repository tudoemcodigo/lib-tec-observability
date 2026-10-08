namespace TEC.Observability.Tests.Security.Adversarial;

/// <summary>
/// Stream "infinito" de conteúdo hostil: um prefixo seguido de um padrão repetido sem fim. Para provar que o leitor para
/// sozinho, o stream registra quantos bytes foram lidos e encerra (fim de arquivo) ao atingir <paramref name="hardLimit"/>,
/// um limite que o teste nunca deve ver alcançado.
/// </summary>
internal sealed class EndlessStream(byte[] prefix, byte[] pattern, long hardLimit) : Stream
{
    /// <summary>Bytes entregues ao leitor.</summary>
    public long BytesRead { get; private set; }

    /// <summary>O leitor consumiu tudo até o limite de segurança do teste (não parou sozinho).</summary>
    public bool HitHardLimit => BytesRead >= hardLimit;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int count = (int)Math.Min(buffer.Length, hardLimit - BytesRead);
        for (int i = 0; i < count; i++)
        {
            long position = BytesRead + i;
            buffer[i] = position < prefix.Length ? prefix[position] : pattern[(position - prefix.Length) % pattern.Length];
        }

        BytesRead += count;
        return count;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer, offset, count));

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
