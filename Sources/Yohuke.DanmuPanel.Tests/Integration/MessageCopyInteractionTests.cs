using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;
using Yohuke.DanmuPanel.Views;
using Yohuke.DanmuPanel.Views.Components;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>通过真实指针输入验证消息右键选择边界和长按复制入口。</summary>
public sealed class MessageCopyInteractionTests
{
    /// <summary>右键只打开当前消息菜单，保留原有选择和消息阅读状态。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu, false)]
    [InlineData(MessageKind.Danmu, true)]
    [InlineData(MessageKind.SuperChat, false)]
    [InlineData(MessageKind.SuperChat, true)]
    [InlineData(MessageKind.Gift, false)]
    [InlineData(MessageKind.Gift, true)]
    [InlineData(MessageKind.Guard, false)]
    [InlineData(MessageKind.Guard, true)]
    public async Task RightClickPreservesSelectionAndMessageState(MessageKind kind, bool hasSelection)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), new());
        var first = Message(kind);
        var second = Message(kind) with { Time = first.Time.AddSeconds(1) };
        await runtime.Messages.AppendAsync(first, new());
        await runtime.Messages.AppendAsync(second, new());
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 650, Height = 450, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var list = view.GetVisualDescendants().OfType<ListBox>().Single(item => item.Name == "ScrollingMessages");
            var selected = hasSelection ? view.Model.Items.Single(item => item.Record.Id == second.Id) : null;
            list.SelectedItem = selected;
            var row = FindRow(view, first.Id);
            var point = Center(row, window);

            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Pump(window);

            Assert.True(row.ContextMenu!.IsOpen);
            Assert.Same(selected, list.SelectedItem);
            Assert.All(list.GetRealizedContainers().OfType<ListBoxItem>(), container =>
                Assert.Equal(ReferenceEquals(container.DataContext, selected), container.IsSelected));
            var unchanged = (await runtime.Messages.GetByIdAsync(first.Id))!;
            Assert.False(unchanged.IsRead);
            Assert.False(unchanged.IsPinned);
            row.ContextMenu.Close();
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>长按原文触发复制请求，松开时不执行置顶或标记已读，后续短按仍正常。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    public async Task HoldingRequestsCopyWithoutActivatingTheMessage(MessageKind kind)
    {
        var row = new MessageFeedRow(kind);
        var requests = 0;
        var clicks = 0;
        row.CopyRequested += (_, _) => requests++;
        row.Click += (_, _) => clicks++;
        var window = new Window { Width = 500, Height = 300, Content = row };
        try
        {
            window.Show();
            Pump(window);
            var point = Center(row, window);
            window.MouseDown(point, MouseButton.Left);
            await WaitForHoldAsync(window);
            Assert.Equal(1, requests);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(0, clicks);

            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(1, clicks);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>消息初次绑定后仍支持键盘激活，不依赖先点击鼠标。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    [InlineData(MessageKind.Gift)]
    [InlineData(MessageKind.Guard)]
    public void BindingPreservesKeyboardActivation(MessageKind kind)
    {
        var row = new MessageFeedRow(kind)
        {
            DataContext = new MessageItemViewModel(Message(kind), new PanelSettings(), new PinyinService())
        };
        var clicks = 0;
        row.Click += (_, _) => clicks++;
        var window = new Window { Width = 500, Height = 300, Content = row };
        try
        {
            window.Show();
            Pump(window);
            row.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.Equal(1, clicks);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>短按、拖动、滚轮和右键长按都不会误打开复制浮层。</summary>
    [AvaloniaTheory]
    [InlineData("short")]
    [InlineData("drag")]
    [InlineData("wheel")]
    [InlineData("right")]
    public async Task OtherPointerActionsDoNotRequestCopy(string action)
    {
        var row = new MessageFeedRow(MessageKind.SuperChat);
        var requests = 0;
        var clicks = 0;
        row.CopyRequested += (_, _) => requests++;
        row.Click += (_, _) => clicks++;
        var window = new Window { Width = 500, Height = 300, Content = row };
        try
        {
            window.Show();
            Pump(window);
            var point = Center(row, window);
            var button = action == "right" ? MouseButton.Right : MouseButton.Left;
            window.MouseDown(point, button);
            if (action == "short")
            {
                window.MouseUp(point, button);
            }
            else if (action == "drag")
            {
                window.MouseMove(point + new Vector(20, 0), RawInputModifiers.LeftMouseButton);
            }
            else if (action == "wheel")
            {
                window.MouseWheel(point, new Vector(0, -1));
            }
            await WaitForHoldAsync(window);
            window.MouseUp(point, button);
            Assert.Equal(0, requests);
            Assert.Equal(action == "short" ? 1 : 0, clicks);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>普通弹幕与 SC 长按后可拖选 BV 号、复制译文片段或全文，关闭后保持原阅读状态。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    public async Task HoldingPopupCopiesOriginalAndTranslationFragments(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), new());
        var settings = runtime.Configuration.Settings.Translation;
        settings.Enabled = true;
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        var record = Message(kind);
        await runtime.Messages.AppendAsync(record, new(), fingerprint);
        Assert.True(await runtime.Messages.ApplyTranslationAsync(record.Id, record.Generation, fingerprint,
            "おすすめの動画はこちら https://b23.tv/example です。"));
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 650, Height = 500, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var row = FindRow(view, record.Id);
            var point = Center(row, window);
            window.MouseDown(point, MouseButton.Left);
            await WaitForHoldAsync(window);
            var popup = Assert.IsType<MessageCopyPopup>(view.CopyPopup);
            Assert.True(popup.IsOpen);
            window.MouseUp(point, MouseButton.Left);
            Pump(window);
            var surface = Assert.IsType<MessageCopySurface>(popup.Content);
            var original = surface.FindControl<TextBox>("CopyOriginalText")!;
            var popupRoot = TopLevel.GetTopLevel(surface)!;
            Assert.Equal(record.Content, original.Text);
            Assert.True(original.IsReadOnly);
            var copySelection = surface.FindControl<Button>("CopySelectedTextButton")!;
            Assert.False(copySelection.IsEnabled);
            DragSelection(original, popupRoot, record.Content.IndexOf("BV", StringComparison.Ordinal), "BV1xx411c7mD".Length);
            Assert.Equal("BV1xx411c7mD", original.SelectedText);
            Assert.True(copySelection.IsEnabled);
            CaptureIfRequested(popupRoot, kind + "-selection");
            ClickButton(copySelection);
            await WaitForClipboardAsync(popupRoot, "BV1xx411c7mD");
            Assert.False(popup.IsOpen);
            Assert.Null(view.CopyPopup);

            view.ShowCopyPopup(row);
            Pump(window);
            popup = Assert.IsType<MessageCopyPopup>(view.CopyPopup);
            surface = Assert.IsType<MessageCopySurface>(popup.Content);
            popupRoot = TopLevel.GetTopLevel(surface)!;
            copySelection = surface.FindControl<Button>("CopySelectedTextButton")!;
            surface.FindControl<TabControl>("CopySourceTabs")!.SelectedIndex = 1;
            Pump(window);
            var translation = surface.FindControl<TextBox>("CopyTranslationText")!;
            Assert.True(translation.IsReadOnly);
            Assert.False(copySelection.IsEnabled);
            var link = "https://b23.tv/example";
            translation.SelectionStart = translation.Text!.IndexOf(link, StringComparison.Ordinal);
            translation.SelectionEnd = translation.SelectionStart + link.Length;
            translation.Focus();
            var commandModifier = popupRoot.PlatformSettings!.HotkeyConfiguration.CommandModifiers == KeyModifiers.Meta
                ? RawInputModifiers.Meta : RawInputModifiers.Control;
            popupRoot.KeyPress(Key.C, commandModifier, PhysicalKey.C, "c");
            popupRoot.KeyRelease(Key.C, RawInputModifiers.None, PhysicalKey.C, "c");
            await WaitForClipboardAsync(popupRoot, link);
            Assert.True(popup.IsOpen);
            ClickButton(surface.FindControl<Button>("CopyAllTextButton")!);
            await WaitForClipboardAsync(popupRoot, translation.Text);
            Assert.False(popup.IsOpen);
            var unchanged = (await runtime.Messages.GetByIdAsync(record.Id))!;
            Assert.False(unchanged.IsRead);
            Assert.False(unchanged.IsPinned);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>菜单入口复用复制浮层；无译文时隐藏译文页，消息换绑保留打开时的文本快照。</summary>
    [AvaloniaFact]
    public async Task CopyMenuKeepsSnapshotWhenMessageRowIsRebound()
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), new());
        var record = Message(MessageKind.SuperChat);
        await runtime.Messages.AppendAsync(record, new());
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, record.Kind, errors.Add);
        var window = new Window { Width = 650, Height = 450, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            var row = FindRow(view, record.Id);
            var menu = row.ContextMenu!;
            menu.Open(row);
            var copy = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, Localization.Get("Panel.CopyText")));
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(window);
            Assert.False(menu.IsOpen);
            var popup = Assert.IsType<MessageCopyPopup>(view.CopyPopup);
            Assert.True(popup.IsOpen);
            var surface = Assert.IsType<MessageCopySurface>(popup.Content);
            Assert.False(surface.FindControl<TabItem>("CopyTranslationTab")!.IsVisible);
            row.DataContext = new MessageItemViewModel(record with { Content = "新的消息" }, runtime.Configuration.Settings,
                new PinyinService());
            Assert.True(popup.IsOpen);
            Assert.Equal(record.Content, surface.FindControl<TextBox>("CopyOriginalText")!.Text);
            popup.Hide();
            view.ShowCopyPopup(row);
            surface = Assert.IsType<MessageCopySurface>(view.CopyPopup!.Content);
            Assert.Equal("新的消息", surface.FindControl<TextBox>("CopyOriginalText")!.Text);
            TopLevel.GetTopLevel(surface)!.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump(window);
            Assert.Null(view.CopyPopup);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    private static void ClickButton(Button button)
    {
        var root = TopLevel.GetTopLevel(button)!;
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), root)!.Value;
        root.MouseDown(point, MouseButton.Left);
        root.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>右侧嵌套面板和浮动窗口中的弹层贴近触发消息，并正确处理布局缩放。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.SuperChat, false, 1.0)]
    [InlineData(MessageKind.Danmu, false, 1.25)]
    [InlineData(MessageKind.SuperChat, true, 1.25)]
    [InlineData(MessageKind.Danmu, true, 1.0)]
    public async Task CopyPopupPositionsBesideMessageInOffsetAndScaledPanels(MessageKind kind, bool floating, double scale)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), new());
        var record = Message(kind);
        await runtime.Messages.AppendAsync(record, new());
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var transformed = new LayoutTransformControl
        {
            Child = view, LayoutTransform = new ScaleTransform(scale, scale), Margin = new Thickness(24, 72, 16, 16)
        };
        var layout = new Grid { ColumnDefinitions = new("*,*") };
        layout.Children.Add(new Border { Background = Brushes.DimGray });
        layout.Children.Add(transformed);
        Grid.SetColumn(transformed, 1);
        var window = new Window
        {
            Width = 1200, Height = 650, Content = layout,
            Position = floating ? new PixelPoint(200, 100) : default
        };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var row = FindRow(view, record.Id);
            view.ShowCopyPopup(row);
            Pump(window);
            var popup = Assert.IsType<MessageCopyPopup>(view.CopyPopup);
            var surface = Assert.IsType<MessageCopySurface>(popup.Content);
            var expected = row.PointToScreen(new Point(0, row.Bounds.Height));
            var actual = surface.PointToScreen(default);

            Assert.InRange(actual.X - expected.X, 0, 12);
            Assert.InRange(actual.Y - expected.Y, 0, 12);
            CaptureIfRequested(TopLevel.GetTopLevel(surface)!, $"position-{kind}-{floating}-{scale}");
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>新消息推进并复用原消息行时，浮层继续保留原文和选区，复制后关闭。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    public async Task IncomingMessagesAndRecyclingKeepCopySnapshotAndSelection(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), new());
        var room = new RoomTabState { Id = "tab", RoomId = "123" };
        await runtime.TestMessages.AddAsync(room, new()
        {
            Kind = kind, UserName = "viewer", Content = "推荐 BV1xx411c7mD，很好看。", Count = 100
        });
        var errors = new List<Exception>();
        var view = new MessageFeedView(runtime, kind, errors.Add);
        var window = new Window { Width = 650, Height = 300, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(room);
            await view.Model.WaitForPresentationAsync();
            window.Show();
            Pump(window);
            var row = view.GetVisualDescendants().OfType<MessageFeedRow>().Last();
            var source = Assert.IsType<MessageItemViewModel>(row.DataContext);
            view.ShowCopyPopup(row);
            Pump(window);
            var popup = Assert.IsType<MessageCopyPopup>(view.CopyPopup);
            var surface = Assert.IsType<MessageCopySurface>(popup.Content);
            var original = surface.FindControl<TextBox>("CopyOriginalText")!;
            var popupRoot = TopLevel.GetTopLevel(surface)!;
            DragSelection(original, popupRoot, source.Record.Content.IndexOf("BV", StringComparison.Ordinal), "BV1xx411c7mD".Length);

            await runtime.TestMessages.AddAsync(room, new()
            {
                Kind = kind, UserName = "next viewer", Content = "新的消息", Count = 100
            });
            await view.Model.RefreshAsync();
            await view.Model.WaitForPresentationAsync();
            Pump(window);
            var list = view.GetVisualDescendants().OfType<ListBox>().Single(item => item.Name == "ScrollingMessages");
            list.ScrollIntoView(view.Model.ScrollingItems[^1]);
            Pump(window);

            Assert.True(row.GetVisualRoot() is null || !ReferenceEquals(row.DataContext, source));
            Assert.True(popup.IsOpen);
            Assert.Equal(source.Record.Content, original.Text);
            Assert.Equal("BV1xx411c7mD", original.SelectedText);
            ClickButton(surface.FindControl<Button>("CopySelectedTextButton")!);
            await WaitForClipboardAsync(popupRoot, "BV1xx411c7mD");
            Assert.False(popup.IsOpen);
            Assert.Empty(errors);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>离开当前房间或移除面板时关闭复制浮层，不残留宿主引用。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeavingRoomOrPanelClosesCopyPopup(bool removePanel)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new ConfigurationService(directory.Path), new());
        var record = Message(MessageKind.Danmu);
        await runtime.Messages.AppendAsync(record, new());
        var view = new MessageFeedView(runtime, record.Kind, error => throw error);
        var window = new Window { Width = 650, Height = 300, Content = view };
        try
        {
            await view.Model.SelectRoomAsync(new() { Id = "tab", RoomId = "123" });
            window.Show();
            Pump(window);
            view.ShowCopyPopup(FindRow(view, record.Id));
            var popup = Assert.IsType<MessageCopyPopup>(view.CopyPopup);
            if (removePanel)
            {
                window.Content = null;
            }
            else
            {
                await view.Model.SelectRoomAsync(null);
            }
            Pump(window);
            Assert.False(popup.IsOpen);
            Assert.Null(view.CopyPopup);
        }
        finally
        {
            view.Model.Dispose();
            window.Close();
        }
    }

    /// <summary>窄窗口和三种语言下操作按钮完整可见，长消息全文复制保留换行和表情。</summary>
    [AvaloniaTheory]
    [InlineData("zh-CN", false)]
    [InlineData("zh-CN", true)]
    [InlineData("en-US", false)]
    [InlineData("en-US", true)]
    [InlineData("ja-JP", false)]
    [InlineData("ja-JP", true)]
    public async Task NarrowPopupKeepsActionsVisibleAndCopiesCompleteText(string language, bool dark)
    {
        var originalLanguage = Localization.SelectedLanguageID;
        Localization.SetLanguage(language);
        var record = Message(MessageKind.SuperChat) with
        {
            Content = string.Join('\n', Enumerable.Repeat("很长的弹幕，推荐 BV1xx411c7mD 🎉 https://b23.tv/example", 30))
        };
        var row = new MessageFeedRow(record.Kind)
        {
            DataContext = new MessageItemViewModel(record, new PanelSettings(), new PinyinService())
        };
        var errors = new List<Exception>();
        var window = new Window
        {
            Width = 340, Height = 600, Content = row,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light
        };
        try
        {
            window.Show();
            Pump(window);
            var popup = new MessageCopyPopup(record.Content, "", errors.Add);
            popup.Show(row, new Rect(row.Bounds.Size));
            Pump(window);
            var surface = Assert.IsType<MessageCopySurface>(popup.Content);
            Assert.InRange(surface.Bounds.Width, 1, window.ClientSize.Width);
            foreach (var button in surface.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible))
            {
                var point = button.TranslatePoint(default, surface)!.Value;
                Assert.InRange(point.X, 0, surface.Bounds.Width);
                Assert.InRange(point.Y, 0, surface.Bounds.Height);
                Assert.True(point.X + button.Bounds.Width <= surface.Bounds.Width + 0.5);
                Assert.True(point.Y + button.Bounds.Height <= surface.Bounds.Height + 0.5);
            }
            var popupRoot = TopLevel.GetTopLevel(surface)!;
            CaptureIfRequested(popupRoot, $"{language}-{(dark ? "dark" : "light")}");
            ClickButton(surface.FindControl<Button>("CopyAllTextButton")!);
            await WaitForClipboardAsync(popupRoot, record.Content);
            Assert.False(popup.IsOpen);
            Assert.Empty(errors);
        }
        finally
        {
            window.Close();
            Localization.SetLanguage(originalLanguage);
        }
    }

    private static void CaptureIfRequested(TopLevel root, string name)
    {
        if (Environment.GetEnvironmentVariable("DANMUPANEL_COPY_SCREENSHOTS") is not { Length: > 0 } directory)
        {
            return;
        }
        Directory.CreateDirectory(directory);
        root.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        Dispatcher.UIThread.RunJobs();
        using var image = root.CaptureRenderedFrame();
        Assert.NotNull(image);
        image.Save(Path.Combine(directory, name + ".png"));
    }

    private static void DragSelection(TextBox text, TopLevel root, int start, int length)
    {
        var presenter = text.GetVisualDescendants().OfType<TextPresenter>().Single();
        var first = presenter.TextLayout.HitTestTextPosition(start);
        var last = presenter.TextLayout.HitTestTextPosition(start + length);
        var from = presenter.TranslatePoint(new Point(first.X, first.Y + first.Height / 2), root)!.Value;
        var to = presenter.TranslatePoint(new Point(last.X, last.Y + last.Height / 2), root)!.Value;
        root.MouseDown(from, MouseButton.Left);
        root.MouseMove(to, RawInputModifiers.LeftMouseButton);
        root.MouseUp(to, MouseButton.Left);
    }

    private static async Task WaitForClipboardAsync(TopLevel root, string expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (await root.Clipboard!.TryGetTextAsync() == expected)
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Equal(expected, await root.Clipboard!.TryGetTextAsync());
    }

    private static MessageRecord Message(MessageKind kind)
    {
        return new()
        {
            RoomTabId = "tab", RoomId = "123", Kind = kind, UserName = "viewer",
            Content = "推荐 BV1xx411c7mD，也可以复制其中一段文字。", TotalPrice = 100
        };
    }

    private static MessageFeedRow FindRow(MessageFeedView view, string id)
    {
        return view.GetVisualDescendants().OfType<MessageFeedRow>().Single(row =>
            row.DataContext is MessageItemViewModel item && item.Record.Id == id);
    }

    private static Point Center(Control control, Window window)
    {
        return control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
    }

    private static async Task WaitForHoldAsync(Window window)
    {
        await Task.Delay(window.PlatformSettings!.HoldWaitDuration + TimeSpan.FromMilliseconds(100));
        Pump(window);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
