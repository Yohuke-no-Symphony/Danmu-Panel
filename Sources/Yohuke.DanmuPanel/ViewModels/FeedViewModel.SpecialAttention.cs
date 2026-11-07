using Avalonia.Threading;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

public sealed partial class FeedViewModel
{
    private void OnSpecialAttentionChanged()
    {
        if (disposed || kind != MessageKind.Danmu)
        {
            return;
        }
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnSpecialAttentionChanged);
            return;
        }
        var current = MessageDisplaySettings.Capture(runtime.Configuration.Settings, displaySettings, runtime.DanmuFilter);
        if (ReferenceEquals(current, displaySettings))
        {
            return;
        }
        displaySettings = current;
        BeginViewportUpdate();
        try
        {
            foreach (var item in Items)
            {
                item.Apply(item.Record, current, item.TranslationPending);
            }
        }
        finally
        {
            EndViewportUpdate();
        }
    }
}
