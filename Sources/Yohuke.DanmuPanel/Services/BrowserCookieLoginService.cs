using Yohuke.DanmuPanel.I18n;
using System.Net;
using Yohuke.DanmuPanel.Apis;
using Yohuke.DanmuPanel.Models.BrowserLogin;

namespace Yohuke.DanmuPanel.Services;

/// <summary>
/// 从内嵌浏览器 Cookie 中提取平台登录信息。
/// </summary>
public class BrowserCookieLoginService
{
    /// <summary>
    /// B 站登录地址。
    /// </summary>
    public static readonly Uri BILIBILI_LOGIN_URI = new("https://passport.bilibili.com/login");

    /// <summary>
    /// 读取平台登录起始地址。
    /// </summary>
    public Uri GetLoginUri(BrowserLoginPlatform platform)
    {
        return platform switch
        {
            BrowserLoginPlatform.Bilibili => BILIBILI_LOGIN_URI,
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
        };
    }

    /// <summary>
    /// 读取平台显示名称。
    /// </summary>
    public string GetPlatformName(BrowserLoginPlatform platform)
    {
        return platform switch
        {
            BrowserLoginPlatform.Bilibili => Localization.Get("Services.BrowserLogin.Bilibili"),
            _ => Localization.Get("Services.BrowserLogin.UnknownPlatform")
        };
    }

    /// <summary>
    /// 从浏览器 Cookie 创建登录结果。
    /// </summary>
    public BrowserCookieLoginResult CreateResult(BrowserLoginPlatform platform, IEnumerable<Cookie> cookies)
    {
        return platform switch
        {
            BrowserLoginPlatform.Bilibili => CreateBilibiliResult(cookies),
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, null)
        };
    }

    /// <summary>
    /// 读取浏览器登录 Profile 目录。
    /// </summary>
    public string GetProfileDirectory(BrowserLoginPlatform platform)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Yohuke.DanmuPanel",
            "WebLoginProfiles",
            platform.ToString());
    }

    private static BrowserCookieLoginResult CreateBilibiliResult(IEnumerable<Cookie> cookies)
    {
        var cookie = ApiCookieHelper.CreateBilibiliCookie(cookies);
        var sessData = ApiCookieHelper.GetValue(cookie, "SESSDATA");
        var biliJct = ApiCookieHelper.GetValue(cookie, "bili_jct");

        if (string.IsNullOrWhiteSpace(sessData) || string.IsNullOrWhiteSpace(biliJct))
        {
            throw LocalizedExceptionText.Create("Services.BrowserLogin.BilibiliCookiesMissing",
                message => new InvalidOperationException(message));
        }

        return new()
        {
            Platform = BrowserLoginPlatform.Bilibili,
            Cookie = cookie,
            Message = Localization.Get("Services.BrowserLogin.BilibiliCookiesRead"),
            MessageKey = "Services.BrowserLogin.BilibiliCookiesRead"
        };
    }

}
