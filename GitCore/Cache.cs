using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FlyByWireless.GitCore;

public interface ICache
{
    public Task<ReadOnlyMemory<byte>> HashBlobAsync(FileInfo file, bool fresh = false, CancellationToken cancellationToken = default);

    public Task<ReadOnlyMemory<byte>> HashTreeAsync(DirectoryInfo directory, bool fresh = false, CancellationToken cancellationToken = default);
}

public sealed class NtfsCache : ICache
{
    private readonly string _ha, _suffix;

    private readonly int _hs;

    public NtfsCache(string name = "GitCore", string hashAlgorithm = nameof(SHA1))
    {
        _ha = hashAlgorithm;
        _suffix = string.Concat(":", name, ".", hashAlgorithm);
        using var ha = HashAlgorithm.Create(hashAlgorithm)!;
        _hs = ha.HashSize / 8;
    }

    public async Task<ReadOnlyMemory<byte>> HashBlobAsync(FileInfo file, bool fresh = false, CancellationToken cancellationToken = default)
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
            catch (FileNotFoundException) { }
        }
        {
            byte[] hash;
            var buffer = ArrayPool<byte>.Shared.Rent(Math.Max(25, 16 + _hs));
            try
            {
                using var read = File.OpenRead(file.FullName); // TODO: normalize EOL in case of plain text
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
                        using var write = File.Create(path);
                        await write.WriteAsync(buffer.AsMemory(0, 16 + _hs), cancellationToken);
                        await write.FlushAsync(cancellationToken);
                    }
                    finally
                    {
                        File.SetLastWriteTimeUtc(path, file.LastWriteTimeUtc);
                    }
                }
                catch (IOException) { }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return hash;
        }
    }

    public async Task<ReadOnlyMemory<byte>> HashTreeAsync(DirectoryInfo directory, bool fresh = false, CancellationToken cancellationToken = default)
    {
        var path = directory.FullName + _suffix;
        if (!fresh)
        {
            try
            {
                return await File.ReadAllBytesAsync(path, cancellationToken);
            }
            catch (FileNotFoundException) { }
        }
        {
            byte[] hash;
            var written = directory.LastWriteTimeUtc;
            {
                Stack<ReadOnlyMemory<byte>> segments = new();
                foreach (var info in directory.EnumerateFileSystemInfos().OrderBy(i => i.Name))
                {
                    if (info.LinkTarget != null)
                    {
                        throw new NotSupportedException("Link not supported");
                    }
                    switch (info)
                    {
                        case FileInfo f:
                            segments.Push(Encoding.UTF8.GetBytes($"100644 {f.Name}\0"));
                            segments.Push(await HashBlobAsync(f, fresh, cancellationToken));
                            break;
                        case DirectoryInfo d:
                            segments.Push(Encoding.UTF8.GetBytes($"40000 {d.Name}\0"));
                            segments.Push(await HashTreeAsync(d, fresh, cancellationToken));
                            break;
                        default:
                            throw new NotSupportedException("Unexpected file system info");
                    }
                }
                var size = segments.Sum(m => m.Length);
                using var ha = HashAlgorithm.Create(_ha)!;
                {
                    var header = Encoding.ASCII.GetBytes($"tree {size}\0");
                    ha.TransformBlock(header, 0, header.Length, null, 0);
                }
                if (size > 0)
                {
                    var runningIndex = size;
                    var last = segments.Pop();
                    BytesSegment end = new(last, null, runningIndex -= last.Length), start = end;
                    while (segments.TryPop(out var previous))
                    {
                        start = new(previous, start, runningIndex -= previous.Length);
                    }
                    hash = ha.ComputeHash(new SequenceStream(new(start, 0, end, end.Memory.Length)));
                }
                else
                {
                    ha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    hash = ha.Hash!;
                }
            }
            try
            {
                try
                {
                    await File.WriteAllBytesAsync(path, hash, cancellationToken);
                }
                finally
                {
                    File.SetLastWriteTimeUtc(path, written);
                }
            }
            catch (IOException) { }
            return hash;
        }
    }
}