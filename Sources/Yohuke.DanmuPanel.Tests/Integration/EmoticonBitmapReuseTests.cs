using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Windowing;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证同一消息面板内的表情复用和面板卸载后的位图释放。</summary>
public sealed class EmoticonBitmapReuseTests
{
    /// <summary>置顶和滚动消息共享解码结果，正文显隐不重复解码，卸载后释放并支持重新挂载。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinnedAndScrollingRowsShareImagesUntilTheFeedDetaches(bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var imagePath = Path.Combine(directory.Path, "shared.png");
        using (var bitmap = new RenderTargetBitmap(new(64, 32)))
        {
            bitmap.Save(imagePath);
        }
        var configuration = new PanelConfiguration();
        configuration.Settings.Translation.Enabled = false;
        configuration.Settings.CompactDanmuDisplay = compact;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var records = Enumerable.Range(0, 3).Select(index => new MessageRecord
        {
            Id = "shared-" + index,
            RoomTabId = "shared",
            Kind = MessageKind.Danmu,
            IsTest = true,
            UserName = "viewer",
            Content = "[笑] " + index,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        }).ToArray();
        await runtime.Messages.AppendTestMessagesAsync(records, configuration.Settings.Reading);
        await runtime.Messages.TogglePinAsync(records[0].Id);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, MessageKind.Danmu, errors.Add);
        var host = new Border { Child = view };
        var window = new Window { Width = 600, Height = 500, Content = host };
        try
        {
            await view.Model.SelectRoomAsync(new RoomTabState { Id = "shared" });
            await view.Model.WaitForPresentationAsync();
            foreach (var item in view.Model.Items)
            {
                item.Emoticons = [new DanmuEmoticon { Text = "[笑]", ImagePath = imagePath }];
            }
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            var texts = VisibleEmoticonTexts(view);
            Assert.Equal(3, texts.Length);
            var shared = Image(texts[0]);
            Assert.All(texts, text => Assert.Same(shared, Image(text)));

            texts[0].IsVisible = false;
            Assert.True(shared.PixelSize.Height > 0);
            Assert.All(texts.Skip(1), text => Assert.Same(shared, Image(text)));
            texts[0].IsVisible = true;
            Assert.Same(shared, Image(texts[0]));
            texts[0].ContentText += " updated";
            Assert.Same(shared, Image(texts[0]));

            host.Child = null;
            Assert.Throws<NullReferenceException>(() => shared.PixelSize);
            host.Child = view;
            await EmoticonImageTestHelper.LoadAsync(window);
            var restored = VisibleEmoticonTexts(view);
            Assert.Equal(3, restored.Length);
            var reloaded = Image(restored[0]);
            Assert.NotSame(shared, reloaded);
            Assert.All(restored, text => Assert.Same(reloaded, Image(text)));
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static EmoticonTextBlock[] VisibleEmoticonTexts(MessageFeedView view)
    {
        return view.GetVisualDescendants().OfType<EmoticonTextBlock>()
            .Where(text => text.IsEffectivelyVisible && text.Emoticons is { Count: > 0 }).ToArray();
    }

    private static Bitmap Image(EmoticonTextBlock text)
    {
        return Assert.IsType<Bitmap>(Assert.IsType<Image>(Assert.Single(text.Inlines!
            .OfType<InlineUIContainer>()).Child).Source);
    }
}
