using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FlyByWireless.GitCore;

public sealed record class User(string Name, string Email);

public sealed record class CommitContent(ReadOnlyMemory<byte> Tree, User Author, DateTimeOffset Authored, User Committer, DateTimeOffset Committed, string Message)
{
    public static async Task<CommitContent> UncompressAsync(string path, CancellationToken cancellationToken = default)
    {
        using var file = File.OpenRead(path);
        using ZLibStream zls = new(file, CompressionMode.Decompress);
        var size = 0;
        {
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
            (User, DateTimeOffset) NETO()
            {
                var b = all.IndexOf('<', s, n - s);
                var e = all.IndexOf('>', b + 1, n - 2 - b);
                var tzo = all!.LastIndexOf(' ', n - 1, n - 2 - e);
                var o = new TimeSpan(int.Parse(all.AsSpan(tzo + 1, 3)), int.Parse(all.AsSpan(tzo + 4, 2)), 0);
                var tso = all.LastIndexOf(' ', tzo - 1, tzo - 1 - e);
                return
                (
                    new(new(all.AsSpan(s, b - s).TrimEnd()), new(all.AsSpan(b + 1, e - 1 - b))),
                    new(DateTime.SpecifyKind(DateTimeOffset.FromUnixTimeSeconds(long.Parse(all.AsSpan(tso + 1, tzo - tso - 1))).UtcDateTime.Add(o), DateTimeKind.Unspecified), o)
                );
            }
            switch (all[i..s++])
            {
                case "tree":
                    Tree = all.AsSpan(s, n - s).ParseHex();
                    break;
                case "parent":
                    Parent = all.AsSpan(s, n - s).ParseHex();
                    break;
                case "author":
                    (Author, Authored) = NETO();
                    break;
                case "committer":
                    (Committer, Committed) = NETO();
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
        sb.Append('\n');
        if (!Parent.IsEmpty)
        {
            sb.Append("parent ");
            sb.Append(Parent.ToHexString());
            sb.Append('\n');
        }
        void NETO(User user, DateTimeOffset dto)
        {
            sb.Append(user.Name);
            sb.Append(" <");
            sb.Append(user.Email);
            sb.Append("> ");
            sb.Append(dto.ToUnixTimeSeconds());
            sb.Append(' ');
            sb.Append(dto.Offset < TimeSpan.Zero ? '-' : '+');
            sb.Append(dto.Offset.ToString("hhmm"));
            sb.Append('\n');
        }
        sb.Append("author ");
        NETO(Author, Authored);
        sb.Append("committer ");
        NETO(Committer, Committed);
        sb.Append('\n');
        sb.Append(Message);
        return sb.ToString();
    }
}