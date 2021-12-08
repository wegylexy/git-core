using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FlyByWireless.GitCore;

public interface ICache
{
    internal static byte[] Hash(string type, Stack<ReadOnlyMemory<byte>> segments, string hashAlgorithm = nameof(SHA1))
    {
        var size = segments.Sum(m => m.Length);
        using var ha = HashAlgorithm.Create(hashAlgorithm)!;
        {
            var header = Encoding.ASCII.GetBytes($"{type} {size}\0");
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
            return ha.ComputeHash(new SequenceStream(new(start, 0, end, end.Memory.Length)));
        }
        else
        {
            ha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return ha.Hash!;
        }
    }

    public Task<ReadOnlyMemory<byte>> HashBlobAsync(FileInfo file, bool fresh = false, CancellationToken cancellationToken = default);

    public Task<ReadOnlyMemory<byte>> HashTreeAsync(DirectoryInfo directory, Action<FileSystemInfo, ReadOnlyMemory<byte>>? progress = null, bool fresh = false, CancellationToken cancellationToken = default);
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
            using var data = File.OpenRead(file.FullName);
            {
                var buffer = GC.AllocateUninitializedArray<byte>(8000);
                var binary = false;
                var read = 0;
                while (read < 8000)
                {
                    var r = await data.ReadAsync(buffer.AsMemory(read, 8000 - read), cancellationToken);
                    if (r == 0)
                    {
                        break;
                    }
                    if (buffer.AsSpan(read, r).Contains<byte>(0))
                    {
                        binary = true;
                        read += r;
                        break;
                    }
                    read += r;
                }
                if (binary)
                {
                    using var ha = HashAlgorithm.Create(_ha)!;
                    var header = Encoding.ASCII.GetBytes(FormattableString.Invariant($"blob {data.Length}\0"));
                    ha.TransformBlock(header, 0, header.Length, null, 0);
                    ha.TransformBlock(buffer, 0, read, null, 0);
                    hash = await ha.ComputeHashAsync(data, cancellationToken);
                }
                else // skip '\r' in front of '\n'
                {
                    Stack<ReadOnlyMemory<byte>> segments = new();
                    for (var i = 0; i < read;)
                    {
                        var r = buffer.AsSpan(i, read - i).IndexOf<byte>(13);
                        if (r < 0)
                        {
                            segments.Push(buffer.AsMemory(i, read - i));
                            i = 0;
                        }
                        else
                        {
                            if (i + r + 1 < read)
                            {
                                if (buffer[i + r + 1] == 10)
                                {
                                    buffer[i + r++] = 10;
                                    segments.Push(buffer.AsMemory(i, r++));
                                }
                                else
                                {
                                    segments.Push(buffer.AsMemory(i, ++r));
                                }
                                i += r;
                                continue;
                            }
                            segments.Push(buffer.AsMemory(i, r));
                            i = 1;
                        }
                        buffer = GC.AllocateUninitializedArray<byte>(4096);
                        if (i == 1)
                        {
                            buffer[0] = 13;
                        }
                        r = await data.ReadAsync(buffer.AsMemory(i, buffer.Length - i), cancellationToken);
                        if (r == 0)
                        {
                            break;
                        }
                        read = i + r;
                        if (i == 1 && buffer[0] != 10)
                        {
                            i = 0;
                        }
                    }
                    hash = ICache.Hash("blob", segments, _ha);
                }
            }
            {
                var buffer = ArrayPool<byte>.Shared.Rent(16 + _hs);
                try
                {
                    file.Refresh();
                    MemoryMarshal.AsRef<long>(buffer.AsSpan(0, 8)) = file.Length;
                    MemoryMarshal.AsRef<long>(buffer.AsSpan(8, 8)) = file.LastWriteTimeUtc.Ticks;
                    hash.CopyTo(buffer.AsSpan(16, _hs));
                    try
                    {
                        using var meta = File.Create(path);
                        await meta.WriteAsync(buffer.AsMemory(0, 16 + _hs), cancellationToken);
                        await meta.FlushAsync(cancellationToken);
                    }
                    finally
                    {
                        File.SetLastWriteTimeUtc(path, file.LastWriteTimeUtc);
                    }
                }
                catch (IOException) { }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
            return hash;
        }
    }

    public async Task<ReadOnlyMemory<byte>> HashTreeAsync(DirectoryInfo directory, Action<FileSystemInfo, ReadOnlyMemory<byte>>? progress = null, bool fresh = false, CancellationToken cancellationToken = default)
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
                            {
                                var h = await HashBlobAsync(f, fresh, cancellationToken);
                                progress?.Invoke(f, h);
                                segments.Push(h);
                            }
                            break;
                        case DirectoryInfo d:
                            segments.Push(Encoding.UTF8.GetBytes($"40000 {d.Name}\0"));
                            {
                                var h = await HashTreeAsync(d, progress, fresh, cancellationToken);
                                progress?.Invoke(d, h);
                                segments.Push(h);
                            }
                            break;
                        default:
                            throw new NotSupportedException("Unexpected file system info");
                    }
                }
                hash = ICache.Hash("tree", segments, _ha);
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