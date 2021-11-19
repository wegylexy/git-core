using System.Buffers;
using System.IO.Compression;
using System.Text;

namespace FlyByWireless.GitCore;

public sealed record class CommitContent(ReadOnlyMemory<byte> Tree, User Author, DateTimeOffset Authored, User Committer, DateTimeOffset Committed, string Message)
{
    public static async Task<CommitContent> UncompressAsync(string path, CancellationToken cancellationToken = default)
    {
        using var file = File.OpenRead(path);
        using ZLibStream zls = new(file, CompressionMode.Decompress);
        var size = 0;
        {
            // Asserts type
            var buffer = GC.AllocateUninitializedArray<byte>(7);
            if (await zls.ReadAsync(buffer.AsMemory(0, 7), cancellationToken) != 7)
            {
                throw new EndOfStreamException();
            }
            if (!buffer.SequenceEqual(new byte[] { 0x63, 0x6f, 0x6d, 0x6d, 0x69, 0x74, 0x20 }))
            {
                throw new InvalidDataException("Invalid commit");
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

    public CommitContent(ReadOnlySequence<byte> uncompressed) : this(null, null!, default, null!, default, null!)
    {
        var all = Encoding.UTF8.GetString(uncompressed);
        for (var i = 0; ;)
        {
            var n = all.IndexOf('\n', i);
            var s = n - 1 - i;
            if (s < 0)
            {
                if (Author == null)
                {
                    throw new InvalidDataException("No author");
                }
                if (Committer == null)
                {
                    throw new InvalidDataException("No committer");
                }
                Message = all[(n + 1)..];
                return;
            }
            s = all.IndexOf(' ', i, s);
            var value = all.AsSpan()[(s + 1)..n];
            switch (all[i..s])
            {
                case "tree":
                    Tree = value.ParseHex();
                    break;
                case "parent":
                    Parent = value.ParseHex();
                    break;
                case "author":
                    {
                        Author = User.ParseWithDateTimeOffset(value, out var authored);
                        Authored = authored;
                    }
                    break;
                case "committer":
                    {
                        Committer = User.ParseWithDateTimeOffset(value, out var committed);
                        Committed = committed;
                    }
                    break;
            }
            i = n + 1;
        }
    }

    public ReadOnlyMemory<byte> Parent { get; init; } = ReadOnlyMemory<byte>.Empty;

    public bool Equals(CommitContent? other) =>
        other != null && ByteROMEqualityComparer.Instance.Equals(Tree, other.Tree) && Author == other.Author && Authored == other.Authored && Committer == other.Committer && Committed == other.Committed && Message == other.Message;

    public override int GetHashCode() => ByteROMEqualityComparer.Instance.GetHashCode(Tree);

    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append("tree ");
        sb.Append(Tree.ToHexString());
        if (!Parent.IsEmpty)
        {
            sb.Append("\nparent ");
            sb.Append(Parent.ToHexString());
        }
        sb.Append("\nauthor ");
        sb.Append(Author.ToString(Authored));
        sb.Append("\ncommitter ");
        sb.Append(Committer.ToString(Committed));
        sb.Append("\n\n");
        sb.Append(Message);
        return sb.ToString();
    }
}