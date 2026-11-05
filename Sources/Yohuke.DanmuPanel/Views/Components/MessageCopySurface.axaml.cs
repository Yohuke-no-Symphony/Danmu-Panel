using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Yohuke.DanmuPanel.Views.Components;

/// <summary>以只读文本提供原文、译文的片段选择及全文复制。</summary>
public sealed partial class MessageCopySurface : UserControl
{
    private readonly TabControl sources;
    private readonly Button copySelected;

    internal event EventHandler? CloseRequested;
    internal event Action<string>? CopyRequested;

    private TextBox CurrentText => (TextBox)((TabItem)sources.SelectedItem!).Content!;

    /// <summary>加载只读文本选择和复制操作。</summary>
    public MessageCopySurface()
    {
        AvaloniaXamlLoader.Load(this);
        sources = this.FindControl<TabControl>("CopySourceTabs")!;
        copySelected = this.FindControl<Button>("CopySelectedTextButton")!;
        var originalText = this.FindControl<TextBox>("CopyOriginalText")!;
        var translationText = this.FindControl<TextBox>("CopyTranslationText")!;
        sources.SelectedIndex = 0;
        originalText.PropertyChanged += OnTextPropertyChanged;
        translationText.PropertyChanged += OnTextPropertyChanged;
        sources.SelectionChanged += (_, _) => RefreshSelection();
        copySelected.Click += (_, _) => CopyRequested?.Invoke(CurrentText.SelectedText);
        this.FindControl<Button>("CopyAllTextButton")!.Click += (_, _) => CopyRequested?.Invoke(CurrentText.Text!);
        this.FindControl<Button>("CloseCopyPopupButton")!.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    internal void SetMessage(string original, string translation)
    {
        this.FindControl<TextBox>("CopyOriginalText")!.Text = original;
        this.FindControl<TextBox>("CopyTranslationText")!.Text = translation;
        this.FindControl<TabItem>("CopyTranslationTab")!.IsVisible = translation.Length > 0;
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
        copySelected.IsEnabled = CurrentText.SelectionStart != CurrentText.SelectionEnd;
    }
}
