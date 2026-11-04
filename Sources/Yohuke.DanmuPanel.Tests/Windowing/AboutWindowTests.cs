using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.About;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证应用关于页、跨平台入口及模态窗口生命周期。</summary>
public sealed class AboutWindowTests
{
    /// <summary>关于页显示构建元数据、来源及许可，并跟随语言与主题更新。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AboutPageShowsBuildMetadataAndLocalizedContent(bool dark)
    {
        var language = Localization.SelectedLanguageID;
        var window = new AboutWindow { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show();
            var assembly = typeof(App).Assembly;
            Assert.Equal(assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product, window.ApplicationName);
            Assert.Equal(assembly.GetCustomAttribute<AssemblyCompanyAttribute>()!.Company, window.Publisher);
            Assert.Equal(assembly.GetName().Version!.ToString(), window.CurrentVersion);
            var surface = Assert.Single(window.GetVisualDescendants().OfType<AboutSurface>());
            foreach (var languageId in new[] { "zh-CN", "en-US", "ja-JP" })
            {
                Localization.SetLanguage(languageId);
                Pump(window);
                Assert.Equal(Localization.Get("About.Title"), window.Title);
                Assert.Equal(Localization.Get("About.Description"), surface.FindControl<TextBlock>("DescriptionText")!.Text);
                Assert.Equal(window.CurrentVersion, surface.FindControl<SelectableTextBlock>("VersionText")!.Text);
                Assert.NotNull(surface.FindControl<Image>("ApplicationIcon")!.Source);
                Assert.Contains("Danmu Neo", surface.FindControl<TextBlock>("CreditsText")!.Text);
                Assert.Equal(new Uri("https://github.com/Yohuke-no-Symphony/DanmuPanel"),
                    surface.FindControl<HyperlinkButton>("ProjectLink")!.NavigateUri);
                Assert.Equal(new Uri("https://github.com/Yohuke-no-Symphony/DanmuPanel/blob/main/LICENSE"),
                    surface.FindControl<HyperlinkButton>("LicenseLink")!.NavigateUri);
                if (Environment.GetEnvironmentVariable("DANMUPANEL_ABOUT_SCREENSHOTS") is { Length: > 0 } output)
                {
                    Directory.CreateDirectory(output);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
                    Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(output, $"about-{languageId}-{(dark ? "dark" : "light")}.png"));
                }
            }
        }
        finally
        {
            window.Close();
            Localization.SetLanguage(language);
        }
    }

    /// <summary>窗口与原生菜单共享关于命令，打开后阻止重复调用，关闭后允许重新打开。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MenuCommandOwnsTheDialogAndRestoresInteraction(bool escape)
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmu-panel-about-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        var shell = new ShellWindow(panels, new TextBlock(), directory, supportsNativeMenus: true);
        try
        {
            await shell.InitializeLayoutAsync();
            shell.Show();
            shell.FloatPanel(WorkbenchPanelIds.GIFT);
            Pump(shell);
            var floating = Assert.Single(shell.Layouts.FloatingWindows);
            var menu = Assert.Single(shell.GetVisualDescendants().OfType<Menu>());
            var applicationMenu = menu.Items.OfType<MenuItem>().First();
            var entry = applicationMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Localization.Get("About.Menu")));
            var nativeMenu = NativeMenu.GetMenu(shell)!;
            var nativeApplicationMenu = nativeMenu.Items.OfType<NativeMenuItem>().First().Menu!;
            var nativeEntry = nativeApplicationMenu.Items.OfType<NativeMenuItem>().Single(item => item.Header == Localization.Get("About.Menu"));
            Assert.Same(shell.AboutCommand, entry.Command);
            Assert.Same(entry.Command, nativeEntry.Command);
            shell.Topmost = true;
            entry.Command!.Execute(null);
            Pump(shell);
            var dialog = Assert.Single(shell.OwnedWindows.OfType<AboutWindow>());
            Assert.Same(shell, dialog.Owner);
            Assert.True(dialog.Topmost);
            Assert.False(floating.IsEnabled);
            Assert.False(entry.Command.CanExecute(null));
            if (escape)
            {
                dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            }
            else
            {
                dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CloseAboutButton")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Pump(shell);
            Assert.Empty(shell.OwnedWindows.OfType<AboutWindow>());
            Assert.True(floating.IsEnabled);
            Assert.True(entry.Command.CanExecute(null));
            entry.Command.Execute(null);
            Pump(shell);
            Assert.Single(shell.OwnedWindows.OfType<AboutWindow>()).Close();
            Pump(shell);
        }
        finally
        {
            foreach (var dialog in shell.OwnedWindows.OfType<AboutWindow>().ToArray())
            {
                dialog.Close();
            }
            await shell.CloseAsync();
            Directory.Delete(directory, true);
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
