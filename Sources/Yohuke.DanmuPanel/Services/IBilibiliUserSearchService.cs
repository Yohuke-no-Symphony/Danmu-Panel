using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.Services;

/// <summary>按昵称分页搜索全部 B 站用户，或按 UID 查询公开身份。</summary>
public interface IBilibiliUserSearchService
{
    /// <summary>以当前账号查询普通用户；空关键词返回空页，纯数字只按 UID 查询。</summary>
    Task<UserSearchPage> SearchAsync(
        string keyword,
        int page = 1,
        string? cookie = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);
}
