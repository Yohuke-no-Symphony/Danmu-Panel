using System.Net;
using System.Text.Json;
using Xunit;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Tests.Translation;

public sealed class OpenAiCompatibleTranslationClientTests
{
    [Fact]
    public async Task SendsCompatibleRequestAndReadsPlainText()
    {
        using var http = new HttpClient(new TranslationHttpHandler(async (message, cancellationToken) =>
        {
            Assert.Equal("http://localhost:8080/v1/chat/completions", message.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer secret", message.Headers.Authorization!.ToString());
            var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("local-model", json.RootElement.GetProperty("model").GetString());
            Assert.Equal("system", json.RootElement.GetProperty("messages")[0].GetProperty("role").GetString());
            Assert.Equal("hello", json.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
            Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
            return Success("你好");
        }));
        var result = await new OpenAiCompatibleTranslationClient(http).TranslateAsync(Request());
        Assert.Equal("你好", result);
    }

    [Theory]
    [InlineData(429, 3)]
    [InlineData(503, 3)]
    [InlineData(401, 1)]
    [InlineData(403, 1)]
    [InlineData(400, 1)]
    public async Task RetryPolicyDistinguishesTemporaryAndPermanentFailures(int status, int expectedCalls)
    {
        var calls = 0;
        using var http = new HttpClient(new TranslationHttpHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("secret should not be reported")
            });
        }));
        var client = new OpenAiCompatibleTranslationClient(http, delay: (_, _) => Task.CompletedTask);
        var exception = await Assert.ThrowsAsync<TranslationException>(() => client.TranslateAsync(Request()));
        Assert.Equal(expectedCalls, calls);
        Assert.DoesNotContain("secret", exception.ToString());
    }

    [Fact]
    public async Task CallerCancellationDoesNotRetry()
    {
        var calls = 0;
        using var http = new HttpClient(new TranslationHttpHandler(async (_, token) =>
        {
            calls++;
            await Task.Delay(Timeout.Infinite, token);
            return Success("unused");
        }));
        using var cancellation = new CancellationTokenSource();
        var task = new OpenAiCompatibleTranslationClient(http).TranslateAsync(Request(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task FullEndpointIsAcceptedWithoutAppendingItAgain()
    {
        using var http = new HttpClient(new TranslationHttpHandler((request, _) =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Success("ok"));
        }));
        await new OpenAiCompatibleTranslationClient(http).TranslateAsync(Request() with { BaseUrl = "http://localhost/v1/chat/completions/" });
    }

    [Fact]
    public async Task InvalidJsonIsSafeFailure()
    {
        using var http = new HttpClient(new TranslationHttpHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json")
        })));
        var exception = await Assert.ThrowsAsync<TranslationException>(() => new OpenAiCompatibleTranslationClient(http).TranslateAsync(Request()));
        Assert.Equal("translation.invalid_response", exception.Message);
    }

    private static TranslationRequest Request()
    {
        return new() { BaseUrl = "http://localhost:8080/v1", ApiKey = "secret", Model = "local-model", SystemPrompt = "Translate.", Text = "hello" };
    }

    private static HttpResponseMessage Success(string value)
    {
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = value } } } })) };
    }
}
