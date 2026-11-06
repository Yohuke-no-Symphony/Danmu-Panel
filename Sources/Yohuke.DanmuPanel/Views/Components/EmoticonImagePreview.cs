using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class EmoticonImagePreview
{
    private const double PREVIEW_SIZE = 160;
    private readonly DispatcherTimer timer = new();
    private Image? target;
    private Control? host;
    private object? previousTip;
    private bool previousServiceEnabled;
    private DanmuEmoticon? emoticon;
    private Image? previewImage;
    private ToolTip? toolTip;
    private Bitmap? bitmap;

    internal bool HasTarget => target is not null;
    internal event Action? TargetChanged;

    internal EmoticonImagePreview()
    {
        timer.Tick += (_, _) => Show();
    }

    internal void UpdateTarget(Image image, DanmuEmoticon value, Control tooltipHost)
    {
        if (ReferenceEquals(target, image) && ReferenceEquals(host, tooltipHost))
        {
            return;
        }
        Clear();
        target = image;
        host = tooltipHost;
        emoticon = value;
        previousTip = ToolTip.GetTip(host);
        previousServiceEnabled = ToolTip.GetServiceEnabled(host);
        host.SetCurrentValue(ToolTip.IsOpenProperty, false);
        host.SetCurrentValue(ToolTip.ServiceEnabledProperty, false);
        host.PropertyChanged += OnHostPropertyChanged;
        TargetChanged?.Invoke();
        var delay = ToolTip.GetShowDelay(image);
        if (delay <= 0)
        {
            Show();
        }
        else
        {
            timer.Interval = TimeSpan.FromMilliseconds(delay);
            timer.Start();
        }
    }

    internal void Clear()
    {
        timer.Stop();
        var previous = target;
        var previousHost = host;
        target = null;
        host = null;
        emoticon = null;
        if (previousHost is not null)
        {
            previousHost.PropertyChanged -= OnHostPropertyChanged;
            previousHost.SetCurrentValue(ToolTip.IsOpenProperty, false);
            if (ReferenceEquals(ToolTip.GetTip(previousHost), toolTip))
            {
                previousHost.SetCurrentValue(ToolTip.TipProperty, previousTip);
            }
            previousHost.SetCurrentValue(ToolTip.ServiceEnabledProperty, previousServiceEnabled);
        }
        previousTip = null;
        if (previewImage is not null)
        {
            previewImage.Source = null;
            previewImage = null;
        }
        if (toolTip is not null)
        {
            toolTip.Content = null;
            toolTip = null;
        }
        bitmap?.Dispose();
        bitmap = null;
        if (previous is not null)
        {
            TargetChanged?.Invoke();
        }
    }

    private void Show()
    {
        timer.Stop();
        if (target is null || !target.IsEffectivelyVisible || target.GetVisualRoot() is null)
        {
            Clear();
            return;
        }
        try
        {
            var source = (Bitmap)target.Source!;
            var scale = TopLevel.GetTopLevel(target)!.RenderScaling;
            var pixels = (int)Math.Ceiling(PREVIEW_SIZE * scale);
            using var stream = File.OpenRead(emoticon!.ImagePath);
            bitmap = source.PixelSize.Width >= source.PixelSize.Height
                ? Bitmap.DecodeToWidth(stream, pixels)
                : Bitmap.DecodeToHeight(stream, pixels);
            var ratio = bitmap.PixelSize.Width / (double)bitmap.PixelSize.Height;
            previewImage = new()
            {
                Source = bitmap,
                Width = ratio >= 1 ? PREVIEW_SIZE : PREVIEW_SIZE * ratio,
                Height = ratio >= 1 ? PREVIEW_SIZE / ratio : PREVIEW_SIZE,
                Stretch = Stretch.Uniform
            };
            AutomationProperties.SetName(previewImage, emoticon.Text);
            toolTip = new() { Content = previewImage, IsHitTestVisible = false };
            host!.SetCurrentValue(ToolTip.TipProperty, toolTip);
            host.SetCurrentValue(ToolTip.IsOpenProperty, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                         ArgumentException or NotSupportedException or NullReferenceException)
        {
            Clear();
            System.Diagnostics.Trace.TraceWarning(Localization.Format("UI.EmoticonTextBlock.ImageDecodeFailed", exception.GetType().Name));
        }
    }

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == ToolTip.IsOpenProperty && !ToolTip.GetIsOpen(host!))
        {
            Clear();
        }
    }
}
