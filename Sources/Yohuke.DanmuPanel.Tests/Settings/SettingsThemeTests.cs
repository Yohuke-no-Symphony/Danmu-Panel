using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证设置下拉框的选中内容和弹出选项随主题与语言更新。</summary>
public sealed class SettingsThemeTests
{
    /// <summary>主题在深浅之间切换时，全部设置下拉框仍使用当前主题的正文颜色。</summary>
    [AvaloniaTheory]
    [InlineData("Appearance", 2)]
    [InlineData("Reading", 1)]
    [InlineData("Translation", 4)]
    public async Task ChoiceTextFollowsThemeAndLanguage(string section, int expectedChoices)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), new());
        var originalTheme = Application.Current!.RequestedThemeVariant;
        var originalLanguage = Localization.SelectedLanguageID;
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Localization.SetLanguage("zh-CN");
        var view = new SettingsView(runtime, new TextBlock());
        var window = new Window { Content = view, Width = 1020, Height = 900 };
        try
        {
            window.Show();
            var sectionButton = view.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "SettingsSection" + section);
            sectionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            var choices = view.GetVisualDescendants().OfType<ComboBox>().ToArray();
            Assert.Equal(expectedChoices, choices.Length);
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current.RequestedThemeVariant = theme;
                Localization.SetLanguage(theme == ThemeVariant.Light ? "en-US" : "ja-JP");
                Pump(window);
                var expected = Assert.IsAssignableFrom<ISolidColorBrush>(window.Foreground).Color;
                foreach (var choice in choices)
                {
                    AssertChoiceText(choice, expected);
                    choice.IsDropDownOpen = true;
                    Pump(window);
                    var optionLabels = choice.GetRealizedContainers().OfType<ComboBoxItem>()
                        .SelectMany(item => item.GetVisualDescendants().OfType<TextBlock>()).ToArray();
                    Assert.NotEmpty(optionLabels);
                    Assert.All(optionLabels, label => Assert.Equal(expected, ((ISolidColorBrush)label.Foreground!).Color));
                    choice.IsDropDownOpen = false;
                }
                Pump(window);
                if (Environment.GetEnvironmentVariable("DANMUPANEL_THEME_SCREENSHOTS") is { Length: > 0 } output)
                {
                    Localization.SetLanguage("zh-CN");
                    Pump(window);
                    Directory.CreateDirectory(output);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(output, section.ToLowerInvariant() + "-" + theme.Key + ".png"));
                }
            }
        }
        finally
        {
            window.Close();
            Application.Current.RequestedThemeVariant = originalTheme;
            Localization.SetLanguage(originalLanguage);
        }
    }

    private static void AssertChoiceText(ComboBox choice, Color expected)
    {
        var labels = choice.GetVisualDescendants().OfType<TextBlock>()
            .Where(label => !string.IsNullOrWhiteSpace(label.Text)).ToArray();
        if (labels.Length == 0 && choice.SelectedItem is ComboBoxItem { Content: TextBlock renderedLabel })
        {
            labels = [renderedLabel];
        }
        Assert.NotEmpty(labels);
        Assert.All(labels, label => Assert.Equal(expected, ((ISolidColorBrush)label.Foreground!).Color));
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
    }
}
