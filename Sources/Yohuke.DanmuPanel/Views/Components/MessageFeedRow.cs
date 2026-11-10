using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Views.Components;

[PseudoClasses(":alternate", ":specialAttention", ":guardMedal", ":captain", ":admiral", ":governor", ":roomAdmin", ":pinged")]
internal sealed class MessageFeedRow : Button
{
    private static readonly StyledProperty<bool> IsSpecialAttentionProperty =
        AvaloniaProperty.Register<MessageFeedRow, bool>(nameof(MessageItemViewModel.IsSpecialAttention));
    private static readonly StyledProperty<bool> IsPingedProperty =
        AvaloniaProperty.Register<MessageFeedRow, bool>(nameof(MessageItemViewModel.IsPinged));
    private static readonly StyledProperty<bool> HasGuardMedalProperty =
        AvaloniaProperty.Register<MessageFeedRow, bool>(nameof(MessageItemViewModel.HasGuardMedal));
    private static readonly StyledProperty<MessageUserNameRole> UserNameRoleProperty =
        AvaloniaProperty.Register<MessageFeedRow, MessageUserNameRole>(nameof(MessageItemViewModel.UserNameRole));
    private static MessageFeedRowTemplates Templates { get; } = new();
    private readonly bool canCopy;
    private bool isCopyHoldEligible;
    private bool suppressClick;
    private Point copyPressPosition;
    private IPointer? copyPointer;

    internal event EventHandler? CopyRequested;

    /// <inheritdoc/>
    protected override Type StyleKeyOverride => typeof(Button);

    internal void UpdateAlternatingBackground(int index)
    {
        PseudoClasses.Set(":alternate", (index & 1) != 0);
    }

    internal MessageFeedRow(MessageKind kind, IUserAvatarService? avatars = null, EmoticonBitmapCache? bitmapCache = null)
    {
        this.Bind(IsSpecialAttentionProperty, new Binding(nameof(MessageItemViewModel.IsSpecialAttention)));
        this.Bind(IsPingedProperty, new Binding(nameof(MessageItemViewModel.IsPinged)));
        if (kind == MessageKind.Danmu)
        {
            this.Bind(HasGuardMedalProperty, new Binding(nameof(MessageItemViewModel.HasGuardMedal)));
            this.Bind(UserNameRoleProperty, new Binding(nameof(MessageItemViewModel.UserNameRole)));
        }
        canCopy = kind is MessageKind.Danmu or MessageKind.SuperChat;
        Gestures.SetIsHoldingEnabled(this, canCopy);
        Gestures.SetIsHoldWithMouseEnabled(this, canCopy);
        Holding += OnHolding;
        var content = Templates.Build(kind);
        foreach (var avatar in content.GetVisualDescendants().OfType<UserAvatarView>())
        {
            avatar.AvatarService = avatars;
        }
        foreach (var body in content.GetVisualDescendants().OfType<MessageBodyPanel>())
        {
            body.SetServices(avatars, bitmapCache);
        }
        Content = content;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Classes.Add(kind == MessageKind.SuperChat ? "scFeedButton" : "danmuFeedButton");
        if (kind is MessageKind.SuperChat or MessageKind.Gift or MessageKind.Guard)
        {
            RenderTransform = new TranslateTransform();
            Classes.Add("messageCardButton");
        }
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        suppressClick = false;
        copyPressPosition = e.GetPosition(this);
        isCopyHoldEligible = canCopy && DataContext is not MessageItemViewModel { IsEventSummary: true } &&
            e.GetCurrentPoint(this).Properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed;
        copyPointer = isCopyHoldEligible ? e.Pointer : null;
        base.OnPointerPressed(e);
        if (isCopyHoldEligible && e.Pointer.Type == PointerType.Mouse)
        {
            e.Pointer.Capture(this);
        }
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        ReleaseCopyPointer();
        isCopyHoldEligible = false;
        suppressClick = false;
    }

    /// <inheritdoc />
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        isCopyHoldEligible = false;
        copyPointer = null;
        base.OnPointerCaptureLost(e);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        suppressClick |= copyPointer is not null;
        isCopyHoldEligible = false;
        base.OnPointerWheelChanged(e);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (isCopyHoldEligible)
        {
            var delta = e.GetPosition(this) - copyPressPosition;
            var tolerance = TopLevel.GetTopLevel(this)!.PlatformSettings!.GetTapSize(e.Pointer.Type);
            if (Math.Abs(delta.X) > tolerance.Width || Math.Abs(delta.Y) > tolerance.Height)
            {
                isCopyHoldEligible = false;
                suppressClick = true;
            }
        }
        base.OnPointerMoved(e);
    }

    /// <inheritdoc />
    protected override void OnClick()
    {
        if (!suppressClick)
        {
            base.OnClick();
        }
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        CancelCopyInteraction();
        PseudoClasses.Set(":pinged", false);
        base.OnDataContextChanged(e);
        UpdatePingState();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsSpecialAttentionProperty)
        {
            PseudoClasses.Set(":specialAttention", GetValue(IsSpecialAttentionProperty));
        }
        if (change.Property == IsPingedProperty || change.Property == IsVisibleProperty)
        {
            UpdatePingState();
        }
        if (change.Property == HasGuardMedalProperty)
        {
            PseudoClasses.Set(":guardMedal", GetValue(HasGuardMedalProperty));
        }
        if (change.Property == UserNameRoleProperty)
        {
            var role = GetValue(UserNameRoleProperty);
            PseudoClasses.Set(":captain", role == MessageUserNameRole.Captain);
            PseudoClasses.Set(":admiral", role == MessageUserNameRole.Admiral);
            PseudoClasses.Set(":governor", role == MessageUserNameRole.Governor);
            PseudoClasses.Set(":roomAdmin", role == MessageUserNameRole.RoomAdmin);
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdatePingState();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelCopyInteraction();
        PseudoClasses.Set(":pinged", false);
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdatePingState()
    {
        PseudoClasses.Set(":pinged", this.GetVisualRoot() is not null && IsEffectivelyVisible &&
            DataContext is MessageItemViewModel { IsPinged: true });
    }

    private void OnHolding(object? sender, HoldingRoutedEventArgs e)
    {
        if (e.HoldingState == HoldingState.Started && isCopyHoldEligible &&
            DataContext is not MessageItemViewModel { IsEventSummary: true })
        {
            suppressClick = true;
            e.Handled = true;
            CopyRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void CancelCopyInteraction()
    {
        isCopyHoldEligible = false;
        suppressClick |= copyPointer is not null;
        ReleaseCopyPointer();
    }

    private void ReleaseCopyPointer()
    {
        var pointer = copyPointer;
        copyPointer = null;
        if (ReferenceEquals(pointer?.Captured, this))
        {
            pointer!.Capture(null);
        }
    }

}
