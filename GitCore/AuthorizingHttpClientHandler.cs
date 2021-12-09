namespace FlyByWireless.GitCore;

public class AuthorizingHttpClientHandler : HttpClientHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        base.SendAsync(request.Authorize(), cancellationToken);
}