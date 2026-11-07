using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Xunit;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证异步图片结果不会越过正文换绑、隐藏和脱离的生命周期。</summary>
public sealed class AsyncEmoticonLoadingTests
{
    /// <summary>图片加载期间换绑或卸载，旧完成结果不会重新显示旧消息；恢复后仍能显示当前图片。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldImageResultsCannotRestoreReboundOrDetachedContent(bool detach)
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var path = Path.Combine(directory.Path, "old.png");
        using (var bitmap = new RenderTargetBitmap(new(512, 256)))
        {
            bitmap.Save(path);
        }
        var cache = new EmoticonBitmapCache();
        var text = new EmoticonTextBlock
        {
            BitmapCache = cache,
            ContentText = "[old]",
            Emoticons = [new DanmuEmoticon { Text = "[old]", ImagePath = path }]
        };
        var host = new Border { Child = text };
        var window = new Window { Width = 360, Height = 180, Content = host };
        try
        {
            window.Show();
            var oldLoad = text.PendingImageLoad;
            if (detach)
            {
                host.Child = null;
            }
            text.ContentText = "current";
            text.Emoticons = [];
            await oldLoad;
            if (detach)
            {
                host.Child = text;
            }
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.Equal("current", text.Text);
            Assert.Null(text.Inlines);
            Assert.Equal(0, cache.PendingCount);

            text.ContentText = "[current]";
            text.Emoticons = [new DanmuEmoticon { Text = "[current]", ImagePath = path }];
            await EmoticonImageTestHelper.LoadAsync(window);
            Assert.Contains(text.Inlines!, inline => inline is Avalonia.Controls.Documents.InlineUIContainer);
        }
        finally
        {
            window.Close();
            cache.Clear();
        }
    }
}
