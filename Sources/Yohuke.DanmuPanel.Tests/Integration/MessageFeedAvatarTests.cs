using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Workspace;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证所有消息类型的头像位置、缩放与设置刷新。</summary>
public sealed class MessageFeedAvatarTests
{
    /// <summary>四类消息的圆形头像均位于名字左侧，开关与缩放原位生效。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public async Task AllMessageKindsShowScaledAvatarToTheLeftOfName(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = false;
        await runtime.Messages.AppendAsync(CreateMessage(kind, 0), settings.Reading);
        var view = new MessageFeedView(runtime, kind, exception => throw exception);
        var window = new Window { Content = view, Width = 760, Height = 300 };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var row = Assert.Single(view.GetVisualDescendants().OfType<MessageFeedRow>());
            var avatar = Assert.Single(row.GetVisualDescendants().OfType<UserAvatarView>(), value => value.IsEffectivelyVisible);
            var name = Assert.Single(row.GetVisualDescendants().OfType<EmoticonTextBlock>(),
                text => text.Classes.Contains("danmuFeedUser") && text.IsEffectivelyVisible);
            Assert.True(avatar.IsEffectivelyVisible);
            Assert.Equal(TextAlignment.Left, name.TextAlignment);
            Assert.Equal(20, avatar.Bounds.Width);
            Assert.Equal(avatar.Bounds.Width, avatar.Bounds.Height);
            Assert.True(avatar.TranslatePoint(new(avatar.Bounds.Width, 0), row)!.Value.X <=
                name.TranslatePoint(default, row)!.Value.X);

            settings.CompactDanmuDisplay = true;
            settings.MessageTextScale = 1.5;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Equal(24, avatar.Bounds.Width);
            Assert.Same(item, Assert.Single(view.Model.Items));

            settings.ShowUserAvatars = false;
            await runtime.ApplySettingsAsync();
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.False(avatar.IsVisible);
            Assert.Null(avatar.AvatarImage);
            Assert.Same(item, Assert.Single(view.Model.Items));

            settings.ShowUserAvatars = true;
            await runtime.ApplySettingsAsync();
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.True(avatar.IsEffectivelyVisible);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>生成真实圆形图片的宽松和紧凑布局预览，所有窗口在完成后关闭。</summary>
    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RenderAvatarPreviews(bool compact, bool narrow)
    {
        using var directory = new MessageStoreTestDirectory();
        var imagePath = Path.Combine(directory.Path, "avatar.png");
        Directory.CreateDirectory(directory.Path);
        using (var image = new RenderTargetBitmap(new(128, 128)))
        {
            var picture = new Grid
            {
                Background = new SolidColorBrush(Color.Parse("#6D6EDB")),
                Children =
                {
                    new TextBlock
                    {
                        Text = "夜", Foreground = Brushes.White, FontSize = 76,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                }
            };
            picture.Measure(new(128, 128));
            picture.Arrange(new(0, 0, 128, 128));
            image.Render(picture);
            image.Save(imagePath);
        }
        var service = new ControlledUserAvatarService { Resolve = (_, _) => Task.FromResult(imagePath) };
        var settings = new PanelSettings { CompactDanmuDisplay = compact };
        var content = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        var surface = new Grid { Children = { content } };
        surface.Classes.Set("compactDanmu", compact);
        var rows = new List<MessageFeedRow>();
        foreach (var kind in Enum.GetValues<MessageKind>())
        {
            var label = new TextBlock { Text = kind.ToString(), FontSize = 13, Foreground = Brushes.Gray };
            var row = new MessageFeedRow(kind, service)
            {
                DataContext = new MessageItemViewModel(CreateMessage(kind, 12345), settings, new PinyinService())
            };
            rows.Add(row);
            content.Children.Add(new StackPanel { Spacing = 4, Children = { label, row } });
        }
        var window = new Window
        {
            Content = surface, Width = narrow ? 380 : 820, Height = 520, Background = Brushes.White,
            RequestedThemeVariant = ThemeVariant.Light
        };
        try
        {
            window.Show();
            Pump(window);
            var avatars = rows.Select(row => Assert.Single(row.GetVisualDescendants().OfType<UserAvatarView>(),
                value => value.IsEffectivelyVisible)).ToArray();
            for (var attempt = 0; attempt < 100 && avatars.Any(avatar => avatar.AvatarImage is null); attempt++)
            {
                await Task.Delay(10);
                Pump(window);
            }
            Assert.All(avatars, avatar => Assert.NotNull(avatar.AvatarImage));
            Pump(window);
            var previewDirectory = Path.Combine(Path.GetTempPath(), "danmaku-panel-previews");
            Directory.CreateDirectory(previewDirectory);
            using var preview = window.CaptureRenderedFrame();
            preview!.Save(Path.Combine(previewDirectory,
                $"user-avatars-{(compact ? "compact" : "normal")}-{(narrow ? "narrow" : "wide")}.png"));
        }
        finally
        {
            window.Close();
        }
    }

    private static MessageRecord CreateMessage(MessageKind kind, long userUid)
    {
        return new()
        {
            RoomTabId = "tab", RoomId = "123", UserUid = userUid, Kind = kind,
            UserName = "夜更小芙", Content = "晚上好！今天的直播也很开心。", MedalName = "夜更中", MedalLevel = 20,
            GiftName = "小花花", Quantity = 3, UnitPrice = 10, TotalPrice = 30, GuardLevel = 3
        };
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
