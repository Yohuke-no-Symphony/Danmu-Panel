using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.Input;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views;

/// <summary>向当前阅读房间注入本地测试消息的独立停靠面板。</summary>
public sealed class TestMessagesView : UserControl, IWorkbenchHeaderActions, IDisposable
{
    public TestMessagesViewModel Model { get; }
    public IAsyncRelayCommand AddSingleCommand => Model.AddSingleCommand;
    public IAsyncRelayCommand AddBatchCommand => Model.AddBatchCommand;
    public IAsyncRelayCommand ClearCommand => Model.ClearCommand;
    public IDataTemplate HeaderActionsTemplate { get; }

    /// <summary>创建本地测试面板，不建立任何网络连接。</summary>
    public TestMessagesView(PanelRuntime runtime, Action<Exception> reportError)
    {
        Model = new(runtime, reportError);
        var resources = new TestMessagesResources();
        Resources.MergedDictionaries.Add(resources);
        DataContext = Model;
        HeaderActionsTemplate = (IDataTemplate)resources["HeaderActions"]!;
        Content = ((IDataTemplate)resources["Form"]!).Build(Model);
    }

    /// <summary>切换测试消息目标房间。</summary>
    public void SelectRoom(RoomTabState? selected)
    {
        Model.SelectRoom(selected);
    }

    /// <summary>退出前等待已发起的本地事务完成。</summary>
    public Task FinishPendingAsync()
    {
        return Model.FinishPendingAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Model.Dispose();
    }
}
