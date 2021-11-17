using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FlyByWireless.GitCore.Tests;

public class Packs
{
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

    [Fact]
    public async Task UploadPackRequestEmptyAsync()
    {
        using UploadPackRequest upr = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() => upr.LoadIntoBufferAsync());
    }

    [Theory]
    [InlineData(nameof(SHA1))]
    [InlineData(nameof(SHA256))]
    public async Task UploadPackRequestWantAsync(string hashAlgorithm)
    {
        using var ha = HashAlgorithm.Create(hashAlgorithm)!;
        for (var i = 1; i < 4; ++i)
        {
            var ids = Enumerable.Range(0, i).Select(_ => ha.ComputeHash(Guid.NewGuid().ToByteArray())).ToList();
            using UploadPackRequest upr = new(ids.Select(id => (ReadOnlyMemory<byte>)id).ToArray());
            Assert.Equal("application/x-git-upload-pack-request", upr.Headers.ContentType!.MediaType);
            StringBuilder sb = new(ids.Count * (10 + ha.HashSize / 4) + 13);
            var prefix = $"{10 + ha.HashSize / 4:x4}want ";
            foreach (var id in ids)
            {
                sb.Append(prefix);
                sb.Append(id.ToHexString());
                sb.Append('\n');
            }
            sb.Append("00000009done\n");
            Assert.Equal(sb.ToString(), await upr.ReadAsStringAsync());
        }
    }
}