using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Testing;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Views;

/// <summary>向当前阅读房间注入本地测试消息的独立停靠面板。</summary>
public sealed class TestMessagesView : UserControl, IWorkbenchHeaderActions, IDisposable
{
    private readonly TestMessageService service;
    private readonly Action<Exception> reportError;
    private readonly TextBlock target = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly ComboBox kind;
    private readonly TextBox userName;
    private readonly TextBox messageContent;
    private readonly TextBox giftName;
    private readonly TextBox translation;
    private readonly NumericUpDown price = new() { Name = "TestUnitPrice", Minimum = 0, Maximum = 1000000, Value = 30, FormatString = "0.##" };
    private readonly NumericUpDown quantity = new() { Name = "TestQuantity", Minimum = 1, Maximum = 1000000, Value = 1, FormatString = "0" };
    private readonly NumericUpDown batchCount = new() { Name = "TestBatchCount", Minimum = 1, Maximum = TestMessageRequest.MAX_BATCH_COUNT, Value = 100, FormatString = "0" };
    private readonly ComboBox guardLevel;
    private readonly Control textFields;
    private readonly Control giftField;
    private readonly Control priceField;
    private readonly Control quantityField;
    private readonly Control guardField;
    private RoomTabState? room;
    private bool busy;
    private bool stopping;
    private string? statusKey;
    private int statusCount;
    private string statusRoom = "";

    /// <summary>添加一条当前类型的测试消息。</summary>
    public IAsyncRelayCommand AddSingleCommand { get; }

    /// <summary>按指定数量批量添加当前类型的测试消息。</summary>
    public IAsyncRelayCommand AddBatchCommand { get; }

    /// <summary>仅删除当前房间测试消息。</summary>
    public IAsyncRelayCommand ClearCommand { get; }

    /// <summary>为每个停靠宿主独立创建紧凑图标操作。</summary>
    public IDataTemplate HeaderActionsTemplate { get; }

