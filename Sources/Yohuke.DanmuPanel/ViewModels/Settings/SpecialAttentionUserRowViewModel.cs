using System.Globalization;
using System.Windows.Input;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.ViewModels.Settings;

/// <summary>为名单和搜索结果提供统一身份展示及针对原始条目的操作。</summary>
public sealed class SpecialAttentionUserRowViewModel(long userUid, string displayName, object item,
    ICommand actionCommand, bool isAdding, IUserAvatarService? avatarService)
{
    public long UserUid { get; } = userUid;
    public string DisplayName { get; } = displayName.Length == 0 ? userUid.ToString(CultureInfo.InvariantCulture) : displayName;
    public string UidText { get; } = "UID " + userUid.ToString(CultureInfo.InvariantCulture);
    public object Item { get; } = item;
    public ICommand ActionCommand { get; } = actionCommand;
    public bool IsAdding { get; } = isAdding;
    public bool IsRemoving => !IsAdding;
    public IUserAvatarService? AvatarService { get; } = avatarService;
}
