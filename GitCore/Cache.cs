using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FlyByWireless.GitCore;

public sealed class Cache
{
    private readonly string _ha, _suffix;

    private readonly int _hs, _bs;

    public Cache(string name = "GitCore", string hashAlgorithm = nameof(SHA1))
    {
        _ha = hashAlgorithm;
        _suffix = string.Concat(":", name, ".", hashAlgorithm);
        using var ha = HashAlgorithm.Create(hashAlgorithm)!;
        _hs = ha.HashSize / 8;
        _bs = Math.Max(25, 16 + _hs);
    }

    public async Task<ReadOnlyMemory<byte>> HashObjectAsync(FileInfo file, bool fresh = false, CancellationToken cancellationToken = default)
    {
        var path = file.FullName + _suffix;
        if (!fresh)
        {
            try
            {
                var meta = await File.ReadAllBytesAsync(path, cancellationToken);
                if (MemoryMarshal.AsRef<long>(meta.AsSpan(0, 8)) == file.Length &&
                    MemoryMarshal.AsRef<long>(meta.AsSpan(8, 8)) == file.LastWriteTimeUtc.Ticks)
                {
                    return meta.AsMemory(16);
                }
            }
            catch { }
        }
        {
            byte[] hash;
            var buffer = ArrayPool<byte>.Shared.Rent(_bs);
            try
            {
                using var read = File.OpenRead(file.FullName);
                {
                    using var ha = HashAlgorithm.Create(_ha)!;
                    var length = Encoding.ASCII.GetBytes(FormattableString.Invariant($"blob {read.Length}\0"), buffer);
                    ha.TransformBlock(buffer, 0, length, null, 0);
                    hash = await ha.ComputeHashAsync(read, cancellationToken);
                }
                try
                {
                    file.Refresh();
                    MemoryMarshal.AsRef<long>(buffer.AsSpan(0, 8)) = file.Length;
                    MemoryMarshal.AsRef<long>(buffer.AsSpan(8, 8)) = file.LastWriteTimeUtc.Ticks;
                    hash.CopyTo(buffer.AsSpan(16, _hs));
                    try
                    {
                        using var write = File.OpenWrite(path);
                        await write.WriteAsync(buffer.AsMemory(0, 16 + _hs), cancellationToken);
                        await write.FlushAsync(cancellationToken);
                    }
                    finally
                    {
                        File.SetLastWriteTimeUtc(path, file.LastWriteTimeUtc);
                    }
                }
                catch { }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return hash;
        }
    }
}