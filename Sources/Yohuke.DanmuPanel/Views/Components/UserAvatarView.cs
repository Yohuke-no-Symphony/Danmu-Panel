using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed partial class UserAvatarView : UserControl
{
    private const int DECODE_WIDTH = 96;
    private const double PREVIEW_SIZE = 160;
    private IUserAvatarService? avatars;
    private readonly Ellipse circle;
    private readonly LucideIcon placeholder;
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly DispatcherTimer previewTimer = new();
    private readonly List<Visual> visibilityAncestors = [];
    private CancellationTokenSource? request;
    private CancellationTokenSource? previewRequest;
    private Bitmap? previewBitmap;
    private Image? previewImage;
    private bool isAttached;
    private bool isInViewport;
    private bool suppressPreviewUntilMove;
    private long activeUserUid;
    private int revision;
    private int previewRevision;

    public static readonly StyledProperty<long> UserUidProperty =
        AvaloniaProperty.Register<UserAvatarView, long>(nameof(UserUid));
    public static readonly StyledProperty<bool> ShowAvatarProperty =
        AvaloniaProperty.Register<UserAvatarView, bool>(nameof(ShowAvatar), true);
    public static readonly StyledProperty<IUserAvatarService?> AvatarServiceProperty =
        AvaloniaProperty.Register<UserAvatarView, IUserAvatarService?>(nameof(AvatarService));

    public IUserAvatarService? AvatarService
    {
        get => GetValue(AvatarServiceProperty);
        set => SetValue(AvatarServiceProperty, value);
    }

    public long UserUid
    {
        get => GetValue(UserUidProperty);
        set => SetValue(UserUidProperty, value);
    }

    public bool ShowAvatar
    {
        get => GetValue(ShowAvatarProperty);
        set => SetValue(ShowAvatarProperty, value);
    }

    internal Bitmap? AvatarImage { get; private set; }
    internal string CurrentAvatarPath { get; private set; } = "";
    internal Task PendingLoad { get; private set; } = Task.CompletedTask;
    internal Task PendingPreviewLoad { get; private set; } = Task.CompletedTask;

    public UserAvatarView()
        : this(null)
    {
    }

    internal UserAvatarView(IUserAvatarService? avatars)
    {
        AvaloniaXamlLoader.Load(this);
        circle = this.FindControl<Ellipse>("AvatarCircle")!;
        placeholder = this.FindControl<LucideIcon>("AvatarPlaceholder")!;
        AvatarService = avatars;
        ToolTip.SetServiceEnabled(this, false);
        ResetPlaceholder();
        refreshTimer.Tick += (_, _) => RefreshAvatar();
        previewTimer.Tick += (_, _) => ShowPreview();
        EffectiveViewportChanged += (_, args) =>
        {
            ClearPreview();
            isInViewport = args.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            UpdateActivity();
            SchedulePreview();
        };
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AvatarServiceProperty)
        {
            if (isAttached && avatars is not null)
            {
                avatars.AvatarChanged -= OnAvatarChanged;
            }
            avatars = change.GetNewValue<IUserAvatarService?>();
            if (isAttached)
            {
                CancelRequest();
                activeUserUid = 0;
                ClearImage();
                if (avatars is not null)
                {
                    avatars.AvatarChanged += OnAvatarChanged;
                }
                UpdateActivity();
            }
        }
        else if (change.Property == ShowAvatarProperty)
        {
            IsVisible = ShowAvatar;
            UpdateActivity();
        }
        else if (change.Property == UserUidProperty)
        {
            ClearImage();
            UpdateActivity();
        }
        else if (change.Property == IsVisibleProperty)
        {
            UpdateActivity();
        }
        else if (change.Property == ToolTip.IsOpenProperty && !ToolTip.GetIsOpen(this))
        {
            ClearPreview();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        SchedulePreview();
    }

    /// <inheritdoc />
    protected override void OnPointerExited(PointerEventArgs e)
    {
        ClearPreview();
        base.OnPointerExited(e);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured is not null)
        {
            return;
        }
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed && !properties.IsMiddleButtonPressed &&
            !properties.IsXButton1Pressed && !properties.IsXButton2Pressed)
        {
            suppressPreviewUntilMove = false;
            SchedulePreview();
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        suppressPreviewUntilMove = true;
        ClearPreview();
        base.OnPointerPressed(e);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        suppressPreviewUntilMove = true;
        ClearPreview();
        base.OnPointerWheelChanged(e);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        isAttached = true;
        if (avatars is not null)
        {
            avatars.AvatarChanged += OnAvatarChanged;
        }
        foreach (var ancestor in this.GetVisualAncestors())
        {
            visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
        }
        UpdateActivity();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        isAttached = false;
        isInViewport = false;
        if (avatars is not null)
        {
            avatars.AvatarChanged -= OnAvatarChanged;
        }
        foreach (var ancestor in visibilityAncestors)
        {
            ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        }
        visibilityAncestors.Clear();
        UpdateActivity();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == IsVisibleProperty)
        {
            UpdateActivity();
        }
    }

    private void UpdateActivity()
    {
        var canDisplayAvatar = isAttached && IsEffectivelyVisible && ShowAvatar && UserUid > 0 && avatars is not null;
        var nextUserUid = canDisplayAvatar && isInViewport ? UserUid : 0;
        if (nextUserUid <= 0)
        {
            activeUserUid = 0;
            refreshTimer.Stop();
            CancelRequest();
            if (canDisplayAvatar)
            {
                ClearPreview();
            }
            else
            {
                ClearImage();
            }
        }
        else if (activeUserUid != nextUserUid)
        {
            activeUserUid = nextUserUid;
            refreshTimer.Start();
            RefreshAvatar();
        }
    }

    private void OnAvatarChanged(long userUid)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (activeUserUid == userUid)
            {
                RefreshAvatar();
            }
        });
    }

    private void RefreshAvatar()
    {
        if (activeUserUid <= 0)
        {
            return;
        }
        CancelRequest();
        request = new();
        PendingLoad = LoadAvatarAsync(activeUserUid, revision, request.Token, avatars!);
    }

    private async Task LoadAvatarAsync(long userUid, int requestRevision, CancellationToken cancellationToken, IUserAvatarService source,
        bool retryInvalidImage = true)
    {
        Bitmap? image = null;
        try
        {
            var path = await source.GetAvatarAsync(userUid, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (path.Length == 0)
            {
                return;
            }
            var alreadyLoaded = await Dispatcher.UIThread.InvokeAsync(() =>
                requestRevision != revision || path == CurrentAvatarPath);
            if (alreadyLoaded)
            {
                return;
            }
            image = await Task.Run(() => DecodeAvatar(path), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (image is null)
            {
                await source.InvalidateAvatarAsync(userUid, path, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (retryInvalidImage)
                {
                    await LoadAvatarAsync(userUid, requestRevision, cancellationToken, source, retryInvalidImage: false)
                        .ConfigureAwait(false);
                }
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (requestRevision != revision || activeUserUid != userUid || cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                ClearPreview();
                var previous = AvatarImage;
                AvatarImage = image;
                CurrentAvatarPath = path;
                circle.Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                placeholder.IsVisible = false;
                image = null;
                previous?.Dispose();
                SchedulePreview();
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }
        finally
        {
            image?.Dispose();
        }
    }

    private static Bitmap? DecodeAvatar(string path, int decodeWidth = DECODE_WIDTH)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, decodeWidth);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or NullReferenceException)
        {
            return null;
        }
    }

    private void SchedulePreview()
    {
        if (!IsPointerOver || suppressPreviewUntilMove || activeUserUid <= 0 || AvatarImage is null ||
            previewTimer.IsEnabled || previewRequest is not null)
        {
            return;
        }
        var delay = ToolTip.GetShowDelay(this);
        if (delay <= 0)
        {
            ShowPreview();
        }
        else
        {
            previewTimer.Interval = TimeSpan.FromMilliseconds(delay);
            previewTimer.Start();
        }
    }

    private void ShowPreview()
    {
        previewTimer.Stop();
        if (!isAttached || !isInViewport || !IsEffectivelyVisible || !IsPointerOver || AvatarImage is null)
        {
            return;
        }
        var pixels = (int)Math.Ceiling(PREVIEW_SIZE * TopLevel.GetTopLevel(this)!.RenderScaling);
        var size = AvatarImage.PixelSize;
        var width = size.Width >= size.Height ? pixels : (int)Math.Ceiling(pixels * size.Width / (double)size.Height);
        previewRequest = new();
        PendingPreviewLoad = LoadPreviewAsync(CurrentAvatarPath, width, previewRevision, previewRequest.Token);
    }

    private async Task LoadPreviewAsync(string path, int width, int requestRevision, CancellationToken cancellationToken)
    {
        Bitmap? image = null;
        try
        {
            image = await Task.Run(() => DecodeAvatar(path, width), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (image is null)
            {
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (requestRevision != previewRevision || cancellationToken.IsCancellationRequested ||
                    !isAttached || !isInViewport || !IsEffectivelyVisible || !IsPointerOver || CurrentAvatarPath != path)
                {
                    return;
                }
                var ratio = image.PixelSize.Width / (double)image.PixelSize.Height;
                previewImage = new()
                {
                    Source = image,
                    Width = ratio >= 1 ? PREVIEW_SIZE : PREVIEW_SIZE * ratio,
                    Height = ratio >= 1 ? PREVIEW_SIZE / ratio : PREVIEW_SIZE,
                    Stretch = Stretch.Uniform
                };
                previewBitmap = image;
                image = null;
                ToolTip.SetTip(this, new ToolTip { Content = previewImage, IsHitTestVisible = false });
                ToolTip.SetIsOpen(this, true);
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            image?.Dispose();
        }
    }

    private void ClearPreview()
    {
        if (!previewTimer.IsEnabled && previewRequest is null && previewImage is null)
        {
            return;
        }
        previewTimer.Stop();
        previewRevision++;
        previewRequest?.Cancel();
        previewRequest?.Dispose();
        previewRequest = null;
        var previousImage = previewImage;
        var previousBitmap = previewBitmap;
        previewImage = null;
        previewBitmap = null;
        ToolTip.SetIsOpen(this, false);
        if (previousImage is not null)
        {
            previousImage.Source = null;
        }
        if (ToolTip.GetTip(this) is ToolTip toolTip)
        {
            toolTip.Content = null;
        }
        ToolTip.SetTip(this, null);
        previousBitmap?.Dispose();
    }

    private void CancelRequest()
    {
        revision++;
        request?.Cancel();
        request?.Dispose();
        request = null;
    }

    private void ClearImage()
    {
        ClearPreview();
        var previous = AvatarImage;
        AvatarImage = null;
        CurrentAvatarPath = "";
        ResetPlaceholder();
        previous?.Dispose();
    }

    private void ResetPlaceholder()
    {
        circle.Bind(Shape.FillProperty, new DynamicResourceExtension("AppAvatarBrush"));
        placeholder.IsVisible = true;
    }
}
