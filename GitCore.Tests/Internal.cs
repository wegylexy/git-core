using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;

namespace FlyByWireless.GitCore.Tests;

public class Internal
{
    [Theory]
    [InlineData(nameof(SHA1))]
    [InlineData(nameof(SHA256))]
    public void Hash(string hashAlgorithm)
    {
        var raw = Guid.NewGuid().ToByteArray();
        byte[] expected;
        {
            using var ha = HashAlgorithm.Create(hashAlgorithm)!;
            ha.ComputeHash(raw);
            expected = ha.Hash!;
        }
        {
            using MemoryStream ms = new();
            using HashStream hs = new(ms, hashAlgorithm, null, true);
            hs.Write(raw);
            hs.ComputeHash();
            Assert.Equal(expected, hs.Hash.ToArray());
        }
        {
            using MemoryStream ms = new(raw);
            using HashStream hs = new(ms, hashAlgorithm, null, true);
            var buffer = GC.AllocateUninitializedArray<byte>(raw.Length);
            hs.Read(buffer);
            hs.Unread(4);
            ms.Seek(-4, SeekOrigin.Current);
            hs.Read(buffer.AsSpan(0, 4));
            hs.ComputeHash();
            Assert.Equal(expected, hs.Hash.ToArray());
        }
    }

    [Fact]
    public void Inflate()
    {
        Span<Guid> expected = stackalloc Guid[16];
        for (var i = 0; i < expected.Length; ++i)
        {
            expected[i] = Guid.NewGuid();
        }
        using MemoryStream deflated = new();
        for (var i = 0; i < expected.Length; ++i)
        {
            using ZLibStream zls = new(deflated, CompressionLevel.SmallestSize, true);
            zls.Write(expected[i].ToByteArray());
            zls.Flush();
        }
        deflated.Position = 0;
        using StackStream ss = new(deflated);
        Span<byte> actual = stackalloc byte[16];
        for (var i = 0; i < expected.Length; ++i)
        {
            using ZLibStream zls = new(ss, CompressionMode.Decompress, true);
            var read = zls.Read(actual);
            Assert.Equal(16, read);
            Assert.Equal(expected[i], new(actual));
            ss.Push(zls.GetInputBuffer());
        }
        Assert.Equal(-1, ss.ReadByte());
    }
}