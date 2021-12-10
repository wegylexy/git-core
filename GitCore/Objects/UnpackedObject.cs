using System.Buffers;
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

public sealed record class UnpackedObject(ObjectType Type, ReadOnlySequence<byte> Data, ReadOnlyMemory<byte> Hash)
{
    public bool IsDelta => Type.HasFlag(ObjectType.OffsetDelta);

    internal static string TypeString(ObjectType type) => type switch
    {
        ObjectType.Commit => "commit",
        ObjectType.Tree => "tree",
        ObjectType.Blob => "blob",
        ObjectType.Tag => "tag",
        _ => throw new InvalidDataException("Invalid object type")
    };

    internal static byte[] HeaderBytes(ObjectType type, long size) => Encoding.ASCII.GetBytes($"{TypeString(type)} {size}\0");

    public bool Equals(UnpackedObject? other) => other != null && Type == other.Type &&
        ByteROMEqualityComparer.Instance.Equals(Hash, other.Hash) && (!IsDelta || Data.Equals(other.Data));

    public override int GetHashCode() =>
        IsDelta ? Data.GetHashCode() : ByteROMEqualityComparer.Instance.GetHashCode(Hash);

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

    public CommitContent ToCommitContent()
    {
        if (Type != ObjectType.Commit)
        {
            throw new InvalidOperationException("Object is not commit");
        }
        return new(Data);
    }

    public TagContent ToTagContent()
    {
        if (Type != ObjectType.Tag)
        {
            throw new InvalidOperationException("Object is not tag");
        }
        return new(Data);
    }

    public AsyncTree AsTree(string hashAlgorithm = nameof(SHA1))
    {
        if (Type != ObjectType.Tree)
        {
            throw new InvalidOperationException("Object is not tree");
        }
        return new(Data.Length, new SequenceStream(Data), hashAlgorithm);
    }

    internal long DeltaSize(out long baseSize, out long deltaOffset)
    {
        deltaOffset = baseSize = 0L;
        if (Type is not (ObjectType.OffsetDelta or ObjectType.ReferenceDelta))
        {
            throw new InvalidOperationException("Object is not delta");
        }
        var derivedSize = 0L;
        for (var s = 0; ; s += 7)
        {
            var b = Data.Slice(deltaOffset++, 1).FirstSpan[0];
            baseSize |= (b & 0b01111111L) << s;
            if ((b & 0b10000000) == 0)
            {
                break;
            }
        }
        for (var s = 0; ; s += 7)
        {
            var b = Data.Slice(deltaOffset++, 1).FirstSpan[0];
            derivedSize |= (b & 0b01111111L) << s;
            if ((b & 0b10000000) == 0)
            {
                return derivedSize;
            }
        }
    }

    public UnpackedObject Delta(UnpackedObject baseObject, string hashAlgorithm = nameof(SHA1), int preferredBufferSize = 4096)
    {
        var derivedSize = DeltaSize(out var baseSize, out var i);
        if (baseObject.Data.Length != baseSize)
        {
            throw new InvalidDataException($"Expected base stream size of {baseSize} instead of {baseObject.Data.Length}");
        }
        Stack<ReadOnlyMemory<byte>> segments = new();
        var written = 0L;
        using var ha = HashAlgorithm.Create(hashAlgorithm)!;
        var header = HeaderBytes(baseObject.Type, derivedSize);
        ha.TransformBlock(header, 0, header.Length, null, 0);
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
                        if (size == 0)
                        {
                            size = 0x10000;
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
            throw new InvalidDataException($"Expected derived size of {derivedSize} instead of {written}");
        }
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
        return new(baseObject.Type, data, ha.ComputeHash(Array.Empty<byte>()));
    }
}