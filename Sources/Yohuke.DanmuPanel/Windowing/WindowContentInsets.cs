using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Yohuke.DanmuPanel.Windowing;

internal sealed class WindowContentInsets : IDisposable
{
    private readonly Control contentRoot;
    private readonly Thickness originalMargin;
    private bool disposed;

    internal WindowContentInsets(Control contentRoot)
    {
        this.contentRoot = contentRoot;
        originalMargin = contentRoot.Margin;
    }

    internal Thickness Insets { get; private set; }

    internal void Update(Thickness insets)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Insets == insets)
        {
            return;
        }

        Insets = insets;
        contentRoot.SetCurrentValue(Layoutable.MarginProperty, originalMargin + Insets);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (contentRoot.Margin == originalMargin + Insets)
        {
            contentRoot.SetCurrentValue(Layoutable.MarginProperty, originalMargin);
        }
    }
}
