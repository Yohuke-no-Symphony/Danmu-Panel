using Avalonia.Headless.XUnit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证特别关注行绑定的稳定实例及原始操作目标。</summary>
public sealed class SpecialAttentionRowBindingTests
{
    /// <summary>名单增删保留已有行，行命令操作对应名单草稿而不修改原配置。</summary>
    [AvaloniaFact]
    public void AddingAndRemovingUsersPreservesExistingRowsAndTargetsDraftItems()
    {
        var initial = new SpecialAttentionSettings
        {
            Users = [new() { UserUid = 42, DisplayName = "已关注的用户" }]
        };
        using var model = new SpecialAttentionEditorViewModel(initial, new ControlledUserSearchService(), () => null);
        var originalRow = Assert.Single(model.UserRows);

        model.ManualUid = "73";
        model.ManualDisplayName = "新增用户";
        model.AddManualUserCommand.Execute(null);

        Assert.Same(originalRow, model.UserRows[0]);
        var added = model.UserRows[1];
        Assert.Same(model.Users[1], added.Item);
        Assert.Equal("UID 73", added.UidText);
        Assert.Equal("新增用户", added.DisplayName);
        Assert.True(added.ActionCommand.CanExecute(added.Item));
        Assert.Single(initial.Users);

        added.ActionCommand.Execute(added.Item);

        Assert.Same(originalRow, Assert.Single(model.UserRows));
        Assert.Equal(42, Assert.Single(model.Users).UserUid);
        Assert.Single(initial.Users);
    }
}
