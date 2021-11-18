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

    public static async Task<UploadPackAdvertisement> GetUploadPackAsync(this HttpClient client, HttpCompletionOption completionOption = HttpCompletionOption.ResponseHeadersRead, CancellationToken cancellationToken = default) =>
        new(await client.GetAsync("info/refs?service=git-upload-pack", completionOption, cancellationToken));

    public static Task<UploadPackResponse> PostUploadPackAsync(this HttpClient client, UploadPackRequest request, string hashAlgorithm = nameof(SHA1), CancellationToken cancellationToken = default) =>
        UploadPackResponse.RequestAsync(client, request, hashAlgorithm, cancellationToken);
}