using System.Globalization;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Tests.Translation;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>验证后台消息投影使用已捕获的本地化设置，在 UI 应用时无需计算拼音。</summary>
public sealed class MessageRowProjectionTests
{
    /// <summary>全局语言和数值文化变化期间，后台生成保留提交给它的快照，刷新可原位换成新语言。</summary>
    [AvaloniaFact]
    public async Task BackgroundProjectionUsesCapturedCultureAndStrings()
    {
        var originalLanguage = Localization.SelectedLanguageID;
        var originalCulture = CultureInfo.CurrentCulture;
        var settings = new PanelSettings { ShowUserPinyin = true, ShowGiftPinyin = true };
        var gift = new MessageRecord { Kind = MessageKind.Gift, UserName = "观众", GiftName = "星光", Quantity = 2, TotalPrice = 12.5m };
        var pinyin = new DelegatePinyinService(_ => throw new InvalidOperationException("Deferred rows do not calculate pinyin."),
            _ => throw new InvalidOperationException("Deferred rows do not calculate annotations."));
        try
        {
            Localization.SetLanguage("zh-CN");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            var originalSummary = Localization.Format("Panel.EventSummary.Gift", gift.GiftName, gift.Quantity);
            var captured = MessageDisplaySettings.Capture(settings);

            Localization.SetLanguage("en-US");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var currentSummary = Localization.Format("Panel.EventSummary.Gift", gift.GiftName, gift.Quantity);
            var current = MessageDisplaySettings.Capture(settings, captured);
            var projection = await Task.Run(() =>
            {
                Assert.False(Dispatcher.UIThread.CheckAccess());
                return MessageRowProjection.Create(gift, captured, isTranslationPending: false, isEventSummary: true);
            });
            var row = MessageItemViewModel.CreateDeferred(projection, pinyin);

            Assert.NotEqual(originalSummary, currentSummary);
            Assert.Equal(originalSummary, row.SummaryLabel);
            Assert.Equal("￥12,5", row.SummaryValue);
            Assert.Equal("星光 ×2 · ￥12,5", row.Detail);
            Assert.True(row.IsNamePinyinPending);
            Assert.True(row.IsContentPinyinPending);

            var updated = await Task.Run(() => MessageRowProjection.Create(gift, current,
                isTranslationPending: false, isEventSummary: true));
            row.Apply(updated);

            Assert.Same(gift, row.Record);
            Assert.Equal(currentSummary, row.SummaryLabel);
            Assert.Equal("￥12.5", row.SummaryValue);
            Assert.Equal("星光 ×2 · ￥12.5", row.Detail);
            Assert.True(row.IsNamePinyinPending);
            Assert.True(row.IsContentPinyinPending);
        }
        finally
        {
            Localization.SetLanguage(originalLanguage);
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
