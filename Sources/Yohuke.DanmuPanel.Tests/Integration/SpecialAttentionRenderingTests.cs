using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Models.Workspace;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Windowing;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证特别关注强调普通弹幕的用户名和正文，并在原位刷新和行复用后恢复样式。</summary>
public sealed class SpecialAttentionRenderingTests
{
    /// <summary>常规、紧凑和换行模式分别设置用户名和正文颜色，并保留译文、辅助信息及注音样式。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, PinyinLayoutMode.Ruby, false)]
    [InlineData(false, false, PinyinLayoutMode.SeparateLine, true)]
    [InlineData(true, false, PinyinLayoutMode.Ruby, true)]
    [InlineData(true, false, PinyinLayoutMode.SeparateLine, false)]
    [InlineData(true, true, PinyinLayoutMode.Ruby, false)]
    [InlineData(true, true, PinyinLayoutMode.SeparateLine, true)]
    public void ExistingRowsEmphasizeNameAndBodyAndRestoreAfterRemoval(bool compact, bool wrap,
        PinyinLayoutMode pinyinLayout, bool dark)
    {
        var settings = CreateSettings(pinyinLayout);
        var record = CreateRecord(settings);
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        var row = new MessageFeedRow(MessageKind.Danmu) { DataContext = model };
        var window = CreateWindow(row, compact, wrap, dark);
        var attentionBrush = new SolidColorBrush(Color.Parse("#C026D3"));
        var userNameBrush = new SolidColorBrush(Color.Parse("#2563EB"));
        window.Resources["SpecialAttentionContentBrush"] = attentionBrush;
        window.Resources["SpecialAttentionUserNameBrush"] = userNameBrush;
        try
        {
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            var original = FindOriginal(row, compact, wrap);
            var translated = FindTranslated(row, compact, wrap);
            var originalBrush = original.Foreground;
            var translatedBrush = translated.Foreground;
            var originalWeight = original.FontWeight;
            var translatedWeight = translated.FontWeight;
            var name = Assert.Single(row.GetVisualDescendants().OfType<EmoticonTextBlock>(),
                text => text.Classes.Contains("danmuFeedUser") && text.IsEffectivelyVisible);
            var nameBrush = name.Foreground;
            var nameWeight = name.FontWeight;
            var namePinyinBrush = name.PinyinForeground;
            var nameAnnotations = model.NameAnnotations;
            var time = Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Classes.Contains("danmuFeedMeta") &&
                    !text.GetVisualAncestors().OfType<MessageEventSummarySurface>().Any());
            var timeBrush = time.Foreground;
            var medal = row.GetVisualDescendants().OfType<TextBlock>()
                .First(text => text.Classes.Contains("danmuMedalText"));
            var medalBrush = medal.Foreground;
            var pinyinBrush = original.PinyinForeground;
            var annotations = model.ContentPinyin;

            settings.SpecialAttention.Users.Add(new() { UserUid = record.UserUid, DisplayName = "关注用户" });
            model.Update(record, settings);
            PinyinTextRenderingTests.Pump(window);

            Assert.Same(original, FindOriginal(row, compact, wrap));
            Assert.Same(translated, FindTranslated(row, compact, wrap));
            Assert.Equal(FontWeight.Bold, original.FontWeight);
            Assert.Equal(translatedWeight, translated.FontWeight);
            Assert.Same(attentionBrush, original.Foreground);
            Assert.Same(translatedBrush, translated.Foreground);
            Assert.Same(userNameBrush, name.Foreground);
            Assert.Equal(FontWeight.Bold, name.FontWeight);
            Assert.Same(namePinyinBrush, name.PinyinForeground);
            Assert.Same(nameAnnotations, model.NameAnnotations);
            Assert.Same(timeBrush, time.Foreground);
            Assert.Same(medalBrush, medal.Foreground);
            Assert.Same(pinyinBrush, original.PinyinForeground);
            Assert.Same(annotations, model.ContentPinyin);
            CaptureIfRequested(window,
                $"special-attention-row-{(dark ? "dark" : "light")}-{(compact ? "compact" : "normal")}-{(wrap ? "wrap" : "single")}-{pinyinLayout}.png");
            if (pinyinLayout == PinyinLayoutMode.Ruby)
            {
                var nameSyllables = name.GetVisualDescendants().OfType<PinyinRubySyllable>().ToArray();
                Assert.NotEmpty(nameSyllables);
                Assert.All(nameSyllables, syllable =>
                {
                    Assert.Equal(FontWeight.Bold, syllable.OriginalText.FontWeight);
                    Assert.Same(userNameBrush, syllable.OriginalText.Foreground);
                    Assert.Equal(FontWeight.Normal, syllable.PronunciationText.FontWeight);
                    Assert.Same(namePinyinBrush, syllable.PronunciationText.Foreground);
                });
                var syllables = original.GetVisualDescendants().OfType<PinyinRubySyllable>().ToArray();
                Assert.NotEmpty(syllables);
                Assert.All(syllables, syllable =>
                {
                    Assert.Equal(FontWeight.Bold, syllable.OriginalText.FontWeight);
                    Assert.Same(attentionBrush, syllable.OriginalText.Foreground);
                    Assert.Equal(FontWeight.Normal, syllable.PronunciationText.FontWeight);
                    Assert.Same(pinyinBrush, syllable.PronunciationText.Foreground);
                });
            }

            var changedBrush = new SolidColorBrush(Color.Parse("#16A34A"));
            window.Resources["SpecialAttentionContentBrush"] = changedBrush;
            PinyinTextRenderingTests.Pump(window);
            Assert.Same(changedBrush, original.Foreground);
            Assert.Same(translatedBrush, translated.Foreground);
            Assert.Equal(translatedWeight, translated.FontWeight);
            Assert.Same(userNameBrush, name.Foreground);
            var changedUserNameBrush = new SolidColorBrush(Color.Parse("#EA580C"));
            window.Resources["SpecialAttentionUserNameBrush"] = changedUserNameBrush;
            PinyinTextRenderingTests.Pump(window);
            Assert.Same(changedUserNameBrush, name.Foreground);
            Assert.Same(changedBrush, original.Foreground);
            Assert.Same(translatedBrush, translated.Foreground);

            settings.SpecialAttention.Users.Clear();
            model.Update(record, settings);
            PinyinTextRenderingTests.Pump(window);

            Assert.Same(originalBrush, original.Foreground);
            Assert.Same(translatedBrush, translated.Foreground);
            Assert.Equal(originalWeight, original.FontWeight);
            Assert.Equal(translatedWeight, translated.FontWeight);
            Assert.Same(nameBrush, name.Foreground);
            Assert.Equal(nameWeight, name.FontWeight);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>容器从关注用户切换到普通用户时清除强调，再次绑定关注用户时恢复。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void RebindingRowsDoesNotLeakAttentionTypography(bool compact)
    {
        var settings = CreateSettings(PinyinLayoutMode.Ruby);
        settings.SpecialAttention.Users.Add(new() { UserUid = 42 });
        var followed = new MessageItemViewModel(CreateRecord(settings), settings, new PinyinService());
        var ordinary = new MessageItemViewModel(CreateRecord(settings) with { UserUid = 73 }, settings, new PinyinService());
        var row = new MessageFeedRow(MessageKind.Danmu) { DataContext = ordinary };
        var window = CreateWindow(row, compact, false, false);
        var attentionBrush = new SolidColorBrush(Color.Parse("#C026D3"));
        window.Resources["SpecialAttentionContentBrush"] = attentionBrush;
        window.Resources["SpecialAttentionUserNameBrush"] = Brushes.DarkCyan;
        try
        {
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            var body = FindOriginal(row, compact, false);
            var normalBrush = body.Foreground;
            var normalWeight = body.FontWeight;
            var name = Assert.Single(row.GetVisualDescendants().OfType<EmoticonTextBlock>(),
                text => text.Classes.Contains("danmuFeedUser") && text.IsEffectivelyVisible);
            var nameBrush = name.Foreground;
            var nameWeight = name.FontWeight;

            row.DataContext = followed;
            PinyinTextRenderingTests.Pump(window);
            Assert.Same(body, FindOriginal(row, compact, false));
            Assert.Equal(FontWeight.Bold, body.FontWeight);
            Assert.Same(attentionBrush, body.Foreground);
            Assert.Same(Brushes.DarkCyan, name.Foreground);
            Assert.Equal(FontWeight.Bold, name.FontWeight);

            row.DataContext = ordinary;
            PinyinTextRenderingTests.Pump(window);
            Assert.Same(normalBrush, body.Foreground);
            Assert.Equal(normalWeight, body.FontWeight);
            Assert.Same(nameBrush, name.Foreground);
            Assert.Equal(nameWeight, name.FontWeight);

            row.DataContext = followed;
            PinyinTextRenderingTests.Pump(window);
            Assert.Equal(FontWeight.Bold, body.FontWeight);
            Assert.Same(attentionBrush, body.Foreground);
            Assert.Same(Brushes.DarkCyan, name.Foreground);
            Assert.Equal(FontWeight.Bold, name.FontWeight);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>关注用户发送的其他消息类型继续使用各自正文颜色与字重。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public void OtherMessageKindsRetainTheirTypography(MessageKind kind)
    {
        var settings = CreateSettings(PinyinLayoutMode.Ruby);
        var record = CreateRecord(settings) with { Kind = kind, GiftName = "鲜花", GuardLevel = 3 };
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        var row = new MessageFeedRow(kind) { DataContext = model };
        var window = CreateWindow(row, false, false, false);
        try
        {
            window.Show();
            PinyinTextRenderingTests.Pump(window);
            var original = FindOriginal(row, false, false);
            var originalBrush = original.Foreground;
            var originalWeight = original.FontWeight;

            settings.SpecialAttention.Users.Add(new() { UserUid = record.UserUid });
            model.Update(record, settings);
            PinyinTextRenderingTests.Pump(window);

            Assert.Same(originalBrush, original.Foreground);
            Assert.Equal(originalWeight, original.FontWeight);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>正文变粗改色时保留内联表情图片及其不透明度。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmoticonImagesKeepTheirSourceDuringAttentionChanges(bool compact)
    {
        using var directory = new MessageStoreTestDirectory();
        Directory.CreateDirectory(directory.Path);
        var imagePath = Path.Combine(directory.Path, "attention.png");
        using (var bitmap = new RenderTargetBitmap(new(16, 16)))
        {
            bitmap.Save(imagePath);
        }
        var settings = CreateSettings(PinyinLayoutMode.Ruby);
        settings.ShowDanmuPinyin = false;
        var record = CreateRecord(settings) with { Content = "正文[笑]正文" };
        var model = new MessageItemViewModel(record, settings, new PinyinService())
        {
            Emoticons = [new DanmuEmoticon { Text = "[笑]", ImagePath = imagePath }]
        };
        var row = new MessageFeedRow(MessageKind.Danmu) { DataContext = model };
        var window = CreateWindow(row, compact, false, false);
        window.Resources["SpecialAttentionContentBrush"] = Brushes.DarkMagenta;
        try
        {
            window.Show();
            await EmoticonImageTestHelper.LoadAsync(window);
            var original = FindOriginal(row, compact, false);
            var image = Assert.IsType<Image>(Assert.Single(original.Inlines!.OfType<InlineUIContainer>()).Child);
            var source = image.Source;

            settings.SpecialAttention.Users.Add(new() { UserUid = record.UserUid });
            model.Update(record, settings);
            PinyinTextRenderingTests.Pump(window);

            Assert.Equal(FontWeight.Bold, original.FontWeight);
            Assert.Same(Brushes.DarkMagenta, original.Foreground);
            Assert.Same(source, image.Source);
            Assert.Equal(1, image.Opacity);
            Assert.Same(image, Assert.IsType<Image>(Assert.Single(original.Inlines!.OfType<InlineUIContainer>()).Child));
        }
        finally
        {
            window.Close();
        }
    }

    private static void CaptureIfRequested(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_SPECIAL_ATTENTION_SCREENSHOTS") is not { Length: > 0 } directory)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(directory, name));
    }

    private static PanelSettings CreateSettings(PinyinLayoutMode pinyinLayout)
    {
        return new()
        {
            ShowUserPinyin = true,
            ShowDanmuPinyin = true,
            PinyinLayout = pinyinLayout,
            AutoScrollLongMessages = false
        };
    }

    private static MessageRecord CreateRecord(PanelSettings settings)
    {
        return new()
        {
            Kind = MessageKind.Danmu, UserUid = 42, UserName = "中文用户", Content = "你好世界", MedalName = "粉丝牌", MedalLevel = 12,
            Translation = "Hello world", TranslationFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation)
        };
    }

    private static Window CreateWindow(MessageFeedRow row, bool compact, bool wrap, bool dark)
    {
        var host = new Grid { Children = { row } };
        if (compact)
        {
            host.Classes.Add("compactDanmu");
        }
        if (wrap)
        {
            host.Classes.Add("compactDanmuWrap");
        }
        return new()
        {
            Width = 850, Height = 280, Content = host,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
    }

    private static EmoticonTextBlock FindOriginal(MessageFeedRow row, bool compact, bool wrap)
    {
        if (compact && !wrap)
        {
            var marquee = Assert.Single(row.GetVisualDescendants().OfType<HoverMarqueeTextBlock>(),
                control => control.Classes.Contains("originalText") && control.IsEffectivelyVisible);
            return Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>());
        }
        return Assert.Single(row.GetVisualDescendants().OfType<EmoticonTextBlock>(),
            control => control.Classes.Contains("originalText") && control.IsEffectivelyVisible);
    }

    private static TextBlock FindTranslated(MessageFeedRow row, bool compact, bool wrap)
    {
        if (compact && !wrap)
        {
            var marquee = Assert.Single(row.GetVisualDescendants().OfType<HoverMarqueeTextBlock>(),
                control => control.Classes.Contains("translatedText") && control.IsEffectivelyVisible);
            return Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>());
        }
        return Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(),
            control => control.Classes.Contains("translatedText") && control.IsEffectivelyVisible);
    }
}
