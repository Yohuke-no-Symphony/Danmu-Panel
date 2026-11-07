using Avalonia.Controls;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Windowing;

internal static class EmoticonImageTestHelper
{
    internal static async Task LoadAsync(Window window)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            PinyinTextRenderingTests.Pump(window);
            var tasks = window.GetVisualDescendants().OfType<EmoticonTextBlock>()
                .SelectMany(text => new[] { text.PendingImageLoad, text.PendingPreviewLoad }).Distinct().ToArray();
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            PinyinTextRenderingTests.Pump(window);
            if (window.GetVisualDescendants().OfType<EmoticonTextBlock>()
                .All(text => text.PendingImageLoad.IsCompleted && text.PendingPreviewLoad.IsCompleted))
            {
                return;
            }
        }
        throw new InvalidOperationException("Emoticon image loading did not settle.");
    }
}
