using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace FlyByWireless.GitCore;

public sealed class UploadPackResponse : IDisposable
{
    internal static async Task<UploadPackResponse> RequestAsync(HttpClient client, UploadPackRequest request, string hashAlgorithm = nameof(SHA1), CancellationToken cancellationToken = default)
    {
        int hashSize;
        {
            using var ha = HashAlgorithm.Create(hashAlgorithm)!;
            hashSize = ha.HashSize / 8;
        }
        var response = await client.SendAsync(new(HttpMethod.Post, "git-upload-pack")
        {
            Content = request
        }, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.EnsureSuccessStatusCode().Content.Headers.ContentType is not { MediaType: "application/x-git-upload-pack-result" })
        {
            throw new InvalidOperationException("Unexpected media type");
        }
        StackStream ss = new(await response.Content.ReadAsStreamAsync(cancellationToken), true);
        var buffer = ArrayPool<byte>.Shared.Rent(5 + hashSize * 2);
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
                var size = (buffer[0].ParseHex() << 12) | (buffer[1].ParseHex() << 8) | (buffer[2].ParseHex() << 4) | buffer[3].ParseHex();
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
                    --size;
                    if (size < 3 || buffer[size] != 10)
                    {
                        throw new InvalidDataException("Invalid upload pack response");
                    }
                    if (buffer[2] == 0x4b)
                    {
                        if (size == 3 && buffer[0] == 0x4e && buffer[1] == 0x41) // "NAK"
                        {
                            break;
                        }
                        if (buffer[0] == 0x41 && buffer[1] == 0x43) // "ACK"
                        {
                            if (size != 4 + hashSize * 2 || buffer[3] != 0x20)
                            {
                                throw new InvalidDataException("Invalid ACK");
                            }
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
        return new(response, ss, hashAlgorithm);
    }

    private readonly HttpResponseMessage _response;

    private readonly Stream _stream;

    public AsyncPack Pack { get; }

    private UploadPackResponse(HttpResponseMessage response, Stream stream, string hashAlgorithm = nameof(SHA1))
    {
        _response = response;
        _stream = stream;
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