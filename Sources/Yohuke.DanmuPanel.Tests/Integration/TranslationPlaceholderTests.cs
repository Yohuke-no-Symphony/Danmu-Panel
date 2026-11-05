using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证真实翻译队列在译文位置的等待占位及原位替换。</summary>
public sealed class TranslationPlaceholderTests
{
    /// <summary>普通、紧凑和两种翻译模式均在开启后显示占位，完成后复用原行显示译文。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, false, TranslationDisplayMode.Bilingual)]
    [InlineData(MessageKind.Danmu, true, TranslationDisplayMode.Bilingual)]
    [InlineData(MessageKind.SuperChat, false, TranslationDisplayMode.Bilingual)]
    [InlineData(MessageKind.SuperChat, true, TranslationDisplayMode.Bilingual)]
    [InlineData(MessageKind.Danmu, false, TranslationDisplayMode.TranslationOnly)]
    [InlineData(MessageKind.Danmu, true, TranslationDisplayMode.TranslationOnly)]
    [InlineData(MessageKind.SuperChat, false, TranslationDisplayMode.TranslationOnly)]
    [InlineData(MessageKind.SuperChat, true, TranslationDisplayMode.TranslationOnly)]
    public async Task EnablingTranslationShowsPlaceholderAndCompletionReusesRow(MessageKind kind, bool compact,
        TranslationDisplayMode mode)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = compact;
        settings.Translation.DisplayMode = mode;
        var message = new MessageRecord { RoomTabId = "tab", Kind = kind, UserName = "viewer", Content = "原文内容" };
        await runtime.Messages.AppendAsync(message, settings.Reading);
        var view = new MessageFeedView(runtime, kind, exception => throw exception);
        var window = new Window { Width = 640, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var row = FindRow(view, message.Id);
            var placeholder = Assert.Single(row.GetVisualDescendants().OfType<ShimmerTextBlock>());
            Assert.False(placeholder.IsEffectivelyVisible);

            settings.Translation.Enabled = true;
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.True(item.IsTranslating);
            Assert.True(item.ShowOriginal);
            Assert.True(placeholder.IsEffectivelyVisible);
            Assert.True(placeholder.IsShimmering);
            Assert.Equal(Localization.Get("Panel.Translating"), placeholder.Text);
            Assert.Equal(compact ? 12 : 13, placeholder.FontSize);
            Assert.False(item.HasTranslation);
            Assert.DoesNotContain(row.GetVisualDescendants().OfType<Control>(), control =>
                control.Classes.Contains("translatedText") && control.IsEffectivelyVisible);

            settings.MessageTextScale = 1.5;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.Equal((compact ? 12 : 13) * 1.5, placeholder.FontSize);
            settings.Translation.Enabled = false;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.False(placeholder.IsEffectivelyVisible);
            Assert.False(placeholder.IsShimmering);
            settings.Translation.Enabled = true;
            await view.Model.RefreshAsync();
            Pump(window);
            Assert.True(placeholder.IsEffectivelyVisible);

            var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
            Assert.True(await runtime.Messages.ApplyTranslationAsync(message.Id, message.Generation, fingerprint, "Translated content"));
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.Same(row, FindRow(view, message.Id));
            Assert.False(item.IsTranslating);
            Assert.False(placeholder.IsEffectivelyVisible);
            Assert.False(placeholder.IsShimmering);
            Assert.True(item.HasTranslation);
            Assert.Equal(mode == TranslationDisplayMode.Bilingual, item.ShowOriginal);
            Assert.Contains(row.GetVisualDescendants().OfType<Control>(), control =>
                control.Classes.Contains("translatedText") && control.IsEffectivelyVisible);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>相同译文完成后折叠整行及间距，在两种显示模式下均保留居中的原文。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, false, TranslationDisplayMode.Bilingual)]
    [InlineData(MessageKind.Danmu, true, TranslationDisplayMode.Bilingual)]
    [InlineData(MessageKind.SuperChat, false, TranslationDisplayMode.Bilingual)]
    [InlineData(MessageKind.SuperChat, true, TranslationDisplayMode.Bilingual)]
    [InlineData(MessageKind.Danmu, false, TranslationDisplayMode.TranslationOnly)]
    [InlineData(MessageKind.Danmu, true, TranslationDisplayMode.TranslationOnly)]
    [InlineData(MessageKind.SuperChat, false, TranslationDisplayMode.TranslationOnly)]
    [InlineData(MessageKind.SuperChat, true, TranslationDisplayMode.TranslationOnly)]
    public async Task IdenticalTranslationCollapsesLineAndCentersOriginal(MessageKind kind, bool compact,
        TranslationDisplayMode mode)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.CompactDanmuDisplay = compact;
        settings.Translation.Enabled = true;
        settings.Translation.DisplayMode = mode;
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        var message = new MessageRecord { RoomTabId = "tab", Kind = kind, UserName = "viewer", Content = "原文内容" };
        await runtime.Messages.AppendAsync(message, settings.Reading, fingerprint);
        var view = new MessageFeedView(runtime, kind, exception => throw exception);
        var window = new Window { Width = 640, Height = 400, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab" });
            window.Show();
            Pump(window);
            var item = Assert.Single(view.Model.Items);
            var row = FindRow(view, message.Id);
            var body = row.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Classes.Contains("messageBody"));
            var pendingHeight = body.Bounds.Height;
            Assert.True(item.IsTranslating);

            Assert.True(await runtime.Messages.ApplyTranslationAsync(message.Id, message.Generation, fingerprint, message.Content));
            await view.Model.RefreshAsync();
            Pump(window);

            Assert.Same(item, Assert.Single(view.Model.Items));
            Assert.Same(row, FindRow(view, message.Id));
            Assert.False(item.IsTranslating);
            Assert.True(item.ShowOriginal);
            var translationLine = row.GetVisualDescendants().OfType<Grid>().Single(grid => grid.Classes.Contains("translationLine"));
            Assert.False(translationLine.IsVisible);
            Assert.False(Assert.Single(row.GetVisualDescendants().OfType<ShimmerTextBlock>()).IsShimmering);
            Assert.Single(row.GetVisualDescendants().OfType<Control>(), control =>
                control.Classes.Contains("originalText") && control.IsEffectivelyVisible);
            Assert.DoesNotContain(row.GetVisualDescendants().OfType<Control>(), control =>
                control.Classes.Contains("translatedText") && control.IsEffectivelyVisible);
            Assert.True(body.Bounds.Height < pendingHeight);
            Assert.InRange(Math.Abs(body.Bounds.Height - body.Children[0].Bounds.Height), 0, 0.5);
            Assert.Equal(VerticalAlignment.Center, body.VerticalAlignment);
            if (kind == MessageKind.Danmu)
            {
                var layout = Assert.IsType<Grid>(body.Parent);
                Assert.InRange(Math.Abs(body.Bounds.Center.Y - layout.Bounds.Height / 2), 0, 0.5);
            }
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>失败和实时模式淘汰的任务结束等待，重新排队后恢复占位。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrExpiredJobsStopPlaceholderAndRetryRestartsIt(bool expire)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.Translation.Enabled = true;
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        var message = new MessageRecord { RoomTabId = "tab", Kind = MessageKind.Danmu, Content = "待翻译的文字" };
        await runtime.Messages.AppendAsync(message, settings.Reading, fingerprint);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(new() { Id = "tab" });
        var item = Assert.Single(model.Items);
        Assert.True(item.IsTranslating);
        if (expire)
        {
            Assert.Equal(1, await runtime.Messages.TrimPendingAsync(DateTimeOffset.UtcNow.AddMinutes(1), 0, []));
        }
        else
        {
            Assert.True(await runtime.Messages.FailAsync(Assert.Single(await runtime.Messages.GetPendingAsync(1)), "failed"));
        }
        await model.RefreshAsync();
        Assert.False(item.IsTranslating);
        Assert.False(item.ShowTranslation);
        Assert.True(item.ShowOriginal);

        Assert.True(await runtime.Messages.UpsertTranslationJobAsync(message.Id, message.Generation, fingerprint));
        await model.RefreshAsync();
        Assert.True(item.IsTranslating);
        Assert.Same(item, Assert.Single(model.Items));
    }

    /// <summary>纯表情、空文本、礼物和舰长消息不显示翻译等待。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, "😀🎉")]
    [InlineData(MessageKind.Danmu, "[smile]")]
    [InlineData(MessageKind.SuperChat, " ")]
    [InlineData(MessageKind.Gift, "礼物")]
    [InlineData(MessageKind.Guard, "舰长")]
    public async Task UntranslatableMessagesDoNotShowPlaceholder(MessageKind kind, string content)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.Translation.Enabled = true;
        var message = new MessageRecord
        {
            RoomTabId = "tab", Kind = kind, Content = content, GuardLevel = 3,
            Emoticons = [new() { Text = "[smile]", Url = "" }]
        };
        await runtime.Messages.AppendAsync(message, settings.Reading,
            TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation));
        using var model = new FeedViewModel(runtime, kind);
        await model.SelectRoomAsync(new() { Id = "tab" });
        Assert.False(Assert.Single(model.Items).IsTranslating);
    }

    private static Button FindRow(MessageFeedView view, string id)
    {
        return view.GetVisualDescendants().OfType<Button>().Single(button =>
            button.DataContext is MessageItemViewModel item && item.Record.Id == id);
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
