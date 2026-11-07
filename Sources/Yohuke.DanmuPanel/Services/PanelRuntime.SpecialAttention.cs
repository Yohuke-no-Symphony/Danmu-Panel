using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Services;

public sealed partial class PanelRuntime
{
    private readonly SemaphoreSlim specialAttentionChanges = new(1, 1);

    /// <summary>特别关注名单已保存，消息面板应原位更新显示状态。</summary>
    public event Action? SpecialAttentionChanged;

    /// <summary>立即保存单个用户的特别关注状态，不修改消息排序、阅读状态或翻译队列。</summary>
    public async Task SetSpecialAttentionAsync(long userUid, string displayName, bool followed)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userUid);
        ArgumentNullException.ThrowIfNull(displayName);
        await specialAttentionChanges.WaitAsync();
        try
        {
            var settings = Configuration.Settings;
            var previous = settings.SpecialAttention;
            if (previous.Users.Any(user => user.UserUid == userUid) == followed)
            {
                return;
            }
            var users = previous.Users.Where(user => user.UserUid != userUid).ToList();
            if (followed)
            {
                users.Add(new() { UserUid = userUid, DisplayName = displayName.Trim() });
            }
            var next = new SpecialAttentionSettings { Users = users };
            SpecialAttentionSettingsValidator.Validate(next);
            settings.SpecialAttention = next;
            try
            {
                await ConfigurationStore.SaveAsync(Configuration);
            }
            catch
            {
                if (ReferenceEquals(settings.SpecialAttention, next))
                {
                    settings.SpecialAttention = previous;
                }
                throw;
            }
            SpecialAttentionChanged?.Invoke();
        }
        finally
        {
            specialAttentionChanges.Release();
        }
    }
}
