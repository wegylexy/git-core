using System.Buffers;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace FlyByWireless.GitCore;

public static class HttpExtensions
{
    public static void Authenticate<T>(this T client, string username, string password) where T : HttpClient =>
        client.DefaultRequestHeaders.Authorization = new("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{HttpUtility.UrlEncode(username)}:{HttpUtility.UrlEncode(password)}"))
        );

    public static HttpRequestMessage WithAuthorization(this HttpRequestMessage message, string username, string password)
    {
        message.Headers.Authorization = new("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{HttpUtility.UrlEncode(username)}:{HttpUtility.UrlEncode(password)}"))
        );
        return message;
    }

    public static async Task<UploadPackAdvertisement> GetUploadPackAsync(this HttpClient client, AuthenticationHeaderValue? authentication = null, HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead, CancellationToken cancellationToken = default) =>
        new(await client.SendAsync(new(HttpMethod.Get, "info/refs?service=git-upload-pack")
        {
            Headers = { Authorization = authentication }
        }, completionOption, cancellationToken));

    public static Task<UploadPackResponse> PostUploadPackAsync(this HttpClient client, UploadPackRequest request, AuthenticationHeaderValue? authentication = null, string hashAlgorithm = nameof(SHA1), CancellationToken cancellationToken = default) =>
        UploadPackResponse.RequestAsync(client, request, authentication, hashAlgorithm, cancellationToken);

    public static async Task<ReadOnlySequence<byte>> ReadAsSequenceAsync(this HttpContent content, int segmentSize = 81920, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Stack<ReadOnlyMemory<byte>> segments = new();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        return await stream.ToSequenceAsync(segmentSize, progress, cancellationToken);
    }

    public static async Task<Stream> ReadAsStreamAsync(this HttpContent content, int segmentSize = 81920, Action<long>? progress = null, CancellationToken cancellationToken = default) =>
        new SequenceStream(await content.ReadAsSequenceAsync(segmentSize, progress, cancellationToken));

    public static async Task<ZipArchive> ReadAsZipAsync(this HttpContent content, bool leaveOpen = false, Encoding? entryNameEncoding = null, Action<long>? progress = null, int segmentSize = 81920, CancellationToken cancellationToken = default) =>
        new(await content.ReadAsStreamAsync(segmentSize, progress, cancellationToken), ZipArchiveMode.Read, leaveOpen, entryNameEncoding);
}