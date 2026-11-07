namespace Yohuke.DanmuPanel.Models.Messages;

/// <summary>全站用户搜索的一页结果及可恢复的问题。</summary>
public sealed class UserSearchPage
{
    public IReadOnlyList<UserSearchResult> Results { get; init; } = [];
    public IReadOnlyList<UserSearchIssue> Issues { get; init; } = [];
    public int Page { get; init; } = 1;
    public int? TotalPages { get; init; }
    public bool HasMore { get; init; }
}
