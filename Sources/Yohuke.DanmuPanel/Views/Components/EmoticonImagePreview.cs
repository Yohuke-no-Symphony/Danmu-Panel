using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private readonly EmoticonBitmapCache bitmapCache = new(0, 0);
    private EmoticonBitmapLease? bitmapLease;
    private CancellationTokenSource? request;
    private int revision;

    internal bool HasTarget => target is not null;
    internal event Action? TargetChanged;
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;

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
        revision++;
        request?.Cancel();
        request?.Dispose();
        request = null;
        bitmapCache.Clear();
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
        bitmapLease?.Dispose();
        bitmapLease = null;
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
        var source = (Bitmap)target.Source!;
        var scale = TopLevel.GetTopLevel(target)!.RenderScaling;
        var pixels = (int)Math.Ceiling(PREVIEW_SIZE * scale);
        var height = source.PixelSize.Width >= source.PixelSize.Height
            ? (int)Math.Ceiling(pixels * source.PixelSize.Height / (double)source.PixelSize.Width) : pixels;
        request = new();
        PendingLoad = ShowAsync(emoticon!.ImagePath, height, revision, request.Token);
    }

    private async Task ShowAsync(string path, int height, int requestRevision, CancellationToken cancellationToken)
    {
        EmoticonBitmapLease? loaded = null;
        try
        {
            loaded = await bitmapCache.AcquireAsync(path, height, cancellationToken);
            if (cancellationToken.IsCancellationRequested || requestRevision != revision)
            {
                return;
            }
            if (loaded is null || target is null || !target.IsEffectivelyVisible || target.GetVisualRoot() is null)
            {
                Clear();
                return;
            }
            bitmapLease = loaded;
            loaded = null;
            var bitmap = bitmapLease.Image;
            var ratio = bitmap.PixelSize.Width / (double)bitmap.PixelSize.Height;
            previewImage = new()
            {
                Source = bitmap,
                Width = ratio >= 1 ? PREVIEW_SIZE : PREVIEW_SIZE * ratio,
                Height = ratio >= 1 ? PREVIEW_SIZE / ratio : PREVIEW_SIZE,
                Stretch = Stretch.Uniform
            };
            AutomationProperties.SetName(previewImage, emoticon!.Text);
            toolTip = new() { Content = previewImage, IsHitTestVisible = false };
            host!.SetCurrentValue(ToolTip.TipProperty, toolTip);
            host.SetCurrentValue(ToolTip.IsOpenProperty, true);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            loaded?.Dispose();
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
