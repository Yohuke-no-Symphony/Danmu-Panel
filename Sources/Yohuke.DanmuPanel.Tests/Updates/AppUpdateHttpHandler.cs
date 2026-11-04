namespace Yohuke.DanmuPanel.Tests.Updates;

internal sealed class AppUpdateHttpHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    internal Uri? RequestUri { get; private set; }
    internal string Accept { get; private set; } = string.Empty;
    internal string UserAgent { get; private set; } = string.Empty;
    internal string ApiVersion { get; private set; } = string.Empty;
    internal bool HasCredentials { get; private set; }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        Accept = request.Headers.Accept.ToString();
        UserAgent = request.Headers.UserAgent.ToString();
        ApiVersion = request.Headers.TryGetValues("X-GitHub-Api-Version", out var values)
            ? string.Join(",", values) : string.Empty;
        HasCredentials = request.Headers.Contains("Cookie") || request.Headers.Contains("Authorization");
        return send(request, cancellationToken);
    }
}
