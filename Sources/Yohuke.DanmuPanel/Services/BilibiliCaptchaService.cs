using System.Text.Json;
using Avalonia.Platform;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Services;

internal static class BilibiliCaptchaService
{
    private const int MAX_PROOF_LENGTH = 16384;

    internal static Task<byte[]> LoadImageAsync(BilibiliLoginChallenge challenge, CancellationToken cancellationToken)
    {
        if (challenge.Type != "img" || !Uri.TryCreate(challenge.ImageUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Host != "api.bilibili.com" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/x/recaptcha/img")
        {
            throw new InvalidDataException("Invalid Bilibili image challenge.");
        }

        return DownloadImageAsync(uri, cancellationToken);
    }

    private static async Task<byte[]> DownloadImageAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BaseApi.DEFAULT_USER_AGENT);
        client.DefaultRequestHeaders.Referrer = new("https://passport.bilibili.com/login");
        return await client.GetByteArrayAsync(uri, cancellationToken);
    }

    internal static string CreateGeetestPage(BilibiliLoginChallenge challenge, string nonce)
    {
        using var page = AssetLoader.Open(new("avares://Yohuke.DanmuPanel/Assets/BilibiliLogin/Geetest.html"));
        using var reader = new StreamReader(page);
        var (lang3, lang4) = Localization.CurrentLanguageID switch
        {
            "ja-JP" => ("ja", "jpn"),
            "zh-CN" => ("zh-cn", "zho"),
            _ => ("en", "eng")
        };
        var config = JsonSerializer.Serialize(new
        {
            nonce,
            v4 = challenge.Type == "geetest4",
            gt = challenge.Gt,
            challenge = challenge.Challenge,
            lang3,
            lang4,
            verifyText = Localization.Get("UI.BilibiliCaptchaWindow.Verify")
        });
        return reader.ReadToEnd().Replace("__NONCE__", nonce, StringComparison.Ordinal)
            .Replace("__CONFIG__", config, StringComparison.Ordinal);
    }

    internal static bool TryReadGeetestMessage(string? body, string nonce, BilibiliLoginChallenge challenge,
        out string? status, out BilibiliChallengeProof? proof)
    {
        status = null;
        proof = null;
        if (string.IsNullOrEmpty(body) || body.Length > 5 * MAX_PROOF_LENGTH)
        {
            return false;
        }

        try
        {
            using var message = JsonDocument.Parse(body);
            var root = message.RootElement;
            if (ReadString(root, "nonce") != nonce)
            {
                return false;
            }

            status = ReadString(root, "status");
            if (status is "ready" or "error")
            {
                return true;
            }
            if (status != "verified" || !root.TryGetProperty("proof", out var value))
            {
                return false;
            }

            proof = challenge.Type switch
            {
                "geetest" when ReadString(value, "geetest_challenge") is { } challengeValue &&
                               ReadString(value, "geetest_validate") is { } validate &&
                               ReadString(value, "geetest_seccode") is { } seccode =>
                    new() { Token = challenge.Token, Challenge = challengeValue, Validate = validate, Seccode = seccode },
                "geetest4" when ReadString(value, "lot_number") is { } lotNumber &&
                                ReadString(value, "captcha_output") is { } output &&
                                ReadString(value, "pass_token") is { } passToken &&
                                ReadString(value, "gen_time") is { } genTime =>
                    new() { Token = challenge.Token, LotNumber = lotNumber, CaptchaOutput = output,
                        PassToken = passToken, GenTime = genTime },
                _ => null
            };
            return proof is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            property.GetString() is { Length: > 0 and <= MAX_PROOF_LENGTH } value &&
            !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return null;
    }
}
