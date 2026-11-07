using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Yohuke.DanmuPanel.ViewModels;

/// <summary>管理复制弹层的来源、全文和选区操作。</summary>
public sealed partial class MessageCopyViewModel : ObservableObject
{
    [ObservableProperty] private string original = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTranslation))]
    private string translation = "";
    [ObservableProperty] private int selectedSourceIndex;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySelectionCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private string selectedText = "";
    public bool HasSelection => SelectedText.Length > 0;
    public bool HasTranslation => Translation.Length > 0;
    public IRelayCommand CopySelectionCommand { get; }
    public IRelayCommand CopyAllCommand { get; }
    public IRelayCommand CloseCommand { get; }

    /// <summary>连接剪贴板请求和弹层关闭。</summary>
    public MessageCopyViewModel(Action<string> copy, Action close)
    {
        CopySelectionCommand = new RelayCommand(() => copy(SelectedText), () => SelectedText.Length > 0);
        CopyAllCommand = new RelayCommand(() => copy(SelectedSourceIndex == 0 ? Original : Translation));
        CloseCommand = new RelayCommand(close);
    }
}
