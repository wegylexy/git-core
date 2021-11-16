using System.Buffers;

namespace FlyByWireless.GitCore;

public class SequenceStream : Stream
{
    private readonly ReadOnlySequence<byte> _sequence;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _sequence.Length;
    public override long Position { get; set; }

    public SequenceStream(ReadOnlySequence<byte> sequence) => _sequence = sequence;

    public override void Flush() => throw new InvalidOperationException();

    public override int Read(byte[] buffer, int offset, int count)
    {
        count = (int)Math.Min(count, Length - Position);
        _sequence.Slice(Position, count).CopyTo(buffer.AsSpan(offset, count));
        return count;
    }

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