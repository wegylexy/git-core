using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FlyByWireless.GitCore.Tests;

public class Public
{
    [Fact]
    public void HexString()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        Assert.Equal(string.Join(string.Empty, bytes.Select(b => b.ToString("x2"))), bytes.ToHexString());
    }

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
        using MemoryStream pack = new();
        {
            using var haAll = HashAlgorithm.Create(hashAlgorithm)!;
            using BinaryWriter writer = new(pack, Encoding.ASCII, true);
            writer.Write(BitConverter.IsLittleEndian ? 0x4b_43_41_50 : 0x50_41_43_4b);
            writer.Write(BitConverter.IsLittleEndian ? 2 << 24 : 2);
            writer.Write(BitConverter.IsLittleEndian ? objects.Count << 24 : objects.Count);
            writer.Flush();
            foreach (var o in objects)
            {
                writer.Write((byte)0b10110000);
                writer.Write((byte)0b00000001);
                using ZLibStream zls = new(pack, CompressionLevel.SmallestSize, true);
                zls.Write(o.Data);
                zls.Flush();
                haAll.TransformBlock(o.Hash, 0, o.Hash.Length, null, 0);
            }
            haAll.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            pack.Write(haAll.Hash);
            pack.Flush();
            pack.Position = 0;
        }
        ReadOnlyPack rop = new(pack, hashAlgorithm: hashAlgorithm);
        Assert.Equal(objects.Count, await rop.CountAsync());
        {
            var i = 0;
            await foreach (var u in rop)
            {
                var o = objects[i];
                Assert.Equal(o.Data.Length, u.Size);
                Assert.True(u.Hash.Span.SequenceEqual(o.Hash));
                Assert.Equal(o.Data, u.Data.ToArray());
                ++i;
            }
        }
    }
}