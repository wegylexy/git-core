namespace FlyByWireless.GitCore;

internal sealed class StackStream : Stream
{
    readonly Stack<ReadOnlyMemory<byte>> _stack = new();
    readonly Stream _stream;
    readonly bool _leaveOpen;

    public override bool CanRead => _stack.Count > 0 || _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _stream.Length + _stack.Sum(m => m.Length);
    public override long Position
    {
        get => throw new InvalidOperationException();
        set => throw new InvalidOperationException();
    }

    public StackStream(Stream stream, bool leaveOpen = false)
    {
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    public override void Flush() => throw new InvalidOperationException();

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return _stack.Count > 0 ? 0 : _stream.Read(buffer);
        }
        var totalRead = 0;
        while (!buffer.IsEmpty && _stack.TryPop(out var rom))
        {
            var read = Math.Min(buffer.Length, rom.Length);
            totalRead += read;
            rom[..read].Span.CopyTo(buffer[..read]);
            if (Push(rom[read..]) || read == buffer.Length)
            {
                return totalRead;
            }
            buffer = buffer[read..];
        }
        return totalRead + _stream.Read(buffer);
    }

    public override int ReadByte()
    {
        Span<byte> s = stackalloc byte[1];
        var read = Read(s);
        return read == 0 ? -1 : s[0];
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return _stack.Count > 0 ? 0 : await _stream.ReadAsync(buffer, cancellationToken);
        }
        var totalRead = 0;
        while (!buffer.IsEmpty && _stack.TryPop(out var rom))
        {
            var read = Math.Min(buffer.Length, rom.Length);
            totalRead += read;
            rom[..read].CopyTo(buffer[..read]);
            if (Push(rom[read..]) || read == buffer.Length)
            {
                return totalRead;
            }
            buffer = buffer[read..];
        }
        return totalRead + await _stream.ReadAsync(buffer, cancellationToken);
    }

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
        throw new NotSupportedException();

    public override int EndRead(IAsyncResult asyncResult) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new InvalidOperationException();

    public override void SetLength(long value) => throw new InvalidOperationException();

    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException();

    public bool Push(ReadOnlyMemory<byte> prolog)
    {
        if (prolog.IsEmpty)
        {
            return false;
        }
        _stack.Push(prolog);
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (!_leaveOpen)
        {
            using (_stream) { }
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!_leaveOpen)
        {
            await using (_stream) { }
        }
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}