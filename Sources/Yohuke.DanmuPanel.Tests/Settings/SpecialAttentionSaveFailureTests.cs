using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证特别关注草稿写盘失败后不会泄漏到运行配置或在重试时复活。</summary>
public sealed class SpecialAttentionSaveFailureTests
{
    /// <summary>写入失败保留草稿并恢复原名单，删去未保存用户后可成功保存空名单。</summary>
    [AvaloniaFact]
    public async Task FailedSaveRestoresRuntimeUsersAndRetryDoesNotResurrectRemovedDraft()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock());
        var model = view.SpecialAttentionEditor.Model;
        var original = runtime.Configuration.Settings.SpecialAttention;
        model.ManualUid = "42";
        model.ManualDisplayName = "未保存用户";
        model.AddManualUserCommand.Execute(null);
        var temporary = Path.Combine(directory.Path, "settings.json.tmp");
        Directory.CreateDirectory(temporary);
        try
        {
            Assert.False(await view.SaveAsync());

            Assert.Same(original, runtime.Configuration.Settings.SpecialAttention);
            Assert.Empty(runtime.Configuration.Settings.SpecialAttention.Users);
            Assert.Equal(42, Assert.Single(model.Users).UserUid);
            Assert.False(File.Exists(Path.Combine(directory.Path, "settings.json")));
            model.RemoveUserCommand.Execute(model.Users[0]);
            Assert.Empty(model.Users);
        }
        finally
        {
            Directory.Delete(temporary);
        }

        Assert.True(await view.SaveAsync());

        Assert.Empty(runtime.Configuration.Settings.SpecialAttention.Users);
        Assert.Empty(runtime.ConfigurationStore.Load().Settings.SpecialAttention.Users);
        Assert.Empty(model.Users);
    }
}
