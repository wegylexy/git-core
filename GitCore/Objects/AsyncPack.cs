using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;

namespace FlyByWireless.GitCore;

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
            if (BitConverter.ToInt32(buffer) != (BitConverter.IsLittleEndian ? 0x4b_43_41_50 : 0x50_41_43_4b)) // "PACK"
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
        var buffer = GC.AllocateUninitializedArray<byte>(hashSize);
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
                if (size == 0)
                {
                    throw new InvalidDataException("Unexpected 0");
                }
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
                        for (var read = 0; read < hashSize;)
                        {
                            var r = await _hs.ReadAsync(buffer.AsMemory(read, hashSize - read), cancellationToken);
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
                                if (i.Length > 0)
                                {
                                    _hs.Unread(i.Length);
                                    _ = _ss.Push(i);
                                }
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
                            var header = UnpackedObject.HeaderBytes(type, size);
                            ha.Initialize();
                            ha.TransformBlock(header, 0, header.Length, null, 0);
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
                                    if (i.Length > 0)
                                    {
                                        _hs.Unread(i.Length);
                                        _ = _ss.Push(i);
                                    }
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
                            yield return new(type, data, ha.ComputeHash(Array.Empty<byte>()));
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