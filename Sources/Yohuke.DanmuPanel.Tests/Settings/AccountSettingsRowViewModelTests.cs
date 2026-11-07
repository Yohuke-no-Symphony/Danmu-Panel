using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.ViewModels.Settings;
using Xunit;
using Avalonia.Headless.XUnit;

namespace Yohuke.DanmuPanel.Tests.Settings;

public sealed class AccountSettingsRowViewModelTests
{
    [AvaloniaFact]
    public void ValidationRefreshPreservesUncommittedNameAndAutomaticNamePolicy()
    {
        var account = new BilibiliAccount { Name = "original", AutoNameOnLogin = true };
        var model = Create(account);
        model.Name = "edited while validating";

        model.RefreshState(new() { Status = AccountValidationStatus.Valid, Uid = 42, UserName = "remote name" }, true);

        Assert.Equal("edited while validating", model.Name);
        Assert.Equal("original", account.Name);
        Assert.True(account.AutoNameOnLogin);
        Assert.True(model.HasNameChanges);
        Assert.True(model.IsDefault);
        Assert.True(model.IsValid);
        model.CommitEdits();
        Assert.Equal("edited while validating", account.Name);
        Assert.False(account.AutoNameOnLogin);
    }

    [AvaloniaFact]
    public void EmptyNameDraftDoesNotClearPersistedNameOrDisableAutomaticNaming()
    {
        var account = new BilibiliAccount { Name = "original", AutoNameOnLogin = true };
        var model = Create(account);
        model.Name = "  ";

        model.CommitEdits();

        Assert.Equal("original", account.Name);
        Assert.True(account.AutoNameOnLogin);
        Assert.Equal("  ", model.Name);
    }

    private static AccountSettingsRowViewModel Create(BilibiliAccount account)
    {
        var noOp = () => Task.CompletedTask;
        return new(account, noOp, noOp, noOp, noOp, noOp, noOp, exception => throw exception);
    }
}
