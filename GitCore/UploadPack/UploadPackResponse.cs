using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace FlyByWireless.GitCore;

public sealed class UploadPackResponse : IDisposable
{
    internal static async Task<UploadPackResponse> RequestAsync(HttpClient client, Uri repo, UploadPackRequest request, string hashAlgorithm = nameof(SHA1), CancellationToken cancellationToken = default)
    {
        int hashSize;
        {
            using var ha = HashAlgorithm.Create(hashAlgorithm)!;
            hashSize = ha.HashSize / 8;
        }
        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, new Uri(repo, "git-upload-pack"))
        {
            Content = request
        }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.EnsureSuccessStatusCode().Content.Headers.ContentType is not { MediaType: "application/x-git-upload-pack-result" })
        {
            throw new InvalidOperationException("Unexpected media type");
        }
        StackStream ss = new(await response.Content.ReadAsStreamAsync(cancellationToken), true);
        HashSet<ReadOnlyMemory<byte>>
            acknowledged = new(ByteROMEqualityComparer.Instance),
            shallow = new(ByteROMEqualityComparer.Instance),
            unshallow = new(ByteROMEqualityComparer.Instance);
        var buffer = ArrayPool<byte>.Shared.Rent(9 + hashSize * 2);
        try
        {
            for (var PACK = BitConverter.IsLittleEndian ? 0x4b_43_41_50 : 0x50_41_43_4b; ;)
            {
                for (var read = 0; read < 4;)
                {
                    var r = await ss.ReadAsync(buffer.AsMemory(read, 4 - read), cancellationToken);
                    if (r == 0)
                    {
                        throw new EndOfStreamException();
                    }
                    read += r;
                }
                if (BitConverter.ToInt32(buffer) == PACK)
                {
                    ss.Push(buffer.AsMemory(0, 4));
                    break;
                }
                var size =
                    (((int)buffer[0]).ParseHex() << 12) |
                    (((int)buffer[1]).ParseHex() << 8) |
                    (((int)buffer[2]).ParseHex() << 4) |
                    ((int)buffer[3]).ParseHex();
                if (size > 4)
                {
                    size -= 4;
                    if (buffer.Length < size)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = ArrayPool<byte>.Shared.Rent(size);
                    }
                    for (var read = 0; read < size;)
                    {
                        var r = await ss.ReadAsync(buffer.AsMemory(read, size - read), cancellationToken);
                        if (r == 0)
                        {
                            throw new EndOfStreamException();
                        }
                        read += r;
                    }
                    if (size > 2)
                    {
                        if (buffer[2] == 0x4b && buffer[size - 1] == 10)
                        {
                            if (size == 4 && buffer[0] == 0x4e && buffer[1] == 0x41) // "NAK"
                            {
                                break;
                            }
                            if (buffer[0] == 0x41 && buffer[1] == 0x43) // "ACK"
                            {
                                if (size < 5 + hashSize * 2 || buffer[3] != 0x20)
                                {
                                    throw new InvalidDataException("Invalid ACK");
                                }
                                acknowledged.Add(((ReadOnlySpan<byte>)buffer.AsSpan(4, hashSize * 2)).ParseHex());
                                continue;
                            }
                        }
                        else if (size == 8 + hashSize * 2 && buffer[7] == 0x20 && buffer[0] == 0x73 && buffer[1] == 0x68 && buffer[2] == 0x61 && buffer[3] == 0x6c && buffer[4] == 0x6c && buffer[5] == 0x6f && buffer[6] == 0x77) // "shallow "
                        {
                            shallow.Add(((ReadOnlySpan<byte>)buffer.AsSpan(8, hashSize * 2)).ParseHex());
                            continue;
                        }
                        else if (size == 10 + hashSize * 2 && buffer[9] == 0x20 && buffer[0] == 0x75 && buffer[1] == 0x6e && buffer[2] == 0x73 && buffer[3] == 0x68 && buffer[4] == 0x61 && buffer[5] == 0x6c && buffer[6] == 0x6c && buffer[7] == 0x6f && buffer[8] == 0x77) // "unshallow "
                        {
                            unshallow.Add(((ReadOnlySpan<byte>)buffer.AsSpan(10, hashSize * 2)).ParseHex());
                            continue;
                        }
                    }
                    throw new InvalidOperationException(Encoding.UTF8.GetString(buffer.AsSpan(0, size)));
                }
                else if (size != 0)
                {
                    throw new InvalidDataException("Invalid pack");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return new(response, ss,
            acknowledged.Count > 0 ? acknowledged : Array.Empty<ReadOnlyMemory<byte>>(),
            shallow.Count > 0 ? shallow : Array.Empty<ReadOnlyMemory<byte>>(),
            unshallow.Count > 0 ? unshallow : Array.Empty<ReadOnlyMemory<byte>>(),
            hashAlgorithm
        );
    }

    private readonly HttpResponseMessage _response;

    private readonly Stream _stream;

    public IEnumerable<ReadOnlyMemory<byte>> Acknowledged { get; }

    public IEnumerable<ReadOnlyMemory<byte>> Shallow { get; }

    public IEnumerable<ReadOnlyMemory<byte>> Unshallow { get; }

    public AsyncPack Pack { get; }

    private UploadPackResponse(HttpResponseMessage response, Stream stream, IEnumerable<ReadOnlyMemory<byte>> acknowledged, IEnumerable<ReadOnlyMemory<byte>> shallow, IEnumerable<ReadOnlyMemory<byte>> unshallow, string hashAlgorithm = nameof(SHA1))
    {
        _response = response;
        _stream = stream;
        Acknowledged = acknowledged;
        Shallow = shallow;
        Unshallow = unshallow;
        Pack = new(_stream, hashAlgorithm);
    }

    public void Dispose()
    {
        using (_response)
        using (_stream)
        { }
        GC.SuppressFinalize(this);
    }
}