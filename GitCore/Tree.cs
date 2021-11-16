﻿using System.IO.Compression;
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
    public TreeEntryType Type => (TreeEntryType)(Mode >> 12);

    public int Permission => Mode & 0b111_111_111;

    public override string ToString() =>
        $"{Convert.ToString(Mode, 8).PadLeft(6, '0')} {(Type.HasFlag(TreeEntryType.Tree) ? "tree" : "blob")} {Hash.ToHexString()}\t{Path}";
}

public class Tree : IAsyncEnumerable<TreeEntry>
{
    public static async IAsyncEnumerable<TreeEntry> EnumerateAsync(string path, bool recusrive = false)
    {
        using var file = File.OpenRead(path);
        using ZLibStream zls = new(file, CompressionMode.Decompress);
        Tree tree = new(zls, nameof(SHA1));
        await foreach (var e in tree)
        {
            if (recusrive && e.Type == TreeEntryType.Tree)
            {
                var hex = e.Hash.ToHexString();
                await foreach (var f in EnumerateAsync(Path.Join(path, "../..", hex.AsSpan(0, 2), hex.AsSpan(2)), true))
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

    private readonly StackStream _stream;

    private readonly int _hashSize;

    public Tree(Stream stream, string hashAlgorithm = nameof(SHA1))
    {
        _stream = stream is StackStream ss ? ss : new(stream, true);
        using var ha = HashAlgorithm.Create(hashAlgorithm)!;
        _hashSize = ha.HashSize / 8;
    }

    public async IAsyncEnumerator<TreeEntry> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(Math.Max(_hashSize, 256));
        if (await _stream.ReadAsync(buffer.AsMemory(0, 5), cancellationToken) != 5)
        {
            throw new EndOfStreamException();

        }
        if (buffer[4] != 0x20 || BitConverter.ToInt32(buffer.AsSpan(0, 4)) != (BitConverter.IsLittleEndian ? 0x65_65_72_74 : 0x74_72_65_65))
        {
            throw new InvalidDataException("Invalid tree");
        }
        // Reads size
        var size = 0;
        for (var i = 0; ; ++i)
        {
            if (await _stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken) != 1)
            {
                throw new EndOfStreamException();
            }
            if (buffer[0] == 0)
            {
                break;
            }
            size = size * 10 + (buffer[0] - '0');
        }
        // Enumerate entries
        while (size > 0)
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
                --size;
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
                var m = buffer.AsMemory(read, Math.Min(buffer.Length, size - _hashSize) - read);
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
                    size -= i + 1;
                    break;
                }
                size -= r;
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
                size -= r;
                read += r;
            }
            yield return new(mode, name, buffer.AsSpan(0, _hashSize).ToArray());
        }
    }
}