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

/// <summary>验证旧版缓存、历史译文和本地缓存身份在配置编辑及重启后继续复用。</summary>
public sealed class TranslationCacheCompatibilityTests
{
    private const string LEGACY_FINGERPRINT = "7FD88E7B20C0C0CED43077C79108FA1ECA45CED8B63E6D2954F9F6A7FC238337";
    private const string LEGACY_CACHE_KEY = "FB7805BF44170C1F73D475B7C8BBAFC75789C5740A9766362350906A698BD847";

    /// <summary>首次升级保留旧键，导入新配置及重启后仍直接命中旧版共享缓存。</summary>
    [AvaloniaFact]
    public async Task LegacyCacheSurvivesSettingsImportAndRestart()
    {
        using var directory = new MessageStoreTestDirectory();
        var configurationStore = new ConfigurationService(directory.Path);
        var cachePath = Path.Combine(directory.Path, "translation-cache.db");
        await using (var cache = new SqliteTranslationCache(cachePath))
        {
            await cache.SetAsync(LEGACY_CACHE_KEY, "cached translation");
        }
        var configuration = new PanelConfiguration
        {
            Settings = new()
            {
                Translation = new() { BaseUrl = "https://example.invalid/v1", Model = "old-model", TargetLanguage = "en" }
            }
        };
        await using (var runtime = new PanelRuntime(configurationStore, configuration))
        {
            Assert.Equal(LEGACY_FINGERPRINT, TranslationRequestFactory.GetConfigurationFingerprint(configuration.Settings.Translation));
            new SettingsShareService().ApplyTo(configuration.Settings, new()
            {
                Translation = new() { BaseUrl = "https://new.example.invalid/v1", Model = "new-model", TargetLanguage = "en" }
            });
            await runtime.ApplySettingsAsync();
            Assert.Equal(LEGACY_FINGERPRINT, TranslationRequestFactory.GetConfigurationFingerprint(configuration.Settings.Translation));
        }

        await using var reopened = new PanelRuntime(configurationStore, configurationStore.Load());
        var settings = reopened.Configuration.Settings.Translation;
        var request = TranslationRequestFactory.Create(settings, "hello")!;
        Assert.Equal(LEGACY_CACHE_KEY, request.CacheKey);
        await using var restoredCache = new SqliteTranslationCache(cachePath);
        await using var service = new TranslationService(new DelegateTranslationClient((_, _) =>
            throw new InvalidOperationException("Cached text must not call the API.")), restoredCache);
        Assert.Equal("cached translation", await service.TranslateAsync(request));

        settings.TargetLanguage = "ja";
        var japaneseFingerprint = TranslationRequestFactory.GetConfigurationFingerprint(settings);
        Assert.NotEqual(LEGACY_FINGERPRINT, japaneseFingerprint);
        await reopened.ApplySettingsAsync();
        var restoredSettings = configurationStore.Load().Settings.Translation;
        Assert.Equal(japaneseFingerprint, TranslationRequestFactory.GetConfigurationFingerprint(restoredSettings));
        restoredSettings.TargetLanguage = "en";
        Assert.Equal(LEGACY_FINGERPRINT, TranslationRequestFactory.GetConfigurationFingerprint(restoredSettings));
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
