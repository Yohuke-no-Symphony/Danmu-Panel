using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Views;

/// <summary>显示当前启动阶段，并由应用处理关闭请求。</summary>
public sealed partial class StartupWindow : Window
{
    internal StartupViewModel Model { get; } = new();

    /// <summary>创建居中显示的启动进度窗口。</summary>
    public StartupWindow()
    {
        InitializeComponent();
        DataContext = Model;
    }

    internal void ShowStage(StartupStage stage)
    {
        Dispatcher.UIThread.VerifyAccess();
        Model.ShowStage(stage);
    }

    /// <inheritdoc />
    protected override void OnOpened(EventArgs e)
    {
        Localization.LanguageChanged += OnLanguageChanged;
        Model.RefreshLanguage();
        base.OnOpened(e);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        Localization.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Model.RefreshLanguage();
    }

    private void OnDragSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
        {
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnCancelClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
