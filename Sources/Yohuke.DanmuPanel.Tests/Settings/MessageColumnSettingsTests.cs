using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Views.Settings;
using Xunit;

namespace Yohuke.DanmuPanel.Tests.Settings;

/// <summary>验证消息分栏按显示密度分别保存，并兼容旧配置及设置草稿。</summary>
public sealed class MessageColumnSettingsTests
{
    /// <summary>旧配置未包含列宽时，各显示密度和消息类型均使用默认比例。</summary>
    [Fact]
    public void LegacyConfigurationUsesIndependentDefaults()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"Language\":\"ja-JP\"}");

        var settings = store.Load().Settings;

        Assert.Equal("ja-JP", settings.Language);
        AssertWidths(new(), settings.MessageColumns);
        Assert.NotSame(settings.MessageColumns.Danmu, settings.MessageColumns.Gift);
        Assert.NotSame(settings.MessageColumns.Gift, settings.MessageColumns.Guard);
    }

    /// <summary>只指定宽松列宽的配置仍为紧凑布局及其他消息类型补齐默认值。</summary>
    [Fact]
    public void PartialConfigurationKeepsUnspecifiedLayoutDefaults()
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"),
            "{\"MessageColumns\":{\"Danmu\":{\"RegularIdentityRatio\":0.35}}}");

        var widths = store.Load().Settings.MessageColumns;

        Assert.Equal(0.35, widths.Danmu.RegularIdentityRatio);
        Assert.Equal(0.4, widths.Danmu.CompactIdentityRatio);
        Assert.Equal(0.4, widths.Gift.RegularIdentityRatio);
        Assert.Equal(0.4, widths.Gift.CompactIdentityRatio);
        Assert.Equal(0.6, widths.Guard.RegularIdentityRatio);
        Assert.Equal(0.6, widths.Guard.CompactIdentityRatio);
    }

    /// <summary>大航海的空对象及单边布局配置保留该消息类型自己的默认比例。</summary>
    [Theory]
    [InlineData("{}", 0.6, 0.6)]
    [InlineData("{\"RegularIdentityRatio\":0.65}", 0.65, 0.6)]
    [InlineData("{\"CompactIdentityRatio\":0.55}", 0.6, 0.55)]
    public async Task PartialGuardConfigurationKeepsItsKindSpecificDefaults(string guardJson, double regular, double compact)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var settingsJson = "{\"MessageColumns\":{\"Guard\":" + guardJson + "}}";
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), settingsJson);

        var restored = store.Load().Settings;
        var packageJson = "{\"Format\":\"Yohuke.DanmuPanel.Settings\",\"Version\":1,\"Settings\":" + settingsJson + "}";
        await using var package = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(packageJson));
        var imported = await new SettingsShareService().ImportAsync(package);

        Assert.Equal(regular, restored.MessageColumns.Guard.RegularIdentityRatio);
        Assert.Equal(compact, restored.MessageColumns.Guard.CompactIdentityRatio);
        Assert.Equal(regular, imported.MessageColumns.Guard.RegularIdentityRatio);
        Assert.Equal(compact, imported.MessageColumns.Guard.CompactIdentityRatio);
    }

    /// <summary>宽松、紧凑布局的六个比例在任一当前显示模式下均能独立往返。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothLayoutWidthsRoundTripIndependently(bool compactDisplay)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var columns = CreateWidths();
        await store.SaveAsync(new()
        {
            Settings = new() { CompactDanmuDisplay = compactDisplay, MessageColumns = columns }
        });

        var restored = store.Load().Settings;

        Assert.Equal(compactDisplay, restored.CompactDanmuDisplay);
        AssertWidths(columns, restored.MessageColumns);
        Assert.NotSame(columns, restored.MessageColumns);
    }

    /// <summary>无效列宽主文件沿用损坏文件保留及有效备份恢复流程。</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{\"Danmu\":null}")]
    [InlineData("{\"Gift\":null}")]
    [InlineData("{\"Guard\":null}")]
    [InlineData("{\"Danmu\":{\"RegularIdentityRatio\":0}}")]
    [InlineData("{\"Danmu\":{\"CompactIdentityRatio\":1}}")]
    [InlineData("{\"Gift\":{\"RegularIdentityRatio\":-0.1}}")]
    [InlineData("{\"Gift\":{\"CompactIdentityRatio\":1.1}}")]
    [InlineData("{\"Guard\":{\"RegularIdentityRatio\":0}}")]
    [InlineData("{\"Guard\":{\"CompactIdentityRatio\":1}}")]
    public async Task InvalidWidthsRestoreTheLastValidBackup(string columnsJson)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration { Settings = new() { MessageColumns = CreateWidths() } };
        await store.SaveAsync(configuration);
        await store.SaveAsync(configuration);
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"MessageColumns\":" + columnsJson + "}");

        AssertWidths(configuration.Settings.MessageColumns, store.Load().Settings.MessageColumns);
        Assert.Single(Directory.GetFiles(Path.Combine(directory.Path, "Corrupt")));
        AssertWidths(configuration.Settings.MessageColumns, store.Load().Settings.MessageColumns);
    }

    /// <summary>无效比例不能覆盖有效配置，也不能写出部分分享文件。</summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.01)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1.01)]
    public async Task InvalidRatiosAreRejectedBeforeWriting(double ratio)
    {
        using var directory = new MessageStoreTestDirectory();
        var store = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration { Settings = new() { MessageColumns = CreateWidths() } };
        await store.SaveAsync(configuration);
        var path = Path.Combine(directory.Path, "settings.json");
        var original = await File.ReadAllTextAsync(path);
        configuration.Settings.MessageColumns.Danmu.CompactIdentityRatio = ratio;
        await using var destination = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(configuration));
        await Assert.ThrowsAsync<InvalidDataException>(() => new SettingsShareService().ExportAsync(configuration.Settings, destination));

        Assert.Equal(original, await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        Assert.Equal(0, destination.Length);
    }

    /// <summary>设置窗口打开后调整的列宽不会被保存或导出其他设置时的旧草稿覆盖。</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SettingsDraftKeepsTheLatestColumnWidths(bool export)
    {
        using var directory = new MessageStoreTestDirectory();
        var configuration = new PanelConfiguration();
        await using var runtime = new PanelRuntime(new(directory.Path), configuration);
        using var destination = new MemoryStream();
        var dialogs = new ControlledSettingsTransferDialogs { ExportDestination = destination };
        var view = new SettingsView(runtime, new TextBlock(), true, dialogs);
        var window = new Window { Content = view, Width = 1020, Height = 760 };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            configuration.Settings.MessageColumns = CreateWidths();
            if (export)
            {
                Assert.True(await view.ExportSettingsAsync());
                using var document = JsonDocument.Parse(destination.ToArray());
                var exported = document.RootElement.GetProperty("Settings").GetProperty(nameof(PanelSettings.MessageColumns));
                AssertWidths(CreateWidths(), exported.Deserialize<MessageColumnSettings>()!);
            }
            else
            {
                Assert.True(await view.SaveAsync());
                AssertWidths(CreateWidths(), runtime.ConfigurationStore.Load().Settings.MessageColumns);
            }
            AssertWidths(CreateWidths(), configuration.Settings.MessageColumns);
        }
        finally
        {
            window.Close();
        }
    }

    private static MessageColumnSettings CreateWidths()
    {
        return new()
        {
            Danmu = new() { RegularIdentityRatio = 0.31, CompactIdentityRatio = 0.27 },
            Gift = new() { RegularIdentityRatio = 0.42, CompactIdentityRatio = 0.36 },
            Guard = new() { RegularIdentityRatio = 0.63, CompactIdentityRatio = 0.51 }
        };
    }

    private static void AssertWidths(MessageColumnSettings expected, MessageColumnSettings actual)
    {
        Assert.Equal(expected.Danmu.RegularIdentityRatio, actual.Danmu.RegularIdentityRatio);
        Assert.Equal(expected.Danmu.CompactIdentityRatio, actual.Danmu.CompactIdentityRatio);
        Assert.Equal(expected.Gift.RegularIdentityRatio, actual.Gift.RegularIdentityRatio);
        Assert.Equal(expected.Gift.CompactIdentityRatio, actual.Gift.CompactIdentityRatio);
        Assert.Equal(expected.Guard.RegularIdentityRatio, actual.Guard.RegularIdentityRatio);
        Assert.Equal(expected.Guard.CompactIdentityRatio, actual.Guard.CompactIdentityRatio);
    }
}
