using Avalonia.Controls;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.ViewModels.Settings;

namespace Yohuke.DanmuPanel.Views.Settings;

internal sealed class DanmuRegexEditor : UserControl
{
    internal DanmuRegexEditorViewModel Model { get; }

    internal DanmuRegexEditor(DanmuRegexFilterSettings initial)
    {
        Name = "DanmuRegexEditor";
        Model = new(initial);
        Content = new DanmuRegexEditorSurface { DataContext = Model };
    }
}
