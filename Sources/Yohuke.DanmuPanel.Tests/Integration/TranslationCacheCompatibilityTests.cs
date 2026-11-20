using System.Text.Json;
using Avalonia.Headless.XUnit;
using Xunit;
using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Translation;
using Yohuke.DanmuPanel.Services;
using Yohuke.DanmuPanel.Services.Settings;
using Yohuke.DanmuPanel.Services.Translation;
using Yohuke.DanmuPanel.Tests.Messages;
using Yohuke.DanmuPanel.Tests.Translation;
using Yohuke.DanmuPanel.ViewModels;

namespace Yohuke.DanmuPanel.Tests.Integration;

/// <summary>验证旧版安装升级后保留语言身份和历史译文，旧缓存文件仅在手动清理时移除。</summary>
public sealed class TranslationCacheCompatibilityTests
{
    private const string LEGACY_CACHE_FILE_NAME = "translation-cache.db";
    private const string LEGACY_FINGERPRINT = "7FD88E7B20C0C0CED43077C79108FA1ECA45CED8B63E6D2954F9F6A7FC238337";
    private const string LEGACY_CACHE_KEY = "FB7805BF44170C1F73D475B7C8BBAFC75789C5740A9766362350906A698BD847";

    /// <summary>导入配置和重启后，旧语言身份仍能识别历史译文，运行时可忽略不可打开的旧库。</summary>
    [AvaloniaFact]
    public async Task LegacyTranslationIdentitySurvivesImportWithoutOpeningOldCacheDatabase()
    {
        using var directory = new MessageStoreTestDirectory();
        var configurationStore = new ConfigurationService(directory.Path);
        var cachePath = Path.Combine(directory.Path, LEGACY_CACHE_FILE_NAME);
        const string LEGACY_FILE_CONTENT = "旧库文件哨兵";
        await File.WriteAllTextAsync(cachePath, LEGACY_FILE_CONTENT);
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                Translation = new()
                {
                    Enabled = true, BaseUrl = "https://example.invalid/v1", Model = "old-model", TargetLanguage = "en"
                }
            }
        };
        var message = new MessageRecord { RoomTabId = "tab", Kind = MessageKind.Danmu, UserName = "viewer", Content = "hello" };
        await using (var runtime = new PanelRuntime(configurationStore, configuration))
        {
            Assert.Equal(LEGACY_FINGERPRINT, TranslationRequestFactory.GetConfigurationFingerprint(configuration.Settings.Translation));
            await runtime.Messages.AppendAsync(message, configuration.Settings.Reading, LEGACY_FINGERPRINT);
            Assert.True(await runtime.Messages.ApplyTranslationAsync(message.Id, message.Generation, LEGACY_FINGERPRINT, "保留历史译文"));
            new SettingsShareService().ApplyTo(configuration.Settings, new()
            {
                Translation = new()
                {
                    Enabled = true, BaseUrl = "https://new.example.invalid/v1", Model = "new-model", TargetLanguage = "en"
                }
            });
            await runtime.ApplySettingsAsync();
            Assert.Equal(LEGACY_FINGERPRINT, TranslationRequestFactory.GetConfigurationFingerprint(configuration.Settings.Translation));
        }

        await using (var reopened = new PanelRuntime(configurationStore, configurationStore.Load()))
        {
            await reopened.InitializeAsync();
            var settings = reopened.Configuration.Settings.Translation;
            var request = TranslationRequestFactory.Create(settings, message.Content)!;
            Assert.Equal(LEGACY_CACHE_KEY, request.CacheKey);
            var record = Assert.IsType<MessageRecord>(await reopened.Messages.GetByIdAsync(message.Id));
            var row = new MessageItemViewModel(record, reopened.Configuration.Settings, reopened.Pinyin);
            Assert.Equal("保留历史译文", row.Translation);

            settings.TargetLanguage = "ja";
            var japaneseFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
            Assert.NotEqual(LEGACY_FINGERPRINT, japaneseFingerprint);
            await reopened.ApplySettingsAsync();
            var restoredSettings = configurationStore.Load().Settings.Translation;
            Assert.Equal(japaneseFingerprint, TranslationRequestFactory.GetConfigurationFingerprint(restoredSettings));
            restoredSettings.TargetLanguage = "en";
            Assert.Equal(LEGACY_FINGERPRINT, TranslationRequestFactory.GetConfigurationFingerprint(restoredSettings));
        }

        Assert.Equal(LEGACY_FILE_CONTENT, await File.ReadAllTextAsync(cachePath));
        Assert.Equal(cachePath, Assert.Single(Directory.GetFiles(directory.Path, LEGACY_CACHE_FILE_NAME + "*")));
    }

    /// <summary>手动清理一并删除旧版缓存数据库及附属文件，退出后也不会重建旧库。</summary>
    [AvaloniaFact]
    public async Task ClearingTranslationCacheRemovesLegacyDatabaseAndSidecars()
    {
        using var directory = new MessageStoreTestDirectory();
        var configurationStore = new ConfigurationService(directory.Path);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            await File.WriteAllTextAsync(Path.Combine(directory.Path, LEGACY_CACHE_FILE_NAME + suffix), "旧缓存");
        }

        await using (var runtime = new PanelRuntime(configurationStore, new()))
        {
            await runtime.ClearTranslationCacheAsync();
            Assert.Empty(Directory.GetFiles(directory.Path, LEGACY_CACHE_FILE_NAME + "*"));
        }

        Assert.Empty(Directory.GetFiles(directory.Path, LEGACY_CACHE_FILE_NAME + "*"));
    }

    /// <summary>运行时启动先恢复快照再调度待办，退出保存快照，手动清除后仍保留已完成消息。</summary>
    [AvaloniaFact]
    public async Task RuntimeRestoresCacheBeforeSchedulingAndPersistsItUntilCleared()
    {
        using var directory = new MessageStoreTestDirectory();
        var configurationStore = new ConfigurationService(directory.Path);
        var configuration = new PanelConfiguration();
        var translation = configuration.Settings.Translation;
        translation.Enabled = true;
        translation.BaseUrl = "invalid for network";
        translation.Model = "test";
        TranslationRequestFactory.PreserveCacheIdentity(translation);
        var message = new MessageRecord { RoomTabId = "tab", Kind = MessageKind.Danmu, UserName = "viewer", Content = "hello" };
        var request = TranslationRequestFactory.Create(translation, message.Content)!;
        var otherRequest = TranslationRequestFactory.Create(translation, "another message")!;
        var snapshotPath = new AppDirectoryService(directory.Path).TranslationCacheSnapshotPath;
        var failures = new List<Exception>();

        await using (var service = new TranslationService(new DelegateTranslationClient((_, _) => Task.FromResult("已恢复译文"))))
        {
            var snapshot = new TranslationCacheSnapshotStore(snapshotPath, service, failures.Add);
            await snapshot.RestoreAsync();
            await service.TranslateAsync(request);
            await service.TranslateAsync(otherRequest);
            await snapshot.SaveAsync();
            Assert.Empty(failures);
        }

        await using (var runtime = new PanelRuntime(configurationStore, configuration))
        {
            var completion = new TaskCompletionSource<TranslationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            runtime.DanmuTranslationCommitted += args => completion.TrySetResult(args);
            await runtime.Messages.AppendAsync(message, configuration.Settings.Reading, request.ConfigurationFingerprint);
            await runtime.InitializeAsync();

            var completed = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(message.Id, completed.MessageId);
            Assert.Equal("已恢复译文", completed.Translation);
        }
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(snapshotPath)))
        {
            var entries = document.RootElement.GetProperty("Entries");
            Assert.Equal(2, entries.GetArrayLength());
            Assert.Equal(otherRequest.CacheKey, entries[0].GetProperty("Key").GetString());
            Assert.Equal(request.CacheKey, entries[1].GetProperty("Key").GetString());
        }

        await using (var runtime = new PanelRuntime(configurationStore, configurationStore.Load()))
        {
            await runtime.InitializeAsync();
            await runtime.ClearTranslationCacheAsync();
            Assert.False(File.Exists(snapshotPath));

            var record = Assert.IsType<MessageRecord>(await runtime.Messages.GetByIdAsync(message.Id));
            Assert.Equal("已恢复译文", record.Translation);
            Assert.Empty(await runtime.Messages.GetPendingAsync(10));
        }
        Assert.False(File.Exists(snapshotPath));
    }

    /// <summary>API 与术语变化和清空共享缓存均保留已保存的译文；目标语言切换才重新排队。</summary>
    [AvaloniaTheory]
    [InlineData(MessageKind.Danmu)]
    [InlineData(MessageKind.SuperChat)]
    public async Task SavedMessagesKeepTranslationAcrossConfigurationChangesAndCacheClear(MessageKind kind)
    {
        using var directory = new MessageStoreTestDirectory();
        await using var runtime = new PanelRuntime(new(directory.Path), new());
        var settings = runtime.Configuration.Settings;
        settings.Translation.Enabled = true;
        var fingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        var message = new MessageRecord { RoomTabId = "tab", Kind = kind, UserName = "viewer", Content = "hello" };
        await runtime.Messages.AppendAsync(message, settings.Reading, fingerprint);
        Assert.True(await runtime.Messages.ApplyTranslationAsync(message.Id, message.Generation, fingerprint, "保留译文"));
        var row = new MessageItemViewModel((await runtime.Messages.GetByIdAsync(message.Id))!, settings, runtime.Pinyin);

        settings.Translation.BaseUrl = "https://example.invalid/v1";
        settings.Translation.Model = "new-model";
        settings.Translation.ExtraSystemPrompt = "Be concise.";
        settings.Translation.Glossary.Add(new() { Source = "hello", Target = "您好", TargetLanguage = "zh-CN" });
        await runtime.ApplySettingsAsync();
        var currentFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        Assert.False(await runtime.Messages.UpsertTranslationJobAsync(message.Id, message.Generation, currentFingerprint));
        row.Update((await runtime.Messages.GetByIdAsync(message.Id))!, settings);

        Assert.Equal("保留译文", row.Translation);
        Assert.False(row.IsTranslating);
        Assert.Empty(await runtime.Messages.GetPendingAsync(10));
        await runtime.ClearTranslationCacheAsync();
        row.Update((await runtime.Messages.GetByIdAsync(message.Id))!, settings);
        Assert.Equal("保留译文", row.Translation);
        Assert.Empty(await runtime.Messages.GetPendingAsync(10));

        settings.Translation.TargetLanguage = "ja";
        await runtime.ApplySettingsAsync();
        currentFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings.Translation);
        Assert.True(await runtime.Messages.UpsertTranslationJobAsync(message.Id, message.Generation, currentFingerprint));
        row.Update((await runtime.Messages.GetByIdAsync(message.Id))!, settings);
        Assert.Empty(row.Translation);
        Assert.Single(await runtime.Messages.GetPendingAsync(10));
    }
}
