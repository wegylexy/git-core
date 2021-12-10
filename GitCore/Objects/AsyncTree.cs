using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace FlyByWireless.GitCore;

[Flags]
public enum TreeEntryType
{
    Link = 0b0010,
    Tree = 0b0100,
    Blob = 0b1000,
    SymbolicLink = Blob | Link,
    GitLink = Tree | SymbolicLink
}

public readonly record struct TreeEntry(int Mode, string Path, ReadOnlyMemory<byte> Hash)
{
    public TreeEntry(TreeEntryType type, int permission, string path, ReadOnlyMemory<byte> hash) :
        this(((int)type << 12) | permission, path, hash)
    { }

    public TreeEntryType Type => (TreeEntryType)(Mode >> 12);

    public int Permission => Mode & 0b111_111_111;

    public override string ToString() =>
        $"{Convert.ToString(Mode, 8).PadLeft(6, '0')} {(Type.HasFlag(TreeEntryType.Tree) ? "tree" : "blob")} {Hash.ToHexString()}\t{Path}";
}

public sealed class AsyncTree : IAsyncEnumerable<TreeEntry>
{
    public static async IAsyncEnumerable<TreeEntry> EnumerateAsync(string path, bool recusrive = false, string hashAlgorithm = nameof(SHA1), int bufferSize = 4096, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var file = File.Open(path, new FileStreamOptions
        {
            Access = FileAccess.Read,
            BufferSize = bufferSize,
            Mode = FileMode.Open,
            Options = FileOptions.Asynchronous,
            Share = FileShare.Read
        });
        using ZLibStream zls = new(file, CompressionMode.Decompress);
        var size = 0;
        {
            // Asserts type
            var buffer = GC.AllocateUninitializedArray<byte>(5);
            if (await zls.ReadAsync(buffer.AsMemory(0, 5), cancellationToken) != 5)
            {
                throw new EndOfStreamException();
            }
            if (buffer[4] != 0x20 || BitConverter.ToInt32(buffer.AsSpan(0, 4)) != (BitConverter.IsLittleEndian ? 0x65_65_72_74 : 0x74_72_65_65))
            {
                throw new InvalidDataException("Invalid tree");
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
        }
        AsyncTree tree = new(size, zls, hashAlgorithm);
        await foreach (var e in tree)
        {
            if (recusrive && e.Type == TreeEntryType.Tree)
            {
                var hex = e.Hash.ToHexString();
                await foreach (var f in EnumerateAsync(Path.Join(path, "../..", hex.AsSpan(0, 2), hex.AsSpan(2)), true, hashAlgorithm, bufferSize, cancellationToken))
                {
                    yield return f with
                    {
                        Path = $"{e.Path}/{f.Path}"
                    };
                }
            }
            else
            {
                yield return e;
            }
        }
    }

    private long _size;

    private readonly StackStream _stream;

    private readonly int _hashSize;

    public AsyncTree(Stream stream, string hashAlgorithm = nameof(SHA1)) : this(stream.Length, stream, hashAlgorithm) { }

    public AsyncTree(long size, Stream stream, string hashAlgorithm = nameof(SHA1))
    {
        _size = size;
        _stream = stream is StackStream ss ? ss : new(stream, true);
        using var ha = HashAlgorithm.Create(hashAlgorithm)!;
        _hashSize = ha.HashSize / 8;
    }

    public async IAsyncEnumerator<TreeEntry> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(Math.Max(_hashSize, 256));
        // Enumerate entries
        while (_size > 0)
        {
            // Reads mode
            var mode = 0;
            for (var read = 0; ;)
            {
                var r = await _stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
                if (r == 0)
                {
                    throw new EndOfStreamException();
                }
                --_size;
                ++read;
                if (buffer[0] == 0x20)
                {
                    break;
                }
                var o = buffer[0] - '0';
                if (o is < 0 || o > 7)
                {
                    throw new InvalidDataException("Invalid mode");
                }
                mode = (mode << 3) | o;
            }
            // Reads name
            string name;
            for (var read = 0; ;)
            {
                var m = buffer.AsMemory(read, (int)Math.Min(buffer.Length, _size - _hashSize) - read);
                if (m.IsEmpty)
                {
                    throw new NotSupportedException("Name too long");
                }
                var r = await _stream.ReadAsync(m, cancellationToken);
                if (r == 0)
                {
                    throw new EndOfStreamException();
                }
                var i = m.Span.IndexOf<byte>(0);
                if (i >= 0)
                {
                    name = Encoding.UTF8.GetString(m.Span[..(read += i)]);
                    _stream.Push(m.Span[(read + 1)..].ToArray());
                    _size -= i + 1;
                    break;
                }
                _size -= r;
                read += r;
            }
            // Reads hash
            for (var read = 0; read < _hashSize;)
            {
                var r = await _stream.ReadAsync(buffer.AsMemory(read, _hashSize - read), cancellationToken);
                if (r == 0)
                {
                    throw new EndOfStreamException();
                }
                _size -= r;
                read += r;
            }
            yield return new(mode, name, buffer.AsSpan(0, _hashSize).ToArray());
        }
    }
}