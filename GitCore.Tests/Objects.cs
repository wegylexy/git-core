using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace FlyByWireless.GitCore.Tests;

public class Objects
{
    private readonly ITestOutputHelper _output;

    public Objects(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(nameof(SHA1))]
    [InlineData(nameof(SHA256))]
    public async Task UnpackAsync(string hashAlgorithm)
    {
        var objects = Enumerable.Range(0, byte.MaxValue).Select(_ => Guid.NewGuid().ToByteArray()).Select(data =>
        {
            using var ha = HashAlgorithm.Create(hashAlgorithm)!;
            {
                var prolog = Encoding.ASCII.GetBytes(FormattableString.Invariant($"blob {data.Length}\0"));
                ha.TransformBlock(prolog, 0, prolog.Length, null, 0);
            }
            ha.TransformFinalBlock(data, 0, data.Length);
            return new
            {
                Hash = ha.Hash!,
                Data = data
            };
        }).ToList();
        objects.Sort((a, b) =>
            a.Hash == b.Hash ? 0 : a.Hash.AsSpan().SequenceCompareTo(b.Hash));
        using MemoryStream ms = new();
        using HashStream hs = new(ms, hashAlgorithm, null, true);
        {
            {
                var buffer = GC.AllocateUninitializedArray<byte>(12);
                MemoryMarshal.AsRef<int>(buffer.AsSpan(0, 4)) = BitConverter.IsLittleEndian ? 0x4b_43_41_50 : 0x50_41_43_4b;
                MemoryMarshal.AsRef<int>(buffer.AsSpan(4, 4)) = BitConverter.IsLittleEndian ? 2 << 24 : 2;
                MemoryMarshal.AsRef<int>(buffer.AsSpan(8, 4)) = BitConverter.IsLittleEndian ? objects.Count << 24 : objects.Count;
                hs.Write(buffer);
            }
            using BinaryWriter writer = new(hs, Encoding.ASCII, true);
            foreach (var o in objects)
            {
                writer.Write((byte)0b10110000);
                writer.Write((byte)0b00000001);
                using ZLibStream zls = new(hs, CompressionLevel.SmallestSize, true);
                zls.Write(o.Data);
                zls.Flush();
            }
            hs.ComputeHash();
            ms.Write(hs.Hash.Span);
            ms.Position = 0;
        }
        AsyncPack rop = new(ms, hashAlgorithm);
        Assert.Equal(objects.Count, await rop.CountAsync());
        {
            var i = 0;
            await foreach (var u in rop)
            {
                var o = objects[i];
                Assert.True(u.Hash.Span.SequenceEqual(o.Hash));
                Assert.Equal(o.Data, u.Data.ToArray());
                ++i;
            }
        }
    }

    [Theory]
    [InlineData("f0d3a70ceaa69fb70811f58254dc738e0f939eac")]
    [InlineData("d56c74a8ae5d81ddfbebce18eea3c791fcea5e2d")]
    public async Task TreeAsync(string hex)
    {
        var count = 0;
        await foreach (var e in AsyncTree.EnumerateAsync(Path.Join("../../../../.git/objects", hex.AsSpan(0, 2), hex.AsSpan(2)), true))
        {
            _output.WriteLine(e.ToString());
            ++count;
            if (hex == "f0d3a70ceaa69fb70811f58254dc738e0f939eac")
            {
                switch (e.Path)
                {
                    case ".gitattributes":
                        Assert.Equal("1ff0c423042b46cb1d617b81efb715defbe8054d", e.Hash.ToHexString());
                        break;
                    case ".gitignore":
                        Assert.Equal("9491a2fda28342ab358eaf234e1afe0c07a53d62", e.Hash.ToHexString());
                        break;
                }
            }
        }
        Assert.True(count > 1);
    }

    [Fact]
    public void Commit()
    {
        User user = new("Alice Bob", "bob.alice@example.com");
        CommitContent expected = new(new byte[] { 2 },
            user, new DateTimeOffset(2020, 9, 12, 16, 0, 0, new(8, 0, 0)),
            user, new DateTimeOffset(2020, 9, 12, 8, 0, 0, default),
            "Hello, world!\nHow are you?"
        )
        {
            Parent = new byte[] { 1 }
        };
        var s = expected.ToString();
        Assert.DoesNotContain('\r', s);
        CommitContent actual = new(new(Encoding.UTF8.GetBytes(s)));
        Assert.Equal(expected, actual);
        Assert.Equal(s, actual.ToString());
    }

    [Theory]
    [InlineData("4143d259f0e175c2d7a0bec79b5c0dbf4262e284", "f0d3a70ceaa69fb70811f58254dc738e0f939eac", "", "Add .gitattributes and .gitIgnore.\n")]
    [InlineData("1eca966549be4680a7a33a8027cedd28479d4c97", "d56c74a8ae5d81ddfbebce18eea3c791fcea5e2d", "4143d259f0e175c2d7a0bec79b5c0dbf4262e284", "Add project files.\n")]
    public async Task CommitAsync(string commit, string tree, string parent, string message, CancellationToken cancellationToken = default)
    {
        var c = await CommitContent.UncompressAsync(Path.Join("../../../../.git/objects", commit.AsSpan(0, 2), commit.AsSpan(2)), cancellationToken);
        Assert.Equal(tree, c.Tree.ToHexString());
        Assert.Equal(parent, c.Parent.ToHexString());
        Assert.Equal(message, c.Message);
    }
}