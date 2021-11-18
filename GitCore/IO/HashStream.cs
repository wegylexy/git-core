using System.Buffers;
using System.Security.Cryptography;

namespace FlyByWireless.GitCore;

internal sealed class HashStream : Stream
{
    private readonly Stream _stream;

    private readonly HashAlgorithm _ha;

    private readonly bool _leaveOpen;

    private byte[]? _buffer;

    private int _read;

    public override bool CanRead => _stream.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => _stream.CanWrite;
    public override long Length => _stream.Length;
    public override long Position
    {
        get => _stream.Position;
        set => throw new InvalidOperationException();
    }

    public ReadOnlyMemory<byte> Hash => _ha.Hash ?? default;

    public int HashSize => _ha.HashSize;

    public HashStream(Stream stream, string hashAlgorithm, byte[]? prolog = null, bool leaveOpen = false)
    {
        _stream = stream;
        _ha = HashAlgorithm.Create(hashAlgorithm)!;
        if (prolog != null)
        {
            _ha.TransformBlock(prolog, 0, prolog.Length, null, 0);
        }
        _leaveOpen = leaveOpen;
    }

    public override void Flush() => _stream.Flush();

    public void Unread(int count) => _read -= count;

    private void BeforeRead()
    {
        if (_read > 0)
        {
            _ha.TransformBlock(_buffer!, 0, _read, null, 0);
            _read = 0;
        }
    }

    private void AfterRead(ReadOnlySpan<byte> buffer)
    {
        if (_buffer == null)
        {
            _buffer = buffer.ToArray();
        }
        else
        {
            if (_buffer.Length < buffer.Length)
            {
                _buffer = GC.AllocateUninitializedArray<byte>(buffer.Length);
            }
            buffer.CopyTo(_buffer);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        BeforeRead();
        AfterRead(buffer[..(_read = _stream.Read(buffer))]);
        return _read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        BeforeRead();
        _read = await _stream.ReadAsync(buffer, cancellationToken);
        AfterRead(buffer.Span[.._read]);
        return _read;
    }

    public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state) =>
        throw new NotSupportedException();

    public override int EndRead(IAsyncResult asyncResult) =>
        throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new InvalidOperationException();

    public override void SetLength(long value) => throw new InvalidOperationException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _ha.TransformBlock(buffer, offset, count, null, 0);
        _stream.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var a = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(a);
            Write(a, 0, buffer.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        _ha.TransformBlock(buffer, offset, count, null, 0);
        return _stream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {

        var a = ArrayPool<byte>.Shared.Rent(buffer.Length);
        try
        {
            buffer.CopyTo(a);
            return new(WriteAsync(a, 0, buffer.Length, cancellationToken));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(a);
        }
    }

    public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback? callback, object? state)
    {
        _ha.TransformBlock(buffer, offset, count, null, 0);
        return _stream.BeginRead(buffer, offset, count, callback, state);
    }

    public override void EndWrite(IAsyncResult asyncResult) => _stream.EndWrite(asyncResult);

    public void ComputeHash()
    {
        BeforeRead();
        _ha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
    }

    protected override void Dispose(bool disposing)
    {
        if (!_leaveOpen)
        {
            using (_stream) { }
        }
    }

    public override ValueTask DisposeAsync() => _leaveOpen ? ValueTask.CompletedTask : _stream.DisposeAsync();
}