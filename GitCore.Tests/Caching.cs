using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FlyByWireless.GitCore.Tests;

public class Caching
{
    [Fact]
    public async Task HashObjectAsync()
    {
        Cache cache = new("Test", nameof(SHA1));
        var temp = Path.GetTempFileName();
        try
        {
            var raw = Guid.NewGuid().ToByteArray();
            await File.WriteAllBytesAsync(temp, raw);
            FileInfo file = new(temp);
            var written = file.LastWriteTimeUtc;
            // new
            var expected = (await cache.HashObjectAsync(file)).ToArray();
            var path = temp + ":Test." + nameof(SHA1);
            Assert.True(File.Exists(path));
            {
                var prefixed = GC.AllocateUninitializedArray<byte>(24);
                raw.CopyTo(prefixed, Encoding.ASCII.GetBytes("blob 16\0", prefixed));
                Assert.Equal(SHA1.HashData(prefixed), expected);
            }
            Assert.Equal(written, File.GetLastWriteTimeUtc(temp));
            // cache
            Assert.Equal(expected, (await cache.HashObjectAsync(file)).ToArray());
            Assert.Equal(written, File.GetLastWriteTimeUtc(temp));
            // hack
            {
                using var write = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                write.Position = 16;
                await write.WriteAsync(Guid.NewGuid().ToByteArray());
                await write.FlushAsync();
            }
            File.SetLastWriteTimeUtc(temp, written);
            Assert.NotEqual(expected, (await cache.HashObjectAsync(file)).ToArray());
            // corrupt
            {
                using var write = File.Open(path, FileMode.Truncate, FileAccess.Write, FileShare.Read);
                await write.WriteAsync(Guid.NewGuid().ToByteArray());
                await write.FlushAsync();
            }
            File.SetLastWriteTimeUtc(temp, written);
            Assert.Equal(expected, (await cache.HashObjectAsync(file)).ToArray());
            // fresh
            Assert.Equal(expected, (await cache.HashObjectAsync(file, true)).ToArray());
            Assert.Equal(written, File.GetLastWriteTimeUtc(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }
}