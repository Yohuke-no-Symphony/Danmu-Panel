using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>用户查询失败的本地化提示或服务器业务错误。</summary>
public sealed class UserSearchIssue
{
    private string message = "";

    public int? ApiCode { get; init; }
    public string? MessageKey { get; init; }
    public string Message
    {
        get => MessageKey is null ? message : Localization.Get(MessageKey);
        init => message = value;
    }
}
