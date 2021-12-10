namespace FlyByWireless.GitCore;

public sealed class TextInputStream : Stream
{
    private bool? _convert;
    private ReadOnlyMemory<byte> _back;
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    public override bool CanRead => !_back.IsEmpty || _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new InvalidOperationException();
    public override long Position
    {
        get => throw new InvalidOperationException();
        set => throw new InvalidOperationException();
    }

    public TextInputStream(Stream stream, bool alwaysConvert = false, bool leaveOpen = false)
    {
        _convert = alwaysConvert ? true : null;
        _stream = stream;
        _leaveOpen = leaveOpen;
    }

    public override void Flush() => throw new InvalidOperationException();

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = 0;
        if (_convert == null)
        {
            _convert = true;
            var peek = GC.AllocateUninitializedArray<byte>(8000);
            while (read < 8000)
            {
                var r = _stream.Read(peek.AsSpan(read, 8000 - read));
                if (r == 0)
                {
                    break;
                }
                if (peek.AsSpan(read, r).Contains<byte>(0))
                {
                    _convert = false;
                    read += r;
                    break;
                }
                read += r;
            }
            _back = peek.AsMemory(0, read);
        }
        var b = buffer;
        read = Math.Min(b.Length, _back.Length);
        if (read > 0)
        {
            _back.Span.CopyTo(b);
            _back = _back[read..];
            b = b[read..];
        }
        if (!b.IsEmpty)
        {
            read += _stream.Read(b);
        }
        if (_convert is true)
        {
            b = buffer[..read];
            var offset = read = 0;
            for (; ; )
            {
                {
                    var i = b[offset..].IndexOf<byte>(13);
                    if (i < 0)
                    {
                        if (offset != read)
                        {
                            b[offset..].CopyTo(b[read..]);
                        }
                        read += b.Length - offset;
                        break;
                    }
                    if (i > 0)
                    {
                        if (offset != read)
                        {
                            b.Slice(offset, i).CopyTo(b.Slice(read, i));
                        }
                        read += i;
                        offset += i;
                    }
                }
                if (offset + 1 < b.Length)
                {
                    if (b[offset + 1] != 10)
                    {
                        b[read++] = 13;
                    }
                    ++offset;
                }
                else if (read + 1 < buffer.Length)
                {
                    var r = _stream.Read(buffer[(read + 1)..]);
                    if (r == 0)
                    {
                        b[read++] = 13;
                        break;
                    }
                    offset = read + 1;
                    b = buffer[..(offset + r)];
                    if (b[offset] != 10)
                    {
                        b[read++] = 13;
                    }
                }
                else
                {
                    var peek = GC.AllocateUninitializedArray<byte>(buffer.Length);
                    var r = _stream.Read(peek);
                    if (r == 0)
                    {
                        b[read++] = 13;
                        break;
                    }
                    if (peek[0] == 10)
                    {
                        b[read++] = 10;
                        _back = peek.AsMemory(1, r - 1);
                    }
                    else
                    {
                        _back = peek.AsMemory(0, r);
                    }
                    break;
                }
            }
        }
        return read;
    }

    public override int ReadByte()
    {
        Span<byte> buffer = stackalloc byte[1];
        return Read(buffer) != 0 ? buffer[0] : -1;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = 0;
        if (_convert == null)
        {
            _convert = true;
            var peek = GC.AllocateUninitializedArray<byte>(8000);
            while (read < 8000)
            {
                var r = await _stream.ReadAsync(peek.AsMemory(read, 8000 - read), cancellationToken);
                if (r == 0)
                {
                    break;
                }
                if (peek.AsSpan(read, r).Contains<byte>(0))
                {
                    _convert = false;
                    read += r;
                    break;
                }
                read += r;
            }
            _back = peek.AsMemory(0, read);
        }
        var b = buffer;
        read = Math.Min(b.Length, _back.Length);
        if (read > 0)
        {
            _back.CopyTo(b);
            _back = _back[read..];
            b = b[read..];
        }
        if (!b.IsEmpty)
        {
            read += await _stream.ReadAsync(b, cancellationToken);
        }
        if (_convert is true)
        {
            b = buffer[..read];
            var offset = read = 0;
            for (; ; )
            {
                {
                    var i = b.Span[offset..].IndexOf<byte>(13);
                    if (i < 0)
                    {
                        if (offset != read)
                        {
                            b[offset..].CopyTo(b[read..]);
                        }
                        read += b.Length - offset;
                        break;
                    }
                    if (i > 0)
                    {
                        if (offset != read)
                        {
                            b.Slice(offset, i).CopyTo(b.Slice(read, i));
                        }
                        read += i;
                        offset += i;
                    }
                }
                if (offset + 1 < b.Length)
                {
                    if (b.Span[offset + 1] != 10)
                    {
                        b.Span[read++] = 13;
                    }
                    ++offset;
                }
                else if (read + 1 < buffer.Length)
                {
                    var r = await _stream.ReadAsync(buffer[(read + 1)..], cancellationToken);
                    if (r == 0)
                    {
                        b.Span[read++] = 13;
                        break;
                    }
                    offset = read + 1;
                    b = buffer[..(offset + r)];
                    if (b.Span[offset] != 10)
                    {
                        b.Span[read++] = 13;
                    }
                }
                else
                {
                    var peek = GC.AllocateUninitializedArray<byte>(buffer.Length);
                    var r = await _stream.ReadAsync(peek, cancellationToken);
                    if (r == 0)
                    {
                        b.Span[read++] = 13;
                        break;
                    }
                    if (peek[0] == 10)
                    {
                        b.Span[read++] = 10;
                        _back = peek.AsMemory(1, r - 1);
                    }
                    else
                    {
                        _back = peek.AsMemory(0, r);
                    }
                    break;
                }
            }
        }
        return read;
    }

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
        throw new NotSupportedException();

    public override int EndRead(IAsyncResult asyncResult) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new InvalidOperationException();

    public override void SetLength(long value) => throw new InvalidOperationException();

    public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException();

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