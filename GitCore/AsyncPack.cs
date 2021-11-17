using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FlyByWireless.GitCore;

public enum ObjectType
{
    Undefined,
    Commit,
    Tree,
    Blob,
    Tag,
    Reserved,
    OffsetDelta,
    ReferenceDelta
}

public readonly record struct UnpackedObject(ObjectType Type, ReadOnlySequence<byte> Data, ReadOnlyMemory<byte> Hash)
{
    internal static string TypeString(ObjectType type) => type switch
    {
        ObjectType.Commit => "commit",
        ObjectType.Tree => "tree",
        ObjectType.Blob => "blob",
        ObjectType.Tag => "tag",
        _ => throw new InvalidDataException("Invalid object type")
    };

    internal static byte[] PrologBytes(ObjectType type, long size) => Encoding.ASCII.GetBytes($"{TypeString(type)} {size}\0");

    public override string ToString() => Type switch
    {
        ObjectType.OffsetDelta => $"delta {Data.Length} based on {Encoding.UTF8.GetString(Hash.Span)}",
        ObjectType.ReferenceDelta => $"delta {Data.Length} based on {Hash.ToHexString()}",
        _ => FormattableString.Invariant($"{TypeString(Type)} {Data.Length} {Hash.ToHexString()}")
    };

    public Stream AsStream()
    {
        if (Type != ObjectType.Blob)
        {
            throw new InvalidOperationException("Object is not blob");
        }
        return new SequenceStream(Data);
    }

    public AsyncTree AsTree(string hashAlgorithm = nameof(SHA1))
    {
        if (Type != ObjectType.Tree)
        {
            throw new InvalidOperationException("Object is not tree");
        }
        return new(Data.Length, new SequenceStream(Data), hashAlgorithm);
    }

    public UnpackedObject Delta(UnpackedObject baseObject, string hashAlgorithm = nameof(SHA1), int preferredBufferSize = 4096)
    {
        if (Type is not (ObjectType.OffsetDelta or ObjectType.ReferenceDelta))
        {
            throw new InvalidOperationException("Object is not delta");
        }
        Stack<ReadOnlyMemory<byte>> segments = new();
        long i = 0L, baseSize = 0L, derivedSize = 0L, written = 0L;
        for (var s = 0; ; s += 7)
        {
            var b = Data.Slice(i++, 1).FirstSpan[0];
            baseSize |= (b & 0b01111111L) << s;
            if ((b & 0b10000000) == 0)
            {
                break;
            }
        }
        if (baseObject.Data.Length != baseSize)
        {
            throw new InvalidOperationException("Base stream size mismatch");
        }
        for (var s = 0; ; s += 7)
        {
            var b = Data.Slice(i++, 1).FirstSpan[0];
            derivedSize |= (b & 0b01111111L) << s;
            if ((b & 0b10000000) == 0)
            {
                break;
            }
        }
        using var ha = HashAlgorithm.Create(hashAlgorithm)!;
        var prolog = PrologBytes(baseObject.Type, derivedSize);
        ha.TransformBlock(prolog, 0, prolog.Length, null, 0);
        {
            byte[]? buffer = null;
            try
            {
                while (i < Data.Length)
                {
                    var b = Data.Slice(i++, 1).FirstSpan[0];
                    ReadOnlySequence<byte> source;
                    if ((b & 0b10000000) == 0)
                    {
                        var length = b & 0b01111111L;
                        if (length == 0)
                        {
                            throw new InvalidDataException("Invalid delta instruction");
                        }
                        source = Data.Slice(i, length);
                        i += length;
                    }
                    else
                    {
                        var offset = 0;
                        if ((b & 0b00000001) == 0b00000001)
                        {
                            offset |= Data.Slice(i++, 1).FirstSpan[0];
                        }
                        if ((b & 0b00000010) == 0b00000010)
                        {
                            offset |= Data.Slice(i++, 1).FirstSpan[0] << 8;
                        }
                        if ((b & 0b00000100) == 0b00000100)
                        {
                            offset |= Data.Slice(i++, 1).FirstSpan[0] << 16;
                        }
                        if ((b & 0b00001000) == 0b00001000)
                        {
                            offset |= Data.Slice(i++, 1).FirstSpan[0] << 24;
                        }
                        var size = 0;
                        if ((b & 0b00010000) == 0b00010000)
                        {
                            size |= Data.Slice(i++, 1).FirstSpan[0];
                        }
                        if ((b & 0b00100000) == 0b00100000)
                        {
                            size |= Data.Slice(i++, 1).FirstSpan[0] << 8;
                        }
                        if ((b & 0b01000000) == 0b01000000)
                        {
                            size |= Data.Slice(i++, 1).FirstSpan[0] << 16;
                        }
                        source = baseObject.Data.Slice(offset, size);
                    }
                    foreach (var s in source)
                    {
                        segments.Push(s);
                        buffer ??= ArrayPool<byte>.Shared.Rent(preferredBufferSize);
                        for (var offset = 0; offset < s.Length;)
                        {
                            var m = s[offset..Math.Min(offset + buffer.Length, s.Length)];
                            m.CopyTo(buffer);
                            ha.TransformBlock(buffer, 0, m.Length, null, 0);
                            offset += m.Length;
                        }
                    }
                    written += source.Length;
                }
            }
            finally
            {
                if (buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        if (written != derivedSize)
        {
            throw new InvalidDataException("Invalid delta");
        }
        ha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        ReadOnlySequence<byte> data;
        if (segments.TryPop(out var last))
        {
            var runningIndex = written;
            BytesSegment end = new(last, null, runningIndex -= last.Length), start = end;
            while (segments.TryPop(out var previous))
            {
                start = new(previous, start, runningIndex -= previous.Length);
            }
            data = new(start, 0, end, end.Memory.Length);
        }
        else
        {
            data = ReadOnlySequence<byte>.Empty;
        }
        return new(baseObject.Type, data, ha.Hash);
    }
}

public sealed class AsyncPack : IAsyncEnumerable<UnpackedObject>
{
    private readonly StackStream _ss;

    private readonly string _hash;

    private readonly HashStream _hs;

    private int _MaxChunkSize = 16384;
    public int MaxChunkSize
    {
        get => _MaxChunkSize;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            _MaxChunkSize = value;
        }
    }

    private long _entries = -1;

    public AsyncPack(Stream stream, string hashAlgorithm = nameof(SHA1))
    {
        _ss = stream is StackStream ss ? ss : new(stream, true);
        _hs = new(_ss, _hash = hashAlgorithm, null, true);
    }

    public async ValueTask<long> CountAsync(CancellationToken cancellationToken = default)
    {
        if (_entries >= 0)
        {
            return _entries;
        }
        var buffer = ArrayPool<byte>.Shared.Rent(12);
        try
        {
            // Reads at least 12 bytes
            for (var read = 0; read < 12 && !cancellationToken.IsCancellationRequested; read += await _hs.ReadAsync(buffer.AsMemory()[read..12], cancellationToken)) { }
            // Asserts PACK
            if (BitConverter.ToInt32(buffer, 0) != (BitConverter.IsLittleEndian ? 0x4b_43_41_50 : 0x50_41_43_4b)) // "PACK"
            {
                throw new InvalidDataException("PACK signature mismatch");
            }
            // Gets number of entries
            if (BitConverter.IsLittleEndian)
            {
                buffer.AsSpan(4, 4).Reverse();
                buffer.AsSpan(8, 4).Reverse();
            }
            // Asserts version
            if (BitConverter.ToUInt32(buffer, 4) is not 2 or 3)
            {
                throw new NotSupportedException("Only version 2 or 3 is supported");
            }
            // Gets count
            return _entries = BitConverter.ToUInt32(buffer, 8);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async IAsyncEnumerator<UnpackedObject> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var hashSize = _hs.HashSize / 8;
        var buffer = GC.AllocateUninitializedArray<byte>(Math.Max(hashSize, 20));
        {
            // Enumerates
            var entries = await CountAsync(cancellationToken);
            using var ha = HashAlgorithm.Create(_hash)!;
            while (entries-- > 0)
            {
                async Task ReadByteAsync()
                {
                    if (await _hs.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) == 0)
                    {
                        throw new EndOfStreamException();
                    }
                }
                await ReadByteAsync();
                long size = buffer[0];
                var type = (ObjectType)((size >> 4) & 0b111);
                size &= 0b1111;
                for (var s = 4; (buffer[0] & 0b10000000) != 0; s += 7)
                {
                    await ReadByteAsync();
                    size |= (buffer[0] & 0b1111111L) << s;
                }
                switch (type)
                {
                    case ObjectType.OffsetDelta:
                        throw new NotSupportedException("Offset delta not supported");
                    case ObjectType.ReferenceDelta:
                        for (var read = 0; read < 20;)
                        {
                            var r = await _hs.ReadAsync(buffer.AsMemory(read, 20 - read), cancellationToken);
                            if (r == 0)
                            {
                                throw new EndOfStreamException();
                            }
                            read += r;
                        }
                        {
                            Stack<ReadOnlyMemory<byte>> segments = new();
                            {
                                using ZLibStream zls = new(_hs, CompressionMode.Decompress, true);
                                for (var read = 0; read < size;)
                                {
                                    var b = GC.AllocateUninitializedArray<byte>((int)Math.Min(size - read, _MaxChunkSize));
                                    var r = await zls.ReadAsync(b, cancellationToken);
                                    if (r == 0)
                                    {
                                        throw new EndOfStreamException();
                                    }
                                    segments.Push(b.AsMemory(0, r));
                                    read += r;
                                }
                                var i = zls.GetInputBuffer();
                                _hs.Unread(i.Length);
                                _ = _ss.Push(i);
                            }
                            var runningIndex = size;
                            var last = segments.Pop();
                            BytesSegment end = new(last, null, runningIndex -= last.Length), start = end;
                            while (segments.TryPop(out var previous))
                            {
                                start = new(previous, start, runningIndex -= previous.Length);
                            }
                            yield return new(type, new(start, 0, end, end.Memory.Length), buffer.ToArray());
                        }
                        break;
                    default:
                        {
                            ReadOnlySequence<byte> data;
                            ReadOnlyMemory<byte> hash;
                            var prolog = UnpackedObject.PrologBytes(type, size);
                            ha.Initialize();
                            ha.TransformBlock(prolog, 0, prolog.Length, null, 0);
                            if (size > 0)
                            {
                                Stack<ReadOnlyMemory<byte>> segments = new();
                                {
                                    using ZLibStream zls = new(_hs, CompressionMode.Decompress, true);
                                    for (var read = 0; read < size;)
                                    {
                                        var b = GC.AllocateUninitializedArray<byte>((int)Math.Min(size - read, _MaxChunkSize));
                                        var r = await zls.ReadAsync(b, cancellationToken);
                                        if (r == 0)
                                        {
                                            throw new EndOfStreamException();
                                        }
                                        segments.Push(b.AsMemory(0, r));
                                        ha.TransformBlock(b, 0, r, null, 0);
                                        read += r;
                                    }
                                    var i = zls.GetInputBuffer();
                                    _hs.Unread(i.Length);
                                    _ = _ss.Push(i);
                                }
                                var runningIndex = size;
                                var last = segments.Pop();
                                BytesSegment end = new(last, null, runningIndex -= last.Length), start = end;
                                while (segments.TryPop(out var previous))
                                {
                                    start = new(previous, start, runningIndex -= previous.Length);
                                }
                                data = new(start, 0, end, end.Memory.Length);
                            }
                            else
                            {
                                data = ReadOnlySequence<byte>.Empty;
                            }
                            ha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                            hash = ha.Hash!;
                            yield return new(type, data, hash);
                        }
                        break;
                }
            }
        }
        // Asserts checksum
        for (var read = 0; read < hashSize;)
        {
            var r = await _ss.ReadAsync(buffer.AsMemory(read, hashSize - read), cancellationToken);
            if (r == 0)
            {
                throw new EndOfStreamException();
            }
            read += r;
        }
        _hs.ComputeHash();
        if (!buffer.AsSpan(0, hashSize).SequenceEqual(_hs.Hash.Span))
        {
            throw new InvalidDataException("Checksum fail");
        }
    }
}