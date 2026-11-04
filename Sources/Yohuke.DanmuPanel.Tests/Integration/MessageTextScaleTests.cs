using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>通过真实消息行验证四类面板的文字缩放与显示对象复用。</summary>
public sealed class MessageTextScaleTests
{
    /// <summary>普通和紧凑消息均在原有行上同步缩放正文、身份、金额及注音，恢复原比例时不累积误差。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, false)]
    [InlineData(MessageKind.Danmu, true)]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.SuperChat, true)]
    [InlineData(MessageKind.Gift, false)]
    [InlineData(MessageKind.Gift, true)]
    [InlineData(MessageKind.Guard, false)]
    [InlineData(MessageKind.Guard, true)]
    public async Task ExistingRowsScaleAllMessageTextAndRestoreTheOriginalSizes(MessageKind kind, bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        Assert.Equal(1, settings.MessageTextScale);
        settings.CompactDanmuDisplay = compact;
        settings.CompactDanmuWrap = false;
        settings.CompactSuperChatWrap = false;
        settings.AutoScrollLongMessages = false;
        settings.ShowUserPinyin = true;
        settings.ShowGiftPinyin = true;
        settings.ShowDanmuPinyin = true;
        settings.ShowSuperChatPinyin = true;
        settings.PinyinLayout = PinyinLayoutMode.Ruby;
        var message = new MessageRecord
        {
            RoomTabId = "scale-room", RoomId = "123", Kind = kind, UserName = "中文观众",
            Content = "你好，世界", GiftName = "鲜花", GuardLevel = 3, Quantity = 2,
            TotalPrice = 30, UnitPrice = 15, MedalName = "粉丝牌", MedalLevel = 12, MedalGuardLevel = 2
        };
        await runtime.Messages.AppendAsync(message, settings.Reading);
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var workbenchText = new TextBlock { Text = "工作区标题" };
        var host = new DockPanel { Children = { workbenchText, view } };
        DockPanel.SetDock(workbenchText, Avalonia.Controls.Dock.Top);
        var window = new Window { Width = 900, Height = 420, Content = host };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "scale-room", RoomId = "123" });
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var row = FindRow(view, message.Id);
            var name = FindName(row);
            var body = FindBody(row, compact);
            var nameAnnotations = item.NameAnnotations;
            var contentAnnotations = item.ContentPinyin;
            var workbenchFontSize = workbenchText.FontSize;
            Assert.Equal(message.UserName, name.ContentText);
            Assert.Equal(item.Detail, body.ContentText);
            Assert.True(row.Bounds.Height > 0);
            AssertScaledTypography(row, name, body, kind, compact, 1);

            foreach (var scale in new[] { 1.5, 1.0 })
            {
                settings.MessageTextScale = scale;
                await view.Model.RefreshAsync();
                Pump(window);

                Assert.Equal(scale, view.Model.MessageTextScale);
                Assert.Same(item, Assert.Single(view.Model.Items));
                Assert.Same(row, FindRow(view, message.Id));
                Assert.Same(name, FindName(row));
                Assert.Same(body, FindBody(row, compact));
                Assert.Same(nameAnnotations, item.NameAnnotations);
                Assert.Same(contentAnnotations, item.ContentPinyin);
                Assert.Equal(workbenchFontSize, workbenchText.FontSize);
                AssertScaledTypography(row, name, body, kind, compact, scale);
                if (scale == 1.5)
                {
                    CaptureScaledPanel(window, kind, compact);
                }
            }
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static void AssertScaledTypography(Button row, EmoticonTextBlock name, EmoticonTextBlock body,
        MessageKind kind, bool compact, double scale)
    {
        var bodySize = (compact ? 12 : 13) * scale;
        Assert.Equal(bodySize, name.FontSize);
        Assert.Equal(bodySize, body.FontSize);
        Assert.True(name.IsEffectivelyVisible);
        Assert.True(body.IsEffectivelyVisible);
        var metadata = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Classes.Contains("danmuFeedMeta"));
        Assert.Equal(11 * scale, metadata.FontSize);
        Assert.Equal(!compact, metadata.IsEffectivelyVisible);
        Assert.False(string.IsNullOrEmpty(metadata.Text));
        var medalText = row.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.Classes.Contains("danmuMedalText")).ToArray();
        var medalSlots = row.GetVisualDescendants().OfType<Grid>()
            .Where(control => control.Classes.Contains("danmuMedalSlot")).ToArray();
        if (kind == MessageKind.SuperChat)
        {
            Assert.Empty(medalText);
            Assert.Empty(medalSlots);
        }
        else
        {
            Assert.NotEmpty(medalText);
            Assert.All(medalText, text => Assert.Equal(11 * scale, text.FontSize));
            var medalLevel = Assert.Single(medalText, text => text.Classes.Contains("danmuMedalLevel"));
            Assert.Equal("12", medalLevel.Text);
            Assert.Equal(20 * scale, medalLevel.Width);
            var medalSlot = Assert.Single(medalSlots);
            Assert.Equal(66 * scale, medalSlot.Width);
            Assert.Equal(66 * scale, medalSlot.Bounds.Width);
        }
        AssertAnnotatedTextSize(name, bodySize, 10 * scale);
        Assert.Equal(10 * scale, body.PinyinFontSize);
        if (kind == MessageKind.Guard)
        {
            Assert.Empty(body.GetVisualDescendants().OfType<PinyinRubySyllable>());
        }
        else
        {
            AssertAnnotatedTextSize(body, bodySize, 10 * scale);
        }
        if (kind == MessageKind.SuperChat)
        {
            var amount = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Classes.Contains("scFeedPrice"));
            Assert.Equal("￥30", amount.Text);
            Assert.Equal(bodySize, amount.FontSize);
        }
        if (kind == MessageKind.Danmu)
        {
            var pin = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Classes.Contains("messagePinBadge"));
            Assert.Equal(10 * scale, pin.FontSize);
            Assert.Equal(14 * scale, pin.Width);
            Assert.Equal(14 * scale, pin.Height);
        }
    }

    private static void AssertAnnotatedTextSize(EmoticonTextBlock owner, double bodySize, double pinyinSize)
    {
        Assert.Equal(pinyinSize, owner.PinyinFontSize);
        var syllables = owner.GetVisualDescendants().OfType<PinyinRubySyllable>().ToArray();
        Assert.NotEmpty(syllables);
        Assert.All(syllables, syllable =>
        {
            Assert.Equal(bodySize, syllable.OriginalText.FontSize);
            Assert.Equal(pinyinSize, syllable.PronunciationText.FontSize);
        });
    }

    private static Button FindRow(MessageFeedView view, string id)
    {
        return Assert.Single(view.GetVisualDescendants().OfType<Button>(), button =>
            button.DataContext is MessageItemViewModel item && item.Record.Id == id);
    }

    private static EmoticonTextBlock FindName(Button row)
    {
        return Assert.Single(row.GetVisualDescendants().OfType<EmoticonTextBlock>(),
            control => control.Classes.Contains("danmuFeedUser"));
    }

    private static EmoticonTextBlock FindBody(Button row, bool compact)
    {
        if (compact)
        {
            var marquee = Assert.Single(row.GetVisualDescendants().OfType<HoverMarqueeTextBlock>(),
                control => control.Classes.Contains("originalText") && control.IsEffectivelyVisible);
            return Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>());
        }
        return Assert.Single(row.GetVisualDescendants().OfType<EmoticonTextBlock>(),
            control => control.Classes.Contains("originalText") && control.IsEffectivelyVisible);
    }

    private static void CaptureScaledPanel(Window window, MessageKind kind, bool compact)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_PINYIN_SCREENSHOTS") is not { Length: > 0 } directory)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var mode = compact ? "compact" : "normal";
        frame.Save(Path.Combine(directory, $"text-scale-{kind}-{mode}-150.png"));
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
