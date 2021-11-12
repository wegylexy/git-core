using System.IO.Compression;
using System.Security.Cryptography;
using Xunit;
using Xunit.Abstractions;

namespace FlyByWireless.GitCore.Tests;

public class Runtime
{
    readonly ITestOutputHelper _output;

    public Runtime(ITestOutputHelper output) => _output = output;

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
        Span<byte> actual = stackalloc byte[16];
        for (var i = 0; i < expected.Length; ++i)
        {
            ReadOnlyMemory<byte> rom;
            {
                using ZLibStream zls = new(deflated, CompressionMode.Decompress, true);
                var read = zls.Read(actual);
                Assert.Equal(16, read);
                Assert.Equal(expected[i], new(actual));
                rom = zls.GetInputBuffer();
            }
            deflated.Seek(-rom.Length, SeekOrigin.Current);
            _output.WriteLine(FormattableString.Invariant($"Sought back {rom.Length} bytes"));
        }
        Assert.Equal(deflated.Length, deflated.Position);
    }

    [Theory]
    [InlineData(nameof(SHA1))]
    [InlineData(nameof(SHA256))]
    public void Hash(string algorithm)
    {
        ReadOnlySpan<byte> expected = new();
        var data = Guid.NewGuid().ToByteArray();
        {
            using var ha = HashAlgorithm.Create(algorithm)!;
            _output.WriteLine($"{ha.HashSize / 8} bytes");
            expected = ha.ComputeHash(data);
        }
        byte[] actual;
        {
            using var ha = HashAlgorithm.Create(algorithm)!;
            ha.TransformBlock(data, 0, 16, null, 0);
            ha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            actual = ha.Hash!;
        }
        Assert.True(actual.AsSpan().SequenceEqual(expected));
        {
            using var ha = HashAlgorithm.Create(algorithm)!;
            ha.TransformBlock(data, 0, 8, null, 0);
            ha.TransformBlock(data, 8, 8, null, 0);
            ha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            actual = ha.Hash!;
        }
        Assert.True(actual.AsSpan().SequenceEqual(expected));
    }
}