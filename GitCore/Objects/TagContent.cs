using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace FlyByWireless.GitCore;

public sealed record class TagContent(ReadOnlyMemory<byte> Object, ObjectType Type, string Name, User Tagger, DateTimeOffset Tagged, string Message)
{
    public static async Task<TagContent> UncompressAsync(string path, CancellationToken cancellationToken = default)
    {
        using var file = File.OpenRead(path);
        using ZLibStream zls = new(file, CompressionMode.Decompress);
        var size = 0;
        {
            // Asserts type
            var buffer = GC.AllocateUninitializedArray<byte>(4);
            if (await zls.ReadAsync(buffer.AsMemory(0, 4), cancellationToken) != 4)
            {
                throw new EndOfStreamException();
            }
            if (BitConverter.ToInt32(buffer.AsSpan(0, 4)) != (BitConverter.IsLittleEndian ? 0x20_67_61_74 : 0x74_61_67_20))
            {
                throw new InvalidDataException("Invalid tag");
            }
            // Reads size
            for (var i = 0; ; ++i)
            {
                if (await zls.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 1)
                {
                    throw new EndOfStreamException();
                }
                if (buffer[0] == 0)
                {
                    break;
                }
                size = size * 10 + (buffer[0] - '0');
            }
            // Reads content
            if (buffer.Length < size)
            {
                buffer = GC.AllocateUninitializedArray<byte>(size);
            }
            for (var read = 0; read < size;)
            {
                var r = await zls.ReadAsync(buffer.AsMemory(read, size - read), cancellationToken);
                if (r == 0)
                {
                    throw new EndOfStreamException();
                }
                read += r;
            }
            return new(new ReadOnlySequence<byte>(buffer, 0, size));
        }

    }

    public TagContent(ReadOnlySequence<byte> uncompressed) : this(null, default, null!, null!, default, null!)
    {
        var all = Encoding.UTF8.GetString(uncompressed);
        for (var i = 0; ;)
        {
            var n = all.IndexOf('\n', i);
            var s = n - 1 - i;
            if (s < 0)
            {
                if (Tagger == null)
                {
                    throw new InvalidDataException("No tagger");
                }
                Message = all[(n + 1)..];
                return;
            }
            s = all.IndexOf(' ', i, s);
            var value = all.AsSpan()[(s + 1)..n];
            switch (all[i..s])
            {
                case "object":
                    Object = value.ParseHex();
                    break;
                case "type":
                    Type = value.ToString() switch
                    {
                        "commit" => ObjectType.Commit,
                        "tree" => ObjectType.Tree,
                        "blob" => ObjectType.Blob,
                        _ => throw new InvalidDataException("Invalid object type")
                    };
                    break;
                case "tag":
                    Name = new(value);
                    break;
                case "tagger":
                    {
                        Tagger = User.ParseWithDateTimeOffset(value, out var tagged);
                        Tagged = tagged;
                    }
                    break;
            }
            i = n + 1;
        }
    }

    public bool Equals(TagContent? other) =>
        other != null && ByteROMEqualityComparer.Instance.Equals(Object, other.Object) && Name == other.Name && Tagger == other.Tagger && Tagged == other.Tagged && Message == other.Message;

    public override int GetHashCode() => Name.GetHashCode();

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append("object ");
        sb.Append(Object.ToHexString());
        sb.Append("\ntype ");
        sb.Append(Type switch
        {
            ObjectType.Commit => "commit",
            ObjectType.Tree => "tree",
            ObjectType.Blob => "blob",
            _ => throw new InvalidDataException("Invalid tag type")
        });
        sb.Append("\ntag ");
        sb.Append(Name);
        sb.Append("\ntagger ");
        sb.Append(Tagger.ToString(Tagged));
        sb.Append("\n\n");
        sb.Append(Message);
        return sb.ToString();
    }
}