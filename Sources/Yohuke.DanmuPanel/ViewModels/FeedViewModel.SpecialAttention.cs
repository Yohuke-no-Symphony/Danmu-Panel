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
        Interlocked.Increment(ref projectionOptionsRevision);
        var followed = runtime.Configuration.Settings.SpecialAttention.Users.Select(user => user.UserUid).ToHashSet();
        if (!Items.Any(item => item.Record.Kind == MessageKind.Danmu && item.IsSpecialAttention != followed.Contains(item.UserUid)))
        {
            return;
        }
        BeginViewportUpdate();
        try
        {
            foreach (var item in Items)
            {
                item.UpdateSpecialAttention(followed.Contains(item.UserUid));
            }
        }
        finally
        {
            EndViewportUpdate();
        }
    }
}
