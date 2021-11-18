using System.Buffers;

namespace FlyByWireless.GitCore;

internal sealed class SequenceStream : Stream
{
    private readonly ReadOnlySequence<byte> _sequence;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _sequence.Length;
    public override long Position { get; set; }

    public SequenceStream(ReadOnlySequence<byte> sequence) => _sequence = sequence;

    public override void Flush() => throw new InvalidOperationException();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = (int)Math.Min(buffer.Length, Length - Position);
        _sequence.Slice(Position, read).CopyTo(buffer);
        Position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer, offset, count));

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => 0,
        SeekOrigin.Current => Position,
        SeekOrigin.End => Length,
        _ => throw new ArgumentOutOfRangeException(nameof(origin))
    } + offset;

    public override void SetLength(long value) => throw new InvalidOperationException();

    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException();
}