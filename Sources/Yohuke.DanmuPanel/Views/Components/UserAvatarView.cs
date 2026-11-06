using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.Services;

namespace Yohuke.DanmuPanel.Views.Components;

internal sealed class UserAvatarView : UserControl
{
    private const int DECODE_WIDTH = 96;
    private readonly IUserAvatarService? avatars;
    private readonly Ellipse circle = new();
    private readonly LucideIcon placeholder = new() { Kind = LucideIconKind.UserRound };
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMinutes(5) };
    private readonly List<Visual> visibilityAncestors = [];
    private CancellationTokenSource? request;
    private bool isAttached;
    private bool isInViewport;
    private long activeUserUid;
    private int revision;

    public static readonly StyledProperty<long> UserUidProperty =
        AvaloniaProperty.Register<UserAvatarView, long>(nameof(UserUid));
    public static readonly StyledProperty<bool> ShowAvatarProperty =
        AvaloniaProperty.Register<UserAvatarView, bool>(nameof(ShowAvatar), true);

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

    internal UserAvatarView(IUserAvatarService? avatars = null)
    {
        this.avatars = avatars;
        IsHitTestVisible = false;
        VerticalAlignment = VerticalAlignment.Center;
        Classes.Add("messageUserAvatar");
        ResetPlaceholder();
        placeholder.Bind(ForegroundProperty, new DynamicResourceExtension("AppAvatarTextBrush"));
        placeholder.Width = 12;
        placeholder.Height = 12;
        placeholder.HorizontalAlignment = HorizontalAlignment.Center;
        placeholder.VerticalAlignment = VerticalAlignment.Center;
        Content = new Grid { Children = { circle, placeholder } };
        refreshTimer.Tick += (_, _) => RefreshAvatar();
        EffectiveViewportChanged += (_, args) =>
        {
            isInViewport = args.EffectiveViewport.Intersects(new Rect(Bounds.Size));
            UpdateActivity();
        };
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowAvatarProperty)
        {
            IsVisible = ShowAvatar;
            UpdateActivity();
        }
        else if (change.Property == UserUidProperty || change.Property == IsVisibleProperty)
        {
            UpdateActivity();
        }
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
        var nextUserUid = isAttached && isInViewport && IsEffectivelyVisible && ShowAvatar ? UserUid : 0;
        if (nextUserUid <= 0 || avatars is null)
        {
            activeUserUid = 0;
            refreshTimer.Stop();
            CancelRequest();
            ClearImage();
        }
        else if (activeUserUid != nextUserUid)
        {
            activeUserUid = nextUserUid;
            ClearImage();
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
        PendingLoad = LoadAvatarAsync(activeUserUid, revision, request.Token);
    }

    private async Task LoadAvatarAsync(long userUid, int requestRevision, CancellationToken cancellationToken,
        bool retryInvalidImage = true)
    {
        Bitmap? image = null;
        try
        {
            var path = await avatars!.GetAvatarAsync(userUid, cancellationToken).ConfigureAwait(false);
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
                await avatars.InvalidateAvatarAsync(userUid, path, cancellationToken).ConfigureAwait(false);
                if (retryInvalidImage)
                {
                    await LoadAvatarAsync(userUid, requestRevision, cancellationToken, retryInvalidImage: false)
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
                var previous = AvatarImage;
                AvatarImage = image;
                CurrentAvatarPath = path;
                circle.Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                placeholder.IsVisible = false;
                image = null;
                previous?.Dispose();
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

    private static Bitmap? DecodeAvatar(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, DECODE_WIDTH);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException or NullReferenceException)
        {
            return null;
        }
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
