using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yohuke.DanmuPanel.ViewModels.Updates;

namespace Yohuke.DanmuPanel.Views.Updates;

internal sealed class AppUpdateWindowCoordinator : IAsyncDisposable
{
    private readonly Window mainWindow;
    private readonly AppUpdateViewModel model;
    private readonly Func<bool> isInteractionBlocked;
    private readonly IDisposable openedSubscription;
    private readonly IDisposable closedSubscription;
    private Window presentationOwner;
    private Task? disposal;
    private bool disposed;
    private bool closingForTransition;

    internal AppUpdateWindowCoordinator(Window mainWindow, AppUpdateViewModel model,
        Func<bool>? isInteractionBlocked = null)
    {
        this.mainWindow = mainWindow;
        this.model = model;
        this.isInteractionBlocked = isInteractionBlocked ?? (() => false);
        presentationOwner = mainWindow;
        model.PropertyChanged += OnModelChanged;
        mainWindow.Closed += OnMainWindowClosed;
        mainWindow.PropertyChanged += OnMainWindowChanged;
        openedSubscription = Window.WindowOpenedEvent.AddClassHandler<Window>(OnWindowTopologyChanged);
        closedSubscription = Window.WindowClosedEvent.AddClassHandler<Window>(OnWindowTopologyChanged);
    }

    internal AppUpdateWindow? CurrentWindow { get; private set; }

    internal Task CheckOnStartupAsync()
    {
        return model.CheckOnStartupAsync();
    }

    internal Task CheckManuallyAsync(Window owner)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed)
        {
            return Task.CompletedTask;
        }
        if (CurrentWindow is { } existing && ReferenceEquals(presentationOwner, owner))
        {
            existing.Activate();
            return model.IsChecking ? model.CheckManuallyAsync() : Task.CompletedTask;
        }
        CloseWindowForTransition();
        if (!ReferenceEquals(presentationOwner, mainWindow))
        {
            presentationOwner.Closed -= OnPresentationOwnerClosed;
            presentationOwner.PropertyChanged -= OnPresentationOwnerChanged;
        }
        presentationOwner = owner;
        if (!ReferenceEquals(owner, mainWindow))
        {
            owner.Closed += OnPresentationOwnerClosed;
            owner.PropertyChanged += OnPresentationOwnerChanged;
        }
        var check = model.CheckManuallyAsync();
        RefreshPresentation();
        return check;
    }

    internal void RefreshPresentation()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (disposed)
        {
            return;
        }
        if (!model.IsDialogOpen || !presentationOwner.IsVisible || !mainWindow.IsEnabled || !presentationOwner.IsEnabled ||
            ReferenceEquals(presentationOwner, mainWindow) && isInteractionBlocked() ||
            !ReferenceEquals(presentationOwner, mainWindow) &&
            presentationOwner.OwnedWindows.Any(window => window.IsVisible && !ReferenceEquals(window, CurrentWindow)))
        {
            CloseWindowForTransition();
            return;
        }
        if (CurrentWindow is { } existing)
        {
            existing.Topmost = presentationOwner.Topmost;
            return;
        }

        var window = new AppUpdateWindow(model) { Topmost = presentationOwner.Topmost };
        CurrentWindow = window;
        window.Closed += OnUpdateWindowClosed;
        window.Show(presentationOwner);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        disposed = true;
        model.PropertyChanged -= OnModelChanged;
        mainWindow.Closed -= OnMainWindowClosed;
        mainWindow.PropertyChanged -= OnMainWindowChanged;
        presentationOwner.Closed -= OnPresentationOwnerClosed;
        presentationOwner.PropertyChanged -= OnPresentationOwnerChanged;
        openedSubscription.Dispose();
        closedSubscription.Dispose();
        CloseWindowForTransition();
        await model.DisposeAsync();
    }

    private void CloseWindowForTransition()
    {
        if (CurrentWindow is not { } window)
        {
            return;
        }
        closingForTransition = true;
        try
        {
            window.Close();
        }
        finally
        {
            closingForTransition = false;
        }
    }

    private void OnUpdateWindowClosed(object? sender, EventArgs args)
    {
        if (sender is AppUpdateWindow window)
        {
            window.Closed -= OnUpdateWindowClosed;
            if (ReferenceEquals(CurrentWindow, window))
            {
                CurrentWindow = null;
                if (!closingForTransition && !disposed)
                {
                    model.Dismiss();
                }
            }
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        RefreshPresentation();
    }

    private void OnMainWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == WindowBase.TopmostProperty || args.Property == InputElement.IsEnabledProperty ||
            args.Property == Visual.IsVisibleProperty)
        {
            RefreshPresentation();
        }
    }

    private void OnMainWindowClosed(object? sender, EventArgs args)
    {
        _ = DisposeAsync();
    }

    private void OnPresentationOwnerClosed(object? sender, EventArgs args)
    {
        presentationOwner.Closed -= OnPresentationOwnerClosed;
        presentationOwner.PropertyChanged -= OnPresentationOwnerChanged;
        model.Dismiss();
        presentationOwner = mainWindow;
    }

    private void OnPresentationOwnerChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == WindowBase.TopmostProperty || args.Property == InputElement.IsEnabledProperty ||
            args.Property == Visual.IsVisibleProperty)
        {
            RefreshPresentation();
        }
    }

    private void OnWindowTopologyChanged(Window window, RoutedEventArgs args)
    {
        if (!disposed && !closingForTransition && !ReferenceEquals(window, CurrentWindow))
        {
            Dispatcher.UIThread.Post(RefreshPresentation);
        }
    }
}
