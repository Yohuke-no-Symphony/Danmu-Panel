using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>为消息面板的多个标题宿主共享状态和异步操作。</summary>
public sealed class MessageFeedHeaderViewModel : ObservableObject
{
    private readonly Action<Exception> reportError;

    public FeedViewModel Feed { get; }
    public double BadgeFontSize => 13 * Feed.MessageTextScale;
    public IAsyncRelayCommand ClearCommand { get; }
    public IAsyncRelayCommand BackToLatestCommand { get; }
    public IAsyncRelayCommand BackToTopCommand { get; }
    public IAsyncRelayCommand MarkAllReadCommand { get; }
    public IAsyncRelayCommand LoadMoreCommand { get; }

    /// <summary>连接消息数据状态与由视图提供的导航和确认操作。</summary>
    public MessageFeedHeaderViewModel(FeedViewModel feed, Func<Task> clear, Func<Task> latest,
        Func<Task> top, Action<Exception> reportError)
    {
        Feed = feed;
        this.reportError = reportError;
        ClearCommand = CreateCommand(clear);
        BackToLatestCommand = CreateCommand(latest);
        BackToTopCommand = CreateCommand(top);
        MarkAllReadCommand = CreateCommand(feed.MarkAllReadAsync);
        LoadMoreCommand = CreateCommand(feed.LoadMoreAsync);
        feed.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FeedViewModel.MessageTextScale))
            {
                OnPropertyChanged(nameof(BadgeFontSize));
            }
        };
    }

    private IAsyncRelayCommand CreateCommand(Func<Task> action)
    {
        return new AsyncRelayCommand(async () =>
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                reportError(exception);
            }
        });
    }
}
