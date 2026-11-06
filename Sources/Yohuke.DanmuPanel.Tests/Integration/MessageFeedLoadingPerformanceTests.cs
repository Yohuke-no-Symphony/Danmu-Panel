using System.Diagnostics;
using System.Collections.Specialized;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;
using Xunit.Abstractions;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>区分数据库读取与显示投影成本，验证历史数量调整仅改变窗口并保留已有实例。</summary>
public sealed class MessageFeedLoadingPerformanceTests(ITestOutputHelper output)
{
    /// <summary>连续补入和裁剪历史只发布一个范围通知，避免可视行随每条消息反复改索引。</summary>
    [AvaloniaFact]
    public async Task RetentionChangesPublishContiguousHistoryAsSingleRanges()
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        configuration.Settings.MessageRetentionCount = 300;
        configuration.Settings.Translation.Enabled = false;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var records = Enumerable.Range(0, 1000).Select(index => new MessageRecord
        {
            Id = "range-" + index, RoomTabId = "range", Kind = MessageKind.Danmu, IsTest = true,
            UserName = "viewer", Content = "history " + index,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        }).ToArray();
        foreach (var batch in records.Chunk(500))
        {
            await runtime.Messages.AppendTestMessagesAsync(batch, configuration.Settings.Reading);
        }
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        await model.SelectRoomAsync(new RoomTabState { Id = "range" });
        await model.WaitForPresentationAsync();
        var retained = model.ScrollingRows.ToArray();
        var changes = new List<NotifyCollectionChangedEventArgs>();
        model.ScrollingRows.CollectionChanged += (_, args) => changes.Add(args);

        configuration.Settings.MessageRetentionCount = 1000;
        await model.RefreshAsync();
        await model.WaitForPresentationAsync();

        var added = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
        Assert.Equal(0, added.NewStartingIndex);
        Assert.Equal(700, added.NewItems!.Count);
        Assert.Equal(retained, model.ScrollingRows.Skip(700));
        Assert.Equal(records.Select(record => record.Id), model.ScrollingItems.Select(item => item.Record.Id));
        changes.Clear();

        configuration.Settings.MessageRetentionCount = 300;
        await model.RefreshAsync();
        await model.WaitForPresentationAsync();

        var removed = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Remove, removed.Action);
        Assert.Equal(0, removed.OldStartingIndex);
        Assert.Equal(700, removed.OldItems!.Count);
        Assert.Equal(retained, model.ScrollingRows);
        Assert.Equal(0, model.ViewportUpdateDepth);
    }

    /// <summary>八百条文本的首次显示、缩小与放大窗口保持可恢复状态，并分别记录耗时。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingAndChangingRetentionKeepTheExistingMessages(bool pinyin)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        var settings = configuration.Settings;
        settings.MessageRetentionCount = 800;
        settings.ShowUserPinyin = pinyin;
        settings.ShowDanmuPinyin = pinyin;
        settings.Translation.Enabled = false;
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        var records = Enumerable.Range(0, 1000).Select(index => new MessageRecord
        {
            Id = "loading-" + index, RoomTabId = "loading", Kind = MessageKind.Danmu, IsTest = true,
            UserName = "晚安观众" + index, Content = "今天的直播很开心大家晚安" + index,
            Time = DateTimeOffset.FromUnixTimeSeconds(1800000000 + index)
        }).ToArray();
        foreach (var batch in records.Chunk(500))
        {
            await runtime.Messages.AppendTestMessagesAsync(batch, settings.Reading);
        }
        var startedAt = Stopwatch.GetTimestamp();
        var snapshot = await runtime.Messages.LoadFeedAsync("loading", MessageKind.Danmu, 800, null);
        var databaseMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        Assert.Equal(800, snapshot.Records.Count);
        using var model = new FeedViewModel(runtime, MessageKind.Danmu);
        startedAt = Stopwatch.GetTimestamp();
        await model.SelectRoomAsync(new RoomTabState { Id = "loading" });
        var projectionMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        await model.WaitForPresentationAsync();
        var readyMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var retained = model.Items[^1];

        settings.MessageRetentionCount = 300;
        startedAt = Stopwatch.GetTimestamp();
        await model.RefreshAsync();
        await model.WaitForPresentationAsync();
        var shrinkMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        Assert.Equal(300, model.ScrollingItems.Count);
        Assert.Same(retained, model.Items[^1]);

        settings.MessageRetentionCount = 1000;
        startedAt = Stopwatch.GetTimestamp();
        await model.RefreshAsync();
        await model.WaitForPresentationAsync();
        var growMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        Assert.Equal(1000, model.ScrollingItems.Count);
        Assert.Same(retained, model.Items[^1]);
        Assert.Equal(0, model.ViewportUpdateDepth);
        var json = JsonSerializer.Serialize(new { pinyin, databaseMs, projectionMs, readyMs, shrinkMs, growMs });
        output.WriteLine(json);
        if (Environment.GetEnvironmentVariable("DANMUPANEL_SCROLL_METRICS_DIRECTORY") is { Length: > 0 } metricsPath)
        {
            Directory.CreateDirectory(metricsPath);
            File.WriteAllText(Path.Combine(metricsPath, $"loading-{pinyin}.json"), json);
        }
    }
}
