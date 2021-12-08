using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace FlyByWireless.GitCore.Tests;

public class Caching
{
    [Fact]
    public async Task HashBlobAsync()
    {
        NtfsCache cache = new("Test", nameof(SHA1));
        var temp = Path.GetTempFileName();
        try
        {
            var raw = Guid.NewGuid().ToByteArray();
            await File.WriteAllBytesAsync(temp, raw);
            FileInfo file = new(temp);
            var written = file.LastWriteTimeUtc;
            // new
            var expected = (await cache.HashBlobAsync(file)).ToArray();
            var path = temp + ":Test." + nameof(SHA1);
            Assert.True(File.Exists(path));
            {
                var prefixed = GC.AllocateUninitializedArray<byte>(24);
                raw.CopyTo(prefixed, Encoding.ASCII.GetBytes("blob 16\0", prefixed));
                Assert.Equal(SHA1.HashData(prefixed), expected);
            }
            Assert.Equal(written, File.GetLastWriteTimeUtc(temp));
            // cache
            Assert.Equal(expected, (await cache.HashBlobAsync(file)).ToArray());
            Assert.Equal(written, File.GetLastWriteTimeUtc(temp));
            // hack
            {
                using var write = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.Read);
                write.Position = 16;
                await write.WriteAsync(Guid.NewGuid().ToByteArray());
                await write.FlushAsync();
            }
            File.SetLastWriteTimeUtc(temp, written);
            Assert.NotEqual(expected, (await cache.HashBlobAsync(file)).ToArray());
            // corrupt
            {
                using var write = File.Open(path, FileMode.Truncate, FileAccess.Write, FileShare.Read);
                await write.WriteAsync(Guid.NewGuid().ToByteArray());
                await write.FlushAsync();
            }
            File.SetLastWriteTimeUtc(temp, written);
            Assert.Equal(expected, (await cache.HashBlobAsync(file)).ToArray());
            // fresh
            Assert.Equal(expected, (await cache.HashBlobAsync(file, true)).ToArray());
            Assert.Equal(written, File.GetLastWriteTimeUtc(temp));
        }
        finally
        {
            File.Delete(temp);
        }
    }

    [Fact]
    public async Task HashTreeAsync()
    {
        var path = Path.Join(Path.GetTempPath(), "GitCore.Tests." + Guid.NewGuid().ToString("n"));
        try
        {
            Directory.CreateDirectory(path);
            NtfsCache cache = new("Test", nameof(SHA1));
            // new
            {
                var none = true;
                Assert.Equal("4b825dc642cb6eb9a060e54bf8d69288fbee4904", (await cache.HashTreeAsync(new(path), (_, _) => none = false)).ToHexString());
                Assert.True(none);
            }
            File.Copy("../../../../.gitattributes", Path.Join(path, ".gitattributes"));
            File.Copy("../../../../.gitignore", Path.Join(path, ".gitignore"));
            // changed
            {
                var none = true;
                Assert.Equal("4b825dc642cb6eb9a060e54bf8d69288fbee4904", (await cache.HashTreeAsync(new(path), (_, _) => none = false)).ToHexString());
                Assert.True(none);
            }
            // fresh
            {
                Dictionary<ReadOnlyMemory<byte>, string> hashes = new(ByteROMEqualityComparer.Instance);
                Assert.Equal("f0d3a70ceaa69fb70811f58254dc738e0f939eac", (await cache.HashTreeAsync(new(path), (fsi, h) => hashes[h] = fsi.Name, true)).ToHexString());
                Assert.Equal(2, hashes.Count);
                Assert.Equal(".gitattributes", hashes["1ff0c423042b46cb1d617b81efb715defbe8054d".ParseHex()]);
                Assert.Equal(".gitignore", hashes["9491a2fda28342ab358eaf234e1afe0c07a53d62".ParseHex()]);
            }
            // cache
            {
                var none = true;
                Assert.Equal("f0d3a70ceaa69fb70811f58254dc738e0f939eac", (await cache.HashTreeAsync(new(path), (_, _) => none = false)).ToHexString());
                Assert.True(none);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (DirectoryNotFoundException) { }
        }
    }
}