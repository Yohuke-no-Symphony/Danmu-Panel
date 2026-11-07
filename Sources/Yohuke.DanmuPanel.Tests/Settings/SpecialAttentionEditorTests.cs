using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Workspace;
using Yohuke.DanmuPanel.Views.Components;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证设置页搜索添加、用户资料、独立名单草稿和共享颜色输入。</summary>
public sealed class SpecialAttentionEditorTests
{
    /// <summary>独立特别关注页展示用户资料，阅读页不再包含此编辑器，切页保留待保存草稿。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchResultsAndSavedUsersShowIdentityAndAvatar(bool dark)
    {
        using var directory = new MessageStoreTestDirectory();
        var originalTheme = Application.Current!.RequestedThemeVariant;
        var originalLanguage = Localization.SelectedLanguageID;
        Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Localization.SetLanguage("zh-CN");
        var configuration = new PanelConfiguration
        {
            Settings = new() { Theme = dark ? "dark" : "light",
                SpecialAttention = new() { Users = [new() { UserUid = 42, DisplayName = "已关注的用户" }] } }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var path = WriteAvatar(directory.Path);
        var avatars = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(path) };
        var search = new ControlledUserSearchService();
        var view = new SettingsView(runtime, new TextBlock(), false, userSearch: search, userAvatars: avatars);
        var window = new Window { Width = 1020, Height = 1000, Content = view };
        try
        {
            window.Show();
            Select(view, "Reading");
            Pump(window);
            Assert.Empty(view.GetVisualDescendants().OfType<SpecialAttentionEditor>());
            Select(view, "SpecialAttention");
            var editor = view.SpecialAttentionEditor;
            Pump(window);
            editor.BringIntoView();
            Pump(window);
            Assert.DoesNotContain(editor.GetVisualDescendants().OfType<TextBox>(), value => value.Name == "SpecialAttentionSearchInput");
            editor.GetVisualDescendants().OfType<Button>().Single(value => value.Name == "OpenSpecialAttentionSearchButton")
                .Command!.Execute(null);
            var dialog = Assert.IsType<SpecialAttentionSearchWindow>(Assert.Single(window.OwnedWindows));
            Pump(dialog);
            var input = dialog.GetVisualDescendants().OfType<TextBox>().Single(value => value.Name == "SpecialAttentionSearchInput");
            input.Text = "普通用户";
            var searchButton = dialog.GetVisualDescendants().OfType<Button>().Single(value => value.Name == "SpecialAttentionSearchButton");
            var pending = Assert.IsAssignableFrom<IAsyncRelayCommand>(searchButton.Command).ExecuteAsync(null);
            Assert.Equal("普通用户", Assert.Single(search.Requests).Query);
            search.Responses[0].SetResult(new() { Results = [new() { UserUid = 73, DisplayName = "搜索到的用户" }] });
            await pending;
            Pump(dialog);
            var add = dialog.GetVisualDescendants().OfType<Button>().Single(value => value.Name == "AddSpecialAttentionSearchResultButton");

            add.Command!.Execute(add.CommandParameter);
            Pump(dialog);

            Assert.Single(configuration.Settings.SpecialAttention.Users);
            Assert.Equal(2, editor.Model.Users.Count);
            Assert.False(add.IsEffectivelyEnabled);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "搜索到的用户");
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "UID 73");
            await WaitUntilAsync(() => dialog.GetVisualDescendants().OfType<UserAvatarView>().Any(avatar => avatar.UserUid == 73 && avatar.AvatarImage is not null), dialog);
            Pump(dialog);
            Capture(dialog, dark ? "special-attention-search-dark.png" : "special-attention-search-light.png");
            var lookup = dialog.GetVisualDescendants().OfType<SearchAndManualDialogSurface>().Single();
            lookup.SelectedMode = SearchAndManualDialogMode.Manual;
            Pump(dialog);
            Assert.Contains(dialog.GetVisualDescendants().OfType<TextBox>(), value => value.Name == "SpecialAttentionManualUid");
            Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBox>(), value => value.Name == "SpecialAttentionSearchInput");
            Capture(dialog, dark ? "special-attention-manual-dark.png" : "special-attention-manual-light.png");
            dialog.GetVisualDescendants().OfType<Button>().Single(value => value.Name == "CloseSpecialAttentionSearchButton")
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await editor.PendingSearchDialog;
            Pump(window);
            Assert.Contains(editor.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "已关注的用户");
            Assert.Contains(editor.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "搜索到的用户");
            Assert.Contains(73L, avatars.RequestedUsers);
            Assert.Equal(2, editor.GetVisualDescendants().OfType<ColorInput>().Count());
            var color = FindColor(editor, "SpecialAttentionContentBrush");
            var userNameColor = FindColor(editor, "SpecialAttentionUserNameBrush");
            color.GetVisualDescendants().OfType<TextBox>().Single(value => value.Name == "ColorHexInput").Text = "#12AB34";
            Pump(window);
            userNameColor.GetVisualDescendants().OfType<TextBox>().Single(value => value.Name == "ColorHexInput").Text = "#80654321";
            Pump(window);
            Assert.Equal("#12AB34", configuration.Settings.ColorScheme.Overrides["SpecialAttentionContentBrush"]);
            Assert.Equal("#80654321", configuration.Settings.ColorScheme.Overrides["SpecialAttentionUserNameBrush"]);
            Select(view, "Appearance");
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<ColorInput>(), value => ReferenceEquals(value, color));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<ColorInput>(), value => ReferenceEquals(value, userNameColor));
            Select(view, "Reading");
            Pump(window);
            Assert.Empty(view.GetVisualDescendants().OfType<SpecialAttentionEditor>());
            Select(view, "SpecialAttention");
            Assert.Equal(2, editor.Model.Users.Count);
            Assert.Same(color, FindColor(editor, "SpecialAttentionContentBrush"));
            Assert.Same(userNameColor, FindColor(editor, "SpecialAttentionUserNameBrush"));
            editor.BringIntoView();
            Pump(window);
            await WaitUntilAsync(() => editor.GetVisualDescendants().OfType<UserAvatarView>().All(avatar => avatar.AvatarImage is not null), window);
            Pump(window);
            Capture(window, dark ? "special-attention-dark.png" : "special-attention-light.png");

            Assert.True(await view.SaveAsync());

            var restored = runtime.ConfigurationStore.Load().Settings;
            Assert.Equal(new long[] { 42, 73 }, restored.SpecialAttention.Users.Select(user => user.UserUid));
            Assert.Equal("搜索到的用户", restored.SpecialAttention.Users[1].DisplayName);
            Assert.Equal("#12AB34", restored.ColorScheme.Overrides["SpecialAttentionContentBrush"]);
            Assert.Equal("#80654321", restored.ColorScheme.Overrides["SpecialAttentionUserNameBrush"]);
            userNameColor.GetVisualDescendants().OfType<Button>().Single(value => value.Name == "ResetColorButton")
                .Command!.Execute(null);
            Pump(window);
            Assert.False(configuration.Settings.ColorScheme.Overrides.ContainsKey("SpecialAttentionUserNameBrush"));
            Assert.Equal("#12AB34", configuration.Settings.ColorScheme.Overrides["SpecialAttentionContentBrush"]);
            Assert.True(await view.SaveAsync());
            var afterReset = runtime.ConfigurationStore.Load().Settings.ColorScheme;
            Assert.False(afterReset.Overrides.ContainsKey("SpecialAttentionUserNameBrush"));
            Assert.Equal("#12AB34", afterReset.Overrides["SpecialAttentionContentBrush"]);
        }
        finally
        {
            foreach (var dialog in window.OwnedWindows.ToArray())
            {
                dialog.Close();
            }
            window.Close();
            Localization.SetLanguage(originalLanguage);
            Application.Current.RequestedThemeVariant = originalTheme;
        }
        Assert.Equal(0, avatars.Subscribers);
    }

    /// <summary>非法颜色保留原始输入并拒绝保存整份名单草稿。</summary>
    [AvaloniaTheory]
    [InlineData("SpecialAttentionUserNameBrush")]
    [InlineData("SpecialAttentionContentBrush")]
    public async Task InvalidAttentionColorPreventsApplyingUserDraft(string colorKey)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock(), false, userSearch: new ControlledUserSearchService(),
            userAvatars: new ControlledUserAvatarService());
        var window = new Window { Width = 1020, Height = 900, Content = view };
        try
        {
            window.Show();
            Select(view, "SpecialAttention");
            Pump(window);
            var editor = view.SpecialAttentionEditor;
            editor.Model.ManualUid = "42";
            editor.Model.AddManualUserCommand.Execute(null);
            FindColor(editor, colorKey).GetVisualDescendants().OfType<TextBox>()
                .Single(value => value.Name == "ColorHexInput").Text = "invalid";

            Assert.False(await view.SaveAsync());

            Assert.Empty(runtime.Configuration.Settings.SpecialAttention.Users);
            Assert.Single(editor.Model.Users);
            Assert.True(window.IsVisible);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == Localization.Get("Colors.InvalidColor"));
        }
        finally
        {
            window.Close();
        }
    }

    private static ColorInput FindColor(Control root, string key)
    {
        return root.GetVisualDescendants().OfType<ColorInput>().Single(input => input.Name == "ColorInput_" + key);
    }

    private static string WriteAvatar(string directory)
    {
        var path = Path.Combine(directory, "identity.png");
        var badge = new Border
        {
            Width = 64, Height = 64, Background = Brushes.SteelBlue,
            Child = new TextBlock { Text = "R", Foreground = Brushes.White, FontSize = 36,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center }
        };
        badge.Measure(new(64, 64));
        badge.Arrange(new Rect(0, 0, 64, 64));
        using var bitmap = new RenderTargetBitmap(new(64, 64));
        bitmap.Render(badge);
        bitmap.Save(path);
        return path;
    }

    private static void Select(SettingsView view, string section)
    {
        view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SettingsSection" + section)
            .Command!.Execute(section);
    }

    private static void Capture(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_SPECIAL_ATTENTION_SCREENSHOTS") is not { Length: > 0 } output)
        {
            return;
        }
        Directory.CreateDirectory(output);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(output, name));
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Window window)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Pump(window);
            if (condition())
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.True(condition());
    }
}
