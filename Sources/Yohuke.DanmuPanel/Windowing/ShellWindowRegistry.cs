using Avalonia;
using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Windowing;

internal sealed class ShellWindowRegistry : IDisposable
{
    private readonly Window owner;
    private readonly HashSet<Window> windows = [];
    private int modalDepth;
    private bool shutdownPending;

    internal ShellWindowRegistry(Window owner)
    {
        this.owner = owner;
        owner.PropertyChanged += OnOwnerPropertyChanged;
    }

    internal event Action? InteractionChanged;
    internal bool IsInteractionBlocked => shutdownPending || modalDepth > 0;

    internal void Register(Window window)
    {
        if (windows.Add(window))
        {
            window.PropertyChanged += OnWindowPropertyChanged;
            Refresh();
        }
    }

    internal void Unregister(Window window)
    {
        if (windows.Remove(window))
        {
            window.PropertyChanged -= OnWindowPropertyChanged;
        }
    }

    internal void SetShutdownPending(bool pending)
    {
        shutdownPending = pending;
        Refresh();
        InteractionChanged?.Invoke();
    }

    internal async Task<T> ShowDialogAsync<T>(Window dialog)
    {
        modalDepth++;
        Refresh();
        InteractionChanged?.Invoke();
        dialog.Topmost = owner.Topmost;
        try
        {
            return await dialog.ShowDialog<T>(owner);
        }
        finally
        {
            modalDepth--;
            Refresh();
            InteractionChanged?.Invoke();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        owner.PropertyChanged -= OnOwnerPropertyChanged;
        foreach (var window in windows.ToArray())
        {
            Unregister(window);
        }
    }

    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowBase.TopmostProperty)
        {
            Refresh();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WorkbenchWindowTopmost.IsIndependentlyTopmostProperty)
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        foreach (var window in windows)
        {
            window.Topmost = owner.Topmost || WorkbenchWindowTopmost.GetIsIndependentlyTopmost(window);
            window.IsEnabled = !shutdownPending && modalDepth == 0;
        }
    }
}
