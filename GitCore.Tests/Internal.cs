using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
            using HashStream hs = new(ms, hashAlgorithm, true);
            hs.Write(raw);
            hs.ComputeHash();
            Assert.Equal(expected, hs.Hash.ToArray());
        }
        {
            using MemoryStream ms = new(raw);
            using HashStream hs = new(ms, hashAlgorithm, true);
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
    public void Sequence()
    {
        var expected = Guid.NewGuid().ToByteArray();
        SequenceStream ss = new(new(expected));
        var actual = new byte[ss.Length];
        ss.Read(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Stack()
    {
        Span<Guid> expected = stackalloc Guid[16];
        for (var i = 0; i < expected.Length; ++i)
        {
            expected[i] = Guid.NewGuid();
        }
        Span<byte> actual = stackalloc byte[Unsafe.SizeOf<Guid>() * expected.Length];
        for (var chunkSize = 1; chunkSize < actual.Length; ++chunkSize)
        {
            actual.Clear();
            using StackStream ss = new(new MemoryStream(expected[^1].ToByteArray()));
            for (var i = expected.Length - 1; i-- > 0;)
            {
                ss.Push(expected[i].ToByteArray());
            }
            for (var read = 0; read < actual.Length; read += ss.Read(actual[read..Math.Min(read + chunkSize, actual.Length)])) { }
            Assert.True(actual.SequenceEqual(MemoryMarshal.Cast<Guid, byte>(expected)));
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