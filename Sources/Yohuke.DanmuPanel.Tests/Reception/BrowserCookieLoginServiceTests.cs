using Xunit;
using Avalonia.Headless.XUnit;
using System.Net;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.BrowserLogin;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Tests.Reception;

public class BrowserCookieLoginServiceTests
{
    [AvaloniaFact]
    public void CreateResult_NormalizesBilibiliCookies()
    {
        var service = new BrowserCookieLoginService();
        var result = service.CreateResult(BrowserLoginPlatform.Bilibili,
        [
            new("buvid3", "b3"),
            new("SESSDATA", "sess"),
            new("bili_jct", "csrf"),
            new("DedeUserID", "42"),
            new("unused", "x")
        ]);

        Assert.Equal("buvid3=b3;SESSDATA=sess;bili_jct=csrf;DedeUserId=42", result.Cookie);
    }

    /// <summary>
    /// 浏览器抓取同时保留当前登录已有的 B 站设备标识。
    /// </summary>
    [AvaloniaFact]
    public void CreateResult_PreservesExistingBilibiliDeviceCookies()
    {
        var service = new BrowserCookieLoginService();
        var result = service.CreateResult(BrowserLoginPlatform.Bilibili,
        [
            new("buvid3", "device3", "/", ".bilibili.com"),
            new("SESSDATA", "session", "/", ".bilibili.com"),
            new("bili_jct", "csrf", "/", ".bilibili.com"),
            new("DedeUserID", "42", "/", ".bilibili.com"),
            new("buvid4", "device4", "/", ".bilibili.com"),
            new("buvid_fp", "fingerprint", "/", "live.bilibili.com"),
            new("unused", "x", "/", ".bilibili.com")
        ]);

        Assert.Equal(
            "buvid3=device3;SESSDATA=session;bili_jct=csrf;DedeUserId=42;buvid4=device4;buvid_fp=fingerprint",
            result.Cookie);
    }

    /// <summary>
    /// 同名第三方 Cookie 不会覆盖 B 站的登录和设备信息。
    /// </summary>
    [AvaloniaFact]
    public void CreateResult_ExcludesCookiesFromOtherDomains()
    {
        var service = new BrowserCookieLoginService();
        var result = service.CreateResult(BrowserLoginPlatform.Bilibili,
        [
            new("SESSDATA", "session", "/", ".bilibili.com"),
            new("bili_jct", "csrf", "/", "passport.bilibili.com"),
            new("DedeUserID", "42", "/", ".bilibili.com"),
            new("buvid4", "device4", "/", ".bilibili.com"),
            new("buvid_fp", "fingerprint", "/", ".bilibili.com"),
            new("SESSDATA", "other-session", "/", ".qq.com"),
            new("bili_jct", "other-csrf", "/", "bilibili.com.example.com"),
            new("buvid4", "other-device", "/", "notbilibili.com"),
            new("buvid_fp", "other-fingerprint", "/", ".qq.com")
        ]);

        Assert.Equal(
            "buvid3=;SESSDATA=session;bili_jct=csrf;DedeUserId=42;buvid4=device4;buvid_fp=fingerprint",
            result.Cookie);
    }

    /// <summary>
    /// 第三方域名提供的同名字段不能构成 B 站登录。
    /// </summary>
    [AvaloniaFact]
    public void CreateResult_RejectsLoginCookiesFromOtherDomains()
    {
        var service = new BrowserCookieLoginService();

        var error = Assert.Throws<InvalidOperationException>(() => service.CreateResult(BrowserLoginPlatform.Bilibili,
        [
            new("SESSDATA", "other-session", "/", ".example.com"),
            new("bili_jct", "other-csrf", "/", ".example.com")
        ]));
        Assert.Equal(Localization.Get("Services.BrowserLogin.BilibiliCookiesMissing"), LocalizedExceptionText.Get(error));
    }

    [AvaloniaFact]
    public void CreateResult_ThrowsWhenBilibiliLoginCookiesMissing()
    {
        var service = new BrowserCookieLoginService();

        var error = Assert.Throws<InvalidOperationException>(() => service.CreateResult(BrowserLoginPlatform.Bilibili, []));
        Assert.Equal(Localization.Get("Services.BrowserLogin.BilibiliCookiesMissing"), LocalizedExceptionText.Get(error));
    }

}
