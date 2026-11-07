using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>在菜单打开及消息行回收时解析当前操作目标。</summary>
public sealed class MessageFeedMenuViewModel : ObservableObject
{
    private readonly MessageKind kind;
    private readonly Func<MessageItemViewModel?> getItem;
    private readonly Func<MessageItemViewModel, bool> canRetry;
    private readonly Func<MessageItemViewModel, bool> isFollowed;
    private readonly Action<Exception> reportError;
    private string activationHeader = "";
    private string attentionHeader = "";
    private LucideIconKind activationIcon;

    public MessageFeedHeaderViewModel Header { get; }
    public string ActivationHeader
    {
        get => activationHeader;
        private set => SetProperty(ref activationHeader, value);
    }

    public string AttentionHeader
    {
        get => attentionHeader;
        private set => SetProperty(ref attentionHeader, value);
    }

    public LucideIconKind ActivationIcon
    {
        get => activationIcon;
        private set => SetProperty(ref activationIcon, value);
    }

    public IAsyncRelayCommand ActivateCommand { get; }
    public IAsyncRelayCommand CopyCommand { get; }
    public IAsyncRelayCommand RetryCommand { get; }
    public IAsyncRelayCommand ToggleAttentionCommand { get; }
    public bool CanActivate => getItem() is { } item && (kind == MessageKind.Danmu || !item.Record.IsRead);
    public bool CanRetryTranslation => getItem() is { } item && canRetry(item);
    public bool CanToggleAttention => getItem() is { UserUid: > 0 };

    /// <summary>连接当前行解析器与宿主操作，不保存回收前的消息实例。</summary>
    public MessageFeedMenuViewModel(MessageKind kind, MessageFeedHeaderViewModel header,
        Func<MessageItemViewModel?> getItem, Func<MessageItemViewModel, Task> activate,
        Func<MessageItemViewModel, Task> copy, Func<MessageItemViewModel, Task> retry,
        Func<MessageItemViewModel, Task> toggleAttention, Func<MessageItemViewModel, bool> canRetry,
        Func<MessageItemViewModel, bool> isFollowed, Action<Exception> reportError)
    {
        this.kind = kind;
        Header = header;
        this.getItem = getItem;
        this.canRetry = canRetry;
        this.isFollowed = isFollowed;
        this.reportError = reportError;
        ActivateCommand = CreateCommand(activate, item => kind == MessageKind.Danmu || !item.Record.IsRead);
        CopyCommand = CreateCommand(copy, _ => true);
        RetryCommand = CreateCommand(retry, canRetry);
        ToggleAttentionCommand = CreateCommand(toggleAttention, item => item.UserUid > 0);
        Refresh();
    }

    /// <summary>刷新随目标、阅读状态或语言变化的菜单标题和启用状态。</summary>
    public void Refresh()
    {
        var item = getItem();
        var pinned = item?.Record.IsPinned == true;
        ActivationHeader = Localization.Get(kind == MessageKind.Danmu
            ? pinned ? "Panel.UnpinMessage" : "Panel.PinMessage"
            : "Panel.MarkRead");
        ActivationIcon = kind == MessageKind.Danmu
            ? pinned ? LucideIconKind.PinOff : LucideIconKind.Pin
            : LucideIconKind.Check;
        AttentionHeader = Localization.Get(item is not null && isFollowed(item)
            ? "SpecialAttention.Remove" : "SpecialAttention.Add");
        OnPropertyChanged(nameof(CanActivate));
        OnPropertyChanged(nameof(CanRetryTranslation));
        OnPropertyChanged(nameof(CanToggleAttention));
        ActivateCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        ToggleAttentionCommand.NotifyCanExecuteChanged();
    }

    private IAsyncRelayCommand CreateCommand(Func<MessageItemViewModel, Task> action,
        Func<MessageItemViewModel, bool> canExecute)
    {
        return new AsyncRelayCommand(async () =>
        {
            if (getItem() is not { } item || !canExecute(item))
            {
                return;
            }
            try
            {
                await action(item);
            }
            catch (Exception exception)
            {
                reportError(exception);
            }
            finally
            {
                Refresh();
            }
        }, () => getItem() is { } item && canExecute(item));
    }
}
