using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.ViewModels.Settings;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证页面草稿与运行配置隔离，并在语言切换前提交表格编辑。</summary>
public sealed class SettingsDraftViewModelTests
{
    /// <summary>输入草稿、术语译法与排序快照都不能提前改变配置。</summary>
    [Fact]
    public void EditingPageDraftsKeepsConfigurationAndCreatedSnapshotsIndependent()
    {
        var settings = new PanelSettings
        {
            Translation = new()
            {
                Model = "original",
                Glossary = [new() { Source = "主播", Target = "配信者", TargetLanguage = "ja-JP" }],
                TargetLanguage = "ja-JP"
            }
        };
        var reading = new SettingsReadingViewModel(settings);
        var appearance = new SettingsAppearanceViewModel(settings, false);
        var translation = new SettingsTranslationViewModel(settings.Translation);
        reading.ShowUserAvatars = false;
        reading.GiftUnreadSort = 1;
        appearance.GiftBorderThickness = 4;
        translation.Model = "draft";
        Assert.Single(translation.Glossary).Target = "ライバー";
        var sorting = reading.CreateSorting();

        Assert.True(settings.ShowUserAvatars);
        Assert.Equal(2, settings.MessageBorders.Gift);
        Assert.Equal("original", settings.Translation.Model);
        Assert.Equal("配信者", Assert.Single(settings.Translation.Glossary).Target);
        Assert.Equal(MessageSortOrder.OldestFirst, settings.MessageSorting.Gift.Unread);
        Assert.Equal(MessageSortOrder.NewestFirst, sorting.Gift.Unread);
        Assert.Equal((int)settings.MessageSorting.Gift.Read, reading.GiftReadSort);
        Assert.NotSame(settings.MessageSorting, sorting);
        Assert.NotSame(settings.MessageSorting.Gift, sorting.Gift);
    }

    /// <summary>切换语言时仍能提交旧语言单元格，且草稿在来回切换中保持。</summary>
    [Fact]
    public void LanguageSwitchCommitsThePreviousLanguageBeforeSelectingTheNext()
    {
        var model = new SettingsTranslationViewModel(new()
        {
            TargetLanguage = "ja-JP",
            Glossary = [new() { Source = "主播", Target = "配信者", TargetLanguage = "ja-JP" }]
        });
        var row = Assert.Single(model.Glossary);
        var committedLanguages = new List<string>();
        model.CommitEditsRequested += () =>
        {
            committedLanguages.Add(model.SelectedLanguage.Code);
            if (model.SelectedLanguage.Code == "ja-JP")
            {
                row.Target = "ライバー";
            }
        };
        model.SelectedLanguage = model.Languages.Single(language => language.Code == "en-US");
        Assert.Equal("", row.Target);
        row.Target = "streamer";
        model.SelectedLanguage = model.Languages.Single(language => language.Code == "ja-JP");

        Assert.Equal(new[] { "ja-JP", "en-US" }, committedLanguages);
        Assert.Equal("ライバー", row.Target);
        Assert.Equal(2, row.GetEntries().Count());
    }

    /// <summary>空白数字保持为无效草稿，不能被默认值静默替换。</summary>
    [Fact]
    public void EmptyBorderDraftStaysInvalidUntilTheUserRepairsIt()
    {
        var settings = new PanelSettings();
        var model = new SettingsAppearanceViewModel(settings, false) { GuardBorderThickness = null };

        Assert.False(model.TryCreateBorders(out _));
        Assert.Null(model.GuardBorderThickness);
        Assert.Equal(2, settings.MessageBorders.Guard);

        model.GuardBorderThickness = 3.5m;
        Assert.True(model.TryCreateBorders(out var borders));
        Assert.Equal(3.5, borders.Guard);
        Assert.Equal(2, settings.MessageBorders.Guard);
    }

    /// <summary>从右键传入的行独立于选中行，删除按钮仍可操作当前选中行。</summary>
    [Fact]
    public async Task RowRemovalUsesItsParameterAndToolbarRemovalUsesSelection()
    {
        var model = new SettingsTranslationViewModel(new()
        {
            Glossary =
            [
                new() { Source = "first", Target = "one", TargetLanguage = "en-US" },
                new() { Source = "second", Target = "two", TargetLanguage = "en-US" }
            ]
        });
        var selected = model.Glossary[0];
        var clicked = model.Glossary[1];
        model.SelectedGlossary = selected;
        await model.RemoveGlossaryTermCommand.ExecuteAsync(clicked);

        Assert.Same(selected, Assert.Single(model.Glossary));
        await model.RemoveGlossaryTermCommand.ExecuteAsync(null);
        Assert.Empty(model.Glossary);
    }
}
