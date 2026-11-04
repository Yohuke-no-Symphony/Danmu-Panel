namespace Yohuke.DanmuPanel.Tests.Workspace;

internal sealed class AvatarHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return send(request, cancellationToken);
    }
}