    /// <summary>创建本地测试面板，不建立任何网络连接。</summary>
    public TestMessagesView(PanelRuntime runtime, Action<Exception> reportError)
    {
        service = runtime.TestMessages;
        this.reportError = reportError;
        AddSingleCommand = new AsyncRelayCommand(() => AddAsync(1), CanExecute);
        AddBatchCommand = new AsyncRelayCommand(() => AddAsync(decimal.ToInt32(batchCount.Value ?? 1)), CanExecute);
        ClearCommand = new AsyncRelayCommand(ClearAsync, CanExecute);
        HeaderActionsTemplate = new FuncDataTemplate<TestMessagesView>((_, _) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            Children =
            {
                HeaderButton("TestMessages.AddSingle", LucideIconKind.Plus, AddSingleCommand),
                HeaderButton("TestMessages.AddBatch", LucideIconKind.ListPlus, AddBatchCommand),
                HeaderButton("TestMessages.Clear", LucideIconKind.Trash2, ClearCommand)
            }
        });
        kind = Choices("TestKind", ["Workbench.Panel.Danmu", "Workbench.Panel.SuperChat", "Workbench.Panel.Gift", "Workbench.Panel.Guard"], 0);
        guardLevel = Choices("TestGuardLevel", ["Panel.Guard.1", "Panel.Guard.2", "Panel.Guard.3"], 2);
        userName = new() { Name = "TestUserName", Text = Localization.Get("TestMessages.SampleUser") };
        messageContent = new()
        {
            Name = "TestMessageContent", Text = Localization.Get("TestMessages.SampleContent"),
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 74, MaxHeight = 160
        };
        giftName = new() { Name = "TestGiftName", Text = Localization.Get("TestMessages.SampleGift") };
        translation = new()
        {
            Name = "TestTranslation", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 54, MaxHeight = 120
        };
        textFields = new StackPanel
        {
            Children = { Ui.Field("TestMessages.Content", messageContent), Ui.Field("TestMessages.Translation", translation) }
        };
        giftField = Ui.Field("TestMessages.GiftName", giftName);
        priceField = Ui.Field("TestMessages.UnitPrice", price);
        quantityField = Ui.Field("TestMessages.Quantity", quantity);
        guardField = Ui.Field("TestMessages.GuardLevel", guardLevel);
        var help = Ui.Text("TestMessages.Help");
        help.TextWrapping = TextWrapping.Wrap;
        help.Classes.Add("muted");
        var form = new StackPanel
        {
            Margin = new Thickness(12), Spacing = 4,
            Children =
            {
                target, help, Ui.Field("TestMessages.Kind", kind), Ui.Field("TestMessages.UserName", userName),
                textFields, giftField, priceField, quantityField, guardField,
                Ui.Field("TestMessages.BatchCount", batchCount), status
            }
        };
        form.Classes.Add("scrollSafeContent");
        var contextMenu = new LocalizedContextMenu();
        AddMenuItem(contextMenu, "TestMessages.AddSingle", LucideIconKind.Plus, AddSingleCommand);
        AddMenuItem(contextMenu, "TestMessages.AddBatch", LucideIconKind.ListPlus, AddBatchCommand);
        contextMenu.Items.Add(new Separator());
        AddMenuItem(contextMenu, "TestMessages.Clear", LucideIconKind.Trash2, ClearCommand);
        Content = new ScrollViewer
        {
            Content = form, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            ContextMenu = contextMenu
        };
        kind.SelectionChanged += (_, _) => RefreshFields();
        Localization.LanguageChanged += OnLanguageChanged;
        RefreshFields();
        RefreshTarget();
    }

    /// <summary>切换本地测试消息的目标房间；未选择房间时禁用操作。</summary>
    public void SelectRoom(RoomTabState? selected)
    {
        if (room is not null)
        {
            room.PropertyChanged -= OnRoomChanged;
        }
        room = selected;
        if (room is not null)
        {
            room.PropertyChanged += OnRoomChanged;
        }
        statusKey = null;
        status.IsVisible = false;
        RefreshTarget();
        RefreshCommands();
    }

    /// <summary>退出前停止新操作并等待已发起的本地事务完成。</summary>
    public Task FinishPendingAsync()
    {
        stopping = true;
        RefreshCommands();
        return Task.WhenAll(AddSingleCommand.ExecutionTask ?? Task.CompletedTask,
            AddBatchCommand.ExecutionTask ?? Task.CompletedTask, ClearCommand.ExecutionTask ?? Task.CompletedTask);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        stopping = true;
        Localization.LanguageChanged -= OnLanguageChanged;
        if (room is not null)
        {
            room.PropertyChanged -= OnRoomChanged;
        }
        RefreshCommands();
    }

    private async Task AddAsync(int count)
    {
        var selectedRoom = room!;
        var selectedKind = (MessageKind)kind.SelectedIndex;
        if (string.IsNullOrWhiteSpace(userName.Text) ||
            selectedKind is MessageKind.Danmu or MessageKind.SuperChat && string.IsNullOrWhiteSpace(messageContent.Text) ||
            selectedKind == MessageKind.Gift && string.IsNullOrWhiteSpace(giftName.Text))
        {
            SetStatus("TestMessages.Required", 0, "");
            return;
        }
        var request = new TestMessageRequest
        {
            Kind = selectedKind, UserName = userName.Text!, Content = messageContent.Text ?? "",
            GiftName = giftName.Text ?? "", UnitPrice = price.Value ?? 0,
            Quantity = decimal.ToInt32(quantity.Value ?? 1), GuardLevel = guardLevel.SelectedIndex + 1,
            Translation = translation.Text ?? "", Count = count
        };
        await RunAsync(async () =>
        {
            var added = await service.AddAsync(selectedRoom, request);
            SetStatus("TestMessages.Added", added, selectedRoom.Name);
        });
    }

    private async Task ClearAsync()
    {
        var selectedRoom = room!;
        await RunAsync(async () =>
        {
            var removed = await service.ClearAsync(selectedRoom);
            SetStatus("TestMessages.Cleared", removed, selectedRoom.Name);
        });
    }

    private async Task RunAsync(Func<Task> action)
    {
        busy = true;
        RefreshCommands();
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            SetStatus("TestMessages.Failed", 0, "");
            reportError(exception);
        }
        finally
        {
            busy = false;
            RefreshCommands();
        }
    }

    private bool CanExecute()
    {
        return room is not null && !busy && !stopping;
    }

    private void RefreshCommands()
    {
        AddSingleCommand.NotifyCanExecuteChanged();
        AddBatchCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    private void RefreshFields()
    {
        var selected = (MessageKind)kind.SelectedIndex;
        textFields.IsVisible = selected is MessageKind.Danmu or MessageKind.SuperChat;
        giftField.IsVisible = selected == MessageKind.Gift;
        priceField.IsVisible = selected != MessageKind.Danmu;
        quantityField.IsVisible = selected is MessageKind.Gift or MessageKind.Guard;
        guardField.IsVisible = selected == MessageKind.Guard;
    }

    private void RefreshTarget()
    {
        target.Text = room is null ? Localization.Get("TestMessages.NoRoom") : Localization.Format("TestMessages.Target", room.Name, room.RoomId);
    }

    private void SetStatus(string key, int count, string roomName)
    {
        statusKey = key;
        statusCount = count;
        statusRoom = roomName;
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        status.IsVisible = statusKey is not null;
        status.Text = statusKey is null ? "" : Localization.Format(statusKey, statusCount, statusRoom);
    }

    private void OnRoomChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RoomTabState.Name))
        {
            RefreshTarget();
        }
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshTarget();
        RefreshStatus();
    }

    private static ComboBox Choices(string name, IReadOnlyList<string> keys, int selected)
    {
        return new()
        {
            Name = name, HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = keys,
            ItemTemplate = new FuncDataTemplate<string>((key, _) => key is null ? new TextBlock() : Ui.Text(key)),
            SelectedIndex = selected
        };
    }

    private static Button HeaderButton(string key, LucideIconKind icon, IAsyncRelayCommand command)
    {
        var button = new Button { Content = new LucideIcon { Kind = icon, Size = 16 }, Command = command };
        button.Classes.Add("iconButton");
        button.Classes.Add("dockChromeButton");
        button.Bind(ToolTip.TipProperty, Localization.Observe(key));
        button.Bind(AutomationProperties.NameProperty, Localization.Observe(key));
        return button;
    }

    private static void AddMenuItem(LocalizedContextMenu menu, string key, LucideIconKind icon, IAsyncRelayCommand command)
    {
        var item = new MenuItem { Icon = new LucideIcon { Kind = icon, Size = 16 }, Command = command };
        menu.SetHeader(item, key);
        menu.Items.Add(item);
    }
}
