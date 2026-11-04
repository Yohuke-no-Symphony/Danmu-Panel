using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lucide.Avalonia;
using Yohuke.DanmuPanel.Layouts;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Windowing;

/// <summary>从真实停靠面板驱动本地测试消息生成和清理。</summary>
public sealed class TestMessagesViewTests
{
    /// <summary>未选择房间时禁用操作，四类型输入通过标题图标或同一右键命令写入并能定向清理。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestPanelCommandsGenerateAllKindsAndClearOnlyLocalTestRecords(bool translationEnabled)
    {
        using var directory = new MessageStoreTestDirectory();
        var room = new RoomTabState { Id = "room", RoomId = "123", Name = "Test room" };
        var configuration = new PanelConfiguration
        {
            Settings = new() { Translation = new() { Enabled = translationEnabled, Model = "test" } }
        };
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var errors = new List<Exception>();
        using var view = new TestMessagesView(runtime, errors.Add);
        var panels = WorkbenchPanelIds.All.ToDictionary(id => id, id => (Control)new TextBlock { Text = id });
        panels[WorkbenchPanelIds.TEST_MESSAGES] = view;
        var shell = new ShellWindow(panels, new TextBlock(), directory.Path);
        try
        {
            await runtime.Messages.AppendAsync(new() { Id = "live", RoomTabId = room.Id, Content = "real message" }, new());
            await shell.InitializeLayoutAsync();
            shell.Show();
            shell.ShowPanel(WorkbenchPanelIds.TEST_MESSAGES);
            Pump(shell);
            Assert.False(view.AddSingleCommand.CanExecute(null));
            Assert.False(view.AddBatchCommand.CanExecute(null));
            Assert.False(view.ClearCommand.CanExecute(null));
            var actions = shell.GetVisualDescendants().OfType<Button>()
                .Where(button => ReferenceEquals(button.Command, view.AddSingleCommand) ||
                    ReferenceEquals(button.Command, view.AddBatchCommand) || ReferenceEquals(button.Command, view.ClearCommand)).ToArray();
            Assert.Equal(3, actions.Length);
            Assert.All(actions, button =>
            {
                Assert.Contains("iconButton", button.Classes);
                Assert.IsType<LucideIcon>(button.Content);
            });
            var context = Assert.IsType<ScrollViewer>(view.Content).ContextMenu!;
            Assert.Equal(3, context.Items.OfType<MenuItem>().Count());
            Assert.Same(view.AddSingleCommand, context.Items.OfType<MenuItem>().First().Command);

            view.SelectRoom(room);
            Assert.True(view.AddSingleCommand.CanExecute(null));
            var medalName = Find<TextBox>(view, "TestMedalName");
            var medalLevel = Find<NumericUpDown>(view, "TestMedalLevel");
            Assert.True(string.IsNullOrEmpty(medalName.Text));
            Assert.Equal(0m, medalLevel.Value);
            Assert.Equal(0m, medalLevel.Minimum);
            medalName.Text = "测试粉丝牌";
            medalLevel.Value = 28;
            await view.AddSingleCommand.ExecuteAsync(null);
            var kind = Find<ComboBox>(view, "TestKind");
            kind.SelectedIndex = (int)MessageKind.SuperChat;
            Find<TextBox>(view, "TestTranslation").Text = "handwritten translation";
            Find<NumericUpDown>(view, "TestBatchCount").Value = 3;
            await view.AddBatchCommand.ExecuteAsync(null);
            var superChats = await runtime.Messages.LoadPageAsync(room.Id, MessageKind.SuperChat, 0, 10);
            Assert.Equal(3, superChats.Count);
            Assert.All(superChats, row => Assert.Equal("handwritten translation", row.Translation));
            Find<TextBox>(view, "TestTranslation").Text = "";
            await view.AddSingleCommand.ExecuteAsync(null);

            kind.SelectedIndex = (int)MessageKind.Gift;
            Find<TextBox>(view, "TestGiftName").Text = "test gift";
            Find<NumericUpDown>(view, "TestUnitPrice").Value = 8;
            Find<NumericUpDown>(view, "TestQuantity").Value = 5;
            await view.AddSingleCommand.ExecuteAsync(null);
            Assert.Equal(40, Assert.Single(await runtime.Messages.LoadPageAsync(room.Id, MessageKind.Gift, 0, 10)).TotalPrice);

            kind.SelectedIndex = (int)MessageKind.Guard;
            Find<ComboBox>(view, "TestGuardLevel").SelectedIndex = 0;
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                shell.RequestedThemeVariant = theme;
                Pump(shell);
                var expected = Assert.IsAssignableFrom<ISolidColorBrush>(shell.Foreground).Color;
                foreach (var choice in new[] { kind, Find<ComboBox>(view, "TestGuardLevel") })
                {
                    var labels = choice.GetVisualDescendants().OfType<TextBlock>()
                        .Where(label => !string.IsNullOrWhiteSpace(label.Text)).ToArray();
                    Assert.NotEmpty(labels);
                    Assert.All(labels, label => Assert.Equal(expected, ((ISolidColorBrush)label.Foreground!).Color));
                }
            }
            await view.AddSingleCommand.ExecuteAsync(null);
            Assert.Equal(1, Assert.Single(await runtime.Messages.LoadPageAsync(room.Id, MessageKind.Guard, 0, 10)).GuardLevel);
            foreach (var messageKind in Enum.GetValues<MessageKind>())
            {
                var records = (await runtime.Messages.LoadPageAsync(room.Id, messageKind, 0, 10))
                    .Where(record => record.IsTest).ToArray();
                Assert.NotEmpty(records);
                Assert.All(records, record =>
                {
                    Assert.Equal("测试粉丝牌", record.MedalName);
                    Assert.Equal(28, record.MedalLevel);
                });
            }
            var pending = await runtime.Messages.GetPendingAsync(10);
            Assert.Equal(translationEnabled ? 2 : 0, pending.Count);
            if (translationEnabled)
            {
                Assert.Single(pending, job => job.Priority == 0);
                Assert.Single(pending, job => job.Priority == 1);
            }

            await view.ClearCommand.ExecuteAsync(null);
            Assert.Equal("live", Assert.Single(await runtime.Messages.LoadPageAsync(room.Id, MessageKind.Danmu, 0, 10)).Id);
            foreach (var messageKind in new[] { MessageKind.SuperChat, MessageKind.Gift, MessageKind.Guard })
            {
                Assert.Empty(await runtime.Messages.LoadPageAsync(room.Id, messageKind, 0, 10));
            }
            Assert.Empty(await runtime.Messages.GetPendingAsync(10));
            view.SelectRoom(null);
            Assert.False(view.AddSingleCommand.CanExecute(null));
            Assert.Empty(errors);
        }
        finally
        {
            await view.FinishPendingAsync();
            await shell.CloseAsync();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static T Find<T>(Control root, string name) where T : Control
    {
        return Assert.Single(root.GetVisualDescendants().OfType<T>(), control => control.Name == name);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
