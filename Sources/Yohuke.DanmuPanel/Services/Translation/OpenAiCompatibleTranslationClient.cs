using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Yohuke.DanmuPanel.Models.Translation;

namespace Yohuke.DanmuPanel.Services.Translation;

/// <summary>使用 Chat Completions 文本协议的翻译客户端。</summary>
public sealed class OpenAiCompatibleTranslationClient : ITranslationClient
{
    private const int MAX_RETRIES = 2;
    private readonly HttpClient httpClient;
    private readonly TimeSpan timeout;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    /// <summary>创建客户端；HTTP 客户端的生命周期由调用者负责。</summary>
    public OpenAiCompatibleTranslationClient(HttpClient httpClient, TimeSpan? timeout = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        this.httpClient = httpClient;
        this.timeout = timeout ?? TimeSpan.FromSeconds(20);
        this.delay = delay ?? Task.Delay;
    }

    /// <summary>执行翻译，暂时性失败最多重试两次。</summary>
    public async Task<string> TranslateAsync(TranslationRequest request, CancellationToken cancellationToken = default)
    {
        var endpoint = GetEndpoint(request.BaseUrl);
        if (string.IsNullOrWhiteSpace(request.Model))
        {
            throw new TranslationException("translation.model_required");
        }

        for (var attempt = 0; ; attempt++)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!string.IsNullOrWhiteSpace(request.ApiKey))
            {
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey.Trim());
            }

            message.Content = JsonContent.Create(new
            {
                model = request.Model,
                messages = new[]
                {
                    new { role = "system", content = request.SystemPrompt },
                    new { role = "user", content = request.Text }
                },
                stream = false
            });
            try
            {
                using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                    timeoutSource.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < MAX_RETRIES && IsTransient(response.StatusCode))
                    {
                        await delay(GetRetryDelay(response, attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new TranslationException($"translation.http_error:{(int)response.StatusCode}");
                }

                await using var body = await response.Content.ReadAsStreamAsync(timeoutSource.Token).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: timeoutSource.Token).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0 ||
                    choices[0].ValueKind != JsonValueKind.Object || !choices[0].TryGetProperty("message", out var result) ||
                    result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(content.GetString()))
                {
                    throw new TranslationException("translation.empty_response");
                }

                return content.GetString()!.Trim();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt >= MAX_RETRIES)
                {
                    throw new TranslationException("translation.timeout");
                }

                await delay(TimeSpan.FromSeconds(attempt * 2 + 1), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                if (attempt >= MAX_RETRIES)
                {
                    throw new TranslationException("translation.network_error");
                }

                await delay(TimeSpan.FromSeconds(attempt * 2 + 1), cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                throw new TranslationException("translation.invalid_response");
            }
        }
    }

    private static Uri GetEndpoint(string baseUrl)
    {
        var value = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new TranslationException("translation.invalid_base_url");
        }

        return value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? uri
            : new Uri(value + "/chat/completions");
    }

    private static bool IsTransient(HttpStatusCode status)
    {
        return status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        var wait = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow);
        return wait is { } value && value >= TimeSpan.Zero ? value : TimeSpan.FromSeconds(attempt * 2 + 1);
    }
}
