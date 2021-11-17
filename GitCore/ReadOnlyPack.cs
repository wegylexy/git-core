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

public readonly record struct UnpackedObject(ObjectType Type, long Size, ReadOnlySequence<byte> Data, ReadOnlyMemory<byte> Hash)
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

    public ReadOnlyMemory<byte> Prolog { get; internal init; } = default;

    public override string ToString() => Type switch
    {
        ObjectType.ReferenceDelta => $"delta {Size} based on {Hash.ToHexString()}",
        ObjectType.OffsetDelta => $"delta {Size} based on {Encoding.UTF8.GetString(Hash.Span)}",
        _ => FormattableString.Invariant($"{TypeString(Type)} {Size} {Hash.ToHexString()}")
    };

    public Stream AsStream()
    {
        if (Type is ObjectType.OffsetDelta or ObjectType.ReferenceDelta)
        {
            throw new InvalidOperationException("Delta");
        }
        return new SequenceStream(Data);
    }

    /// <summary>
    /// Derives from base stream by applying delta.
    /// </summary>
    /// <param name="baseStream">Seekable base stream.</param>
    /// <param name="derivedStream">Derived output stream.</param>
    /// <param name="hashAlgorithm">Hash algorithm.</param>
    /// <param name="preferredBufferSize">Preferred buffer size for copy instructions.</param>
    /// <param name="cancellationToken">Token to cancel async instructions.</param>
    /// <returns>Hash of derived data.</returns>
    /// <exception cref="InvalidOperationException">Delta not applicable.</exception>
    /// <exception cref="InvalidDataException">Invalid delta.</exception>
    public async Task<ReadOnlyMemory<byte>> DeltaAsync(ObjectType type, Stream baseStream, Stream derivedStream, string hashAlgorithm = nameof(SHA1), int preferredBufferSize = 4096, CancellationToken cancellationToken = default)
    {
        if (Type is not (ObjectType.OffsetDelta or ObjectType.ReferenceDelta))
        {
            throw new InvalidOperationException("Not delta");
        }
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
        if (baseStream.Length != baseSize)
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
        using HashStream hs = new(derivedStream, hashAlgorithm, PrologBytes(type, derivedSize), true);
        while (i < Size)
        {
            var b = Data.Slice(i++, 1).FirstSpan[0];
            if ((b & 0b10000000) == 0)
            {
                var length = b & 0b01111111L;
                if (length == 0)
                {
                    throw new InvalidDataException("Invalid delta instruction");
                }
                await Data.Slice(i, length).CopyToAsync(hs, cancellationToken);
                i += length;
                written += length;
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
                baseStream.Seek(offset, SeekOrigin.Begin);
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
                var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(preferredBufferSize, size));
                try
                {
                    while (size > 0)
                    {
                        var r = await baseStream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, size)), cancellationToken);
                        await hs.WriteAsync(buffer.AsMemory(0, r), cancellationToken);
                        size -= r;
                        written += r;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        if (written != derivedSize)
        {
            throw new InvalidDataException("Invalid delta");
        }
        hs.ComputeHash();
        return hs.Hash;
    }
}

public sealed class ReadOnlyPack : IAsyncEnumerable<UnpackedObject>
{
    private sealed class ObjectSegment : ReadOnlySequenceSegment<byte>
    {
        public ObjectSegment(ReadOnlyMemory<byte> memory, ReadOnlySequenceSegment<byte>? next, long runningIndex)
        {
            Memory = memory;
            Next = next;
            RunningIndex = runningIndex;
        }
    }

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

    public ReadOnlyPack(Stream stream, string hashAlgorithm = nameof(SHA1))
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
                            ObjectSegment end = new(last, null, runningIndex -= last.Length), start = end;
                            while (segments.TryPop(out var previous))
                            {
                                start = new(previous, start, runningIndex -= previous.Length);
                            }
                            yield return new(type, size, new(start, 0, end, end.Memory.Length), buffer.ToArray());
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
                                ObjectSegment end = new(last, null, runningIndex -= last.Length), start = end;
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
                            yield return new(type, size, data, hash)
                            {
                                Prolog = prolog
                            };
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