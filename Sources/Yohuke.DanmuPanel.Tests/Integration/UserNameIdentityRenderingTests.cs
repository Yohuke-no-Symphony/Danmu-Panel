using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证本房间身份只影响用户名，并与特别关注、配色更新及行复用正确协作。</summary>
public sealed class UserNameIdentityRenderingTests
{
    /// <summary>深浅主题和三种布局中，名称按身份切色，房管和特别关注依次覆盖舰队身份。</summary>
    [AvaloniaTheory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void ExistingRowsOnlyColorTheNameAndRestoreTheirRole(bool compact, bool wrap, bool dark)
    {
        var settings = new PanelSettings { ShowUserAvatars = false, ShowUserPinyin = true, ShowDanmuPinyin = true };
        var record = new MessageRecord
        {
            UserUid = 42, UserName = "中文观众", Content = "普通正文", MedalName = "别家牌", MedalLevel = 21,
            MedalGuardLevel = 1, Translation = "Original translation color",
            TranslationFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation)
        };
        var model = new MessageItemViewModel(record, settings, new PinyinService());
        var row = new MessageFeedRow(MessageKind.Danmu) { DataContext = model };
        var host = new Grid { Children = { row } };
        if (compact)
        {
            host.Classes.Add("compactDanmu");
        }
        if (wrap)
        {
            host.Classes.Add("compactDanmuWrap");
        }
        var window = new Window
        {
            Content = host, Width = 960, Height = 220,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        var captain = new SolidColorBrush(Color.Parse("#2563EB"));
        var admiral = new SolidColorBrush(Color.Parse("#A46EE8"));
        var governor = new SolidColorBrush(Color.Parse("#E9A23B"));
        var admin = new SolidColorBrush(Color.Parse("#15803D"));
        window.Resources["GuardCaptainUserNameBrush"] = captain;
        window.Resources["GuardAdmiralUserNameBrush"] = admiral;
        window.Resources["GuardGovernorUserNameBrush"] = governor;
        window.Resources["RoomAdminUserNameBrush"] = admin;
        window.Resources["SpecialAttentionUserNameBrush"] = Brushes.DarkMagenta;
        window.Resources["SpecialAttentionContentBrush"] = Brushes.OrangeRed;
        try
        {
            window.Show();
            Pump(window);
            var name = Assert.Single(row.GetVisualDescendants().OfType<EmoticonTextBlock>(),
                text => text.Classes.Contains("danmuFeedUser") && text.IsEffectivelyVisible);
            var original = FindOriginal(row, compact, wrap);
            var translated = FindTranslated(row, compact, wrap);
            var regularName = name.Foreground;
            var regularWeight = name.FontWeight;
            var originalBrush = original.Foreground;
            var originalWeight = original.FontWeight;
            var translatedBrush = translated.Foreground;
            var translatedWeight = translated.FontWeight;
            var namePinyin = name.PinyinForeground;
            var medal = row.GetVisualDescendants().OfType<TextBlock>().First(text => text.Classes.Contains("danmuMedalText"));
            var medalBrush = medal.Foreground;
            foreach (var (level, expected) in new[] { (3, captain), (2, admiral), (1, governor) })
            {
                record = record with { RoomGuardLevel = level };
                model.Update(record, settings);
                Pump(window);
                Assert.Same(expected, name.Foreground);
                Assert.Equal(regularWeight, name.FontWeight);
                Assert.Same(originalBrush, original.Foreground);
                Assert.Equal(originalWeight, original.FontWeight);
                Assert.Same(translatedBrush, translated.Foreground);
                Assert.Same(namePinyin, name.PinyinForeground);
                Assert.Same(medalBrush, medal.Foreground);
            }

            record = record with { IsRoomAdmin = true };
            model.Update(record, settings);
            Pump(window);
            Assert.Same(admin, name.Foreground);
            Assert.Same(originalBrush, original.Foreground);

            settings.SpecialAttention.Users.Add(new() { UserUid = record.UserUid });
            model.Update(record, settings);
            Pump(window);
            Assert.Same(Brushes.DarkMagenta, name.Foreground);
            Assert.Same(Brushes.OrangeRed, original.Foreground);
            Assert.Same(translatedBrush, translated.Foreground);
            Assert.Equal(translatedWeight, translated.FontWeight);
            settings.SpecialAttention.Users.Clear();
            model.Update(record, settings);
            Pump(window);
            Assert.Same(admin, name.Foreground);
            Assert.Same(originalBrush, original.Foreground);

            record = record with { IsRoomAdmin = false, RoomGuardLevel = 3 };
            model.Update(record, settings);
            Pump(window);
            Assert.Same(captain, name.Foreground);
            window.Resources["GuardCaptainUserNameBrush"] = Brushes.DarkCyan;
            Pump(window);
            Assert.Same(Brushes.DarkCyan, name.Foreground);
            Assert.Same(originalBrush, original.Foreground);
            record = record with { RoomGuardLevel = 0 };
            model.Update(record, settings);
            Pump(window);
            Assert.Same(regularName, name.Foreground);
            Assert.Equal(regularWeight, name.FontWeight);

            row.DataContext = new MessageItemViewModel(record with { IsRoomAdmin = true }, settings, new PinyinService());
            Pump(window);
            Assert.Same(admin, name.Foreground);
            row.DataContext = model;
            Pump(window);
            Assert.Same(regularName, name.Foreground);
            Assert.Same(translatedBrush, translated.Foreground);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>舰队默认名称色与粉丝牌保持同色系，并在深浅主题的消息背景上满足文字对比度。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultsRemainCloseToMedalHuesAndReadable(bool dark)
    {
        var settings = new PanelSettings { ShowUserAvatars = false };
        var rows = new StackPanel { Spacing = 6 };
        var cases = new[]
        {
            (Name: "舰长观众", Level: 3, Admin: false, Medal: Color.Parse("#4F8FDD")),
            (Name: "提督观众", Level: 2, Admin: false, Medal: Color.Parse("#A46EE8")),
            (Name: "总督观众", Level: 1, Admin: false, Medal: Color.Parse("#E9A23B")),
            (Name: "房管观众", Level: 0, Admin: true, Medal: Colors.Transparent)
        };
        foreach (var entry in cases)
        {
            rows.Children.Add(new MessageFeedRow(MessageKind.Danmu)
            {
                DataContext = new MessageItemViewModel(new()
                {
                    UserName = entry.Name, RoomGuardLevel = entry.Level, IsRoomAdmin = entry.Admin,
                    MedalName = "粉丝牌", MedalLevel = 21, MedalGuardLevel = entry.Level,
                    Content = "正文保留普通颜色"
                }, settings, new PinyinService())
            });
        }
        var window = new Window
        {
            Content = rows, Width = 900, Height = 240,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        try
        {
            window.Show();
            Pump(window);
            for (var index = 0; index < cases.Length; index++)
            {
                var name = rows.Children[index].GetVisualDescendants().OfType<EmoticonTextBlock>()
                    .Single(text => text.Classes.Contains("danmuFeedUser") && text.IsEffectivelyVisible);
                var color = Assert.IsAssignableFrom<ISolidColorBrush>(name.Foreground).Color;
                foreach (var background in dark ? new[] { "#1C222D", "#252D3A" } : new[] { "#FFFFFF", "#F2F4F8" })
                {
                    Assert.True(ColorContrast.GetContrastRatio(color, Color.Parse(background)) >= ColorContrast.MINIMUM_TEXT_CONTRAST);
                }
                if (cases[index].Level > 0)
                {
                    var difference = Math.Abs(color.ToHsv().H - cases[index].Medal.ToHsv().H);
                    Assert.InRange(Math.Min(difference, 360 - difference), 0, 4);
                }
            }
            CaptureIfRequested(window, dark ? "user-name-identities-dark.png" : "user-name-identities-light.png");
        }
        finally
        {
            window.Close();
        }
    }

    private static EmoticonTextBlock FindOriginal(MessageFeedRow row, bool compact, bool wrap)
    {
        if (compact && !wrap)
        {
            var marquee = row.GetVisualDescendants().OfType<HoverMarqueeTextBlock>()
                .Single(text => text.Classes.Contains("originalText") && text.IsEffectivelyVisible);
            return Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>());
        }
        return row.GetVisualDescendants().OfType<EmoticonTextBlock>()
            .Single(text => text.Classes.Contains("originalText") && text.IsEffectivelyVisible);
    }

    private static TextBlock FindTranslated(MessageFeedRow row, bool compact, bool wrap)
    {
        if (compact && !wrap)
        {
            var marquee = row.GetVisualDescendants().OfType<HoverMarqueeTextBlock>()
                .Single(text => text.Classes.Contains("translatedText") && text.IsEffectivelyVisible);
            return Assert.Single(marquee.GetVisualDescendants().OfType<EmoticonTextBlock>());
        }
        return Assert.Single(row.GetVisualDescendants().OfType<MessageTranslationTextBlock>(), text => text.IsEffectivelyVisible);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void CaptureIfRequested(Window window, string name)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_USER_NAME_SCREENSHOTS") is not { Length: > 0 } directory)
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
}
