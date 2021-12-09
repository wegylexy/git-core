using System.Buffers;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace FlyByWireless.GitCore;

public static class HttpExtensions
{
    public static void Authorize<T>(this T client, string username, string password) where T : HttpClient =>
        client.DefaultRequestHeaders.Authorization = new("Basic",
            Convert.ToBase64String(Encoding.ASCII.GetBytes($"{HttpUtility.UrlEncode(username)}:{HttpUtility.UrlEncode(password)}"))
        );

    public static HttpRequestMessage Authorize(this HttpRequestMessage message)
    {
        if (message.RequestUri is { UserInfo: { Length: > 0 } and var ui })
        {
            message.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(ui)));
        }
        return message;
    }
    public static AuthenticationHeaderValue? GetBasicAuthorization(this Uri uri) =>
            string.IsNullOrEmpty(uri.UserInfo) ? null : new("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(uri.UserInfo)));

    public static async Task<UploadPackAdvertisement> GetUploadPackAsync(this HttpClient client, Uri repo, HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead, CancellationToken cancellationToken = default) =>
        new(await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, new Uri(repo, "info/refs?service=git-upload-pack")), completionOption, cancellationToken));

    public static Task<UploadPackResponse> PostUploadPackAsync(this HttpClient client, Uri repo, UploadPackRequest request, string hashAlgorithm = nameof(SHA1), CancellationToken cancellationToken = default) =>
        UploadPackResponse.RequestAsync(client, repo, request, hashAlgorithm, cancellationToken);

    public static async Task<ReadOnlySequence<byte>> ReadAsSequenceAsync(this HttpContent content, int segmentSize = 0x20000, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        Stack<ReadOnlyMemory<byte>> segments = new();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        return await stream.ToSequenceAsync(segmentSize, progress, cancellationToken);
    }

    public static async Task<Stream> ReadAsStreamAsync(this HttpContent content, int segmentSize = 0x20000, Action<long>? progress = null, CancellationToken cancellationToken = default) =>
        new SequenceStream(await content.ReadAsSequenceAsync(segmentSize, progress, cancellationToken));

    public static async Task<ZipArchive> ReadAsZipAsync(this HttpContent content, bool leaveOpen = false, Encoding? entryNameEncoding = null, Action<long>? progress = null, int segmentSize = 0x20000, CancellationToken cancellationToken = default) =>
        new(await content.ReadAsStreamAsync(segmentSize, progress, cancellationToken), ZipArchiveMode.Read, leaveOpen, entryNameEncoding);
}