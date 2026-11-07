using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>以只读文本提供原文、译文的片段选择及全文复制。</summary>
public sealed partial class MessageCopySurface : UserControl
{
    private readonly TabControl sources;
    public MessageCopyViewModel Model { get; }

    internal event EventHandler? CloseRequested;
    internal event Action<string>? CopyRequested;

    private TextBox CurrentText => (TextBox)((TabItem)sources.SelectedItem!).Content!;

    /// <summary>加载只读文本选择和复制操作。</summary>
    public MessageCopySurface()
    {
        Model = new(text => CopyRequested?.Invoke(text), () => CloseRequested?.Invoke(this, EventArgs.Empty));
        AvaloniaXamlLoader.Load(this);
        DataContext = Model;
        sources = this.FindControl<TabControl>("CopySourceTabs")!;
        var originalText = this.FindControl<TextBox>("CopyOriginalText")!;
        var translationText = this.FindControl<TextBox>("CopyTranslationText")!;
        originalText.PropertyChanged += OnTextPropertyChanged;
        translationText.PropertyChanged += OnTextPropertyChanged;
        sources.SelectionChanged += (_, _) => RefreshSelection();

    }

    internal void SetMessage(string original, string translation)
    {
        Model.Original = original;
        Model.Translation = translation;
    }

    internal void FocusOriginal()
    {
        this.FindControl<TextBox>("CopyOriginalText")!.Focus();
    }

    private void OnTextPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty)
        {
            RefreshSelection();
        }
    }

    private void RefreshSelection()
    {
        Model.SelectedText = CurrentText.SelectedText;
    }
}
