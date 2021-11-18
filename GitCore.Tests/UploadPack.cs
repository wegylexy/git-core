using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FlyByWireless.GitCore.Tests;

public class UploadPack
{
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
            Assert.Contains(upr.Capabilities, c => c == "thin-pack");
            StringBuilder sb = new(ids.Count * (10 + ha.HashSize / 4) + 13);
            var e = ids.GetEnumerator();
            Assert.True(e.MoveNext());
            {
                var l = upr.Capabilities.Sum(c => 1 + c.Length);
                var prefix = $"{10 + ha.HashSize / 4 + l:x4}want ";
                sb.Append(prefix);
                sb.Append(e.Current.ToHexString());
                foreach (var c in upr.Capabilities)
                {
                    sb.Append(' ');
                    sb.Append(c);
                }
                sb.Append('\n');
            }
            while (e.MoveNext())
            {
                var prefix = $"{10 + ha.HashSize / 4:x4}want ";
                sb.Append(prefix);
                sb.Append(e.Current.ToHexString());
                sb.Append('\n');
            }
            sb.Append("00000009done\n");
            Assert.Equal(sb.ToString(), (await upr.ReadAsStringAsync()));
        }
    }
}