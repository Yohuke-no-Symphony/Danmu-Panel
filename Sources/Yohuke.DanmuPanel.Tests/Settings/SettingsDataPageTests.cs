using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Xunit;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证数据管理页的自动统计、刷新绑定与平台目录入口。</summary>
public sealed class SettingsDataPageTests
{
    /// <summary>进入页面统计缓存，刷新与重新进入均读取新文件，目录按钮使用真实数据根路径。</summary>
    [AvaloniaFact]
    public async Task EnteringAndRefreshingThePageUpdatesUsageAndOpensTheDataRoot()
    {
        using var directory = new MessageStoreTestDirectory();
        var root = Path.Combine(directory.Path, "资料 with spaces");
        await using var runtime = new PanelRuntime(new(root), new());
        await runtime.ClearTranslationCacheAsync();
        var opened = new List<string>();
        var view = new SettingsView(runtime, new TextBlock(), false, openDataDirectory: path =>
        {
            opened.Add(path);
            return Task.FromResult(true);
        });
        var window = new Window { Width = 1020, Height = 900, Content = view };
        try
        {
            window.Show();
            view.Model.SelectSectionCommand.Execute("Data");
            Pump(window);
            await view.Model.Data.RefreshCacheSizeCommand.ExecutionTask!;
            Pump(window);
            var originalSize = Assert.IsType<long>(view.Model.Data.CacheSizeBytes);
            var paths = new AppDirectoryService(root);
            Directory.CreateDirectory(paths.AvatarCacheDirectory);
            File.WriteAllBytes(Path.Combine(paths.AvatarCacheDirectory, "avatar.img"), new byte[2048]);
            var refresh = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "RefreshCacheSizeButton");

            refresh.ExecuteBoundCommand();
            await view.Model.Data.RefreshCacheSizeCommand.ExecutionTask!;
            Pump(window);

            Assert.Equal(originalSize + 2048, view.Model.Data.CacheSizeBytes);
            var sizeText = view.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "CacheSizeText");
            Assert.Equal(view.Model.Data.CacheSizeText, sizeText.Text);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == root);
            var open = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "OpenDataDirectoryButton");
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(open.Command).ExecuteAsync(open.CommandParameter);
            Assert.Equal(root, Assert.Single(opened));

            view.Model.SelectSectionCommand.Execute("Reading");
            Pump(window);
            File.WriteAllBytes(Path.Combine(paths.AvatarCacheDirectory, "avatar.img"), new byte[4096]);
            view.Model.SelectSectionCommand.Execute("Data");
            Pump(window);
            await view.Model.Data.RefreshCacheSizeCommand.ExecutionTask!;

            Assert.Equal(originalSize + 4096, view.Model.Data.CacheSizeBytes);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>系统无法打开目录或启动器抛错时，页面反馈失败而不让命令异常终止。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDirectoryLaunchShowsAnError(bool throwException)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var view = new SettingsView(runtime, new TextBlock(), false, openDataDirectory: _ => throwException
            ? Task.FromException<bool>(new IOException())
            : Task.FromResult(false));

        await view.Model.Data.OpenDataDirectoryCommand.ExecuteAsync(null);

        Assert.StartsWith(Localization.Get("Settings.OpenDataDirectoryFailed"), view.Model.Status);
        Assert.False(view.Model.IsStatusSuccess);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
