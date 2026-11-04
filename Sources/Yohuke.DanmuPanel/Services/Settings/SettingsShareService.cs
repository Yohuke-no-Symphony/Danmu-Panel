using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Yohuke.DanmuPanel.Models.Settings;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Messages;

namespace Yohuke.DanmuPanel.Services.Settings;

/// <summary>读写可迁移的全局设置，保留 API Key 并隔离本地账号与工作区绑定。</summary>
public sealed class SettingsShareService
{
    private static readonly JsonSerializerOptions jsonOptions = CreateJsonOptions();

    /// <summary>验证并深复制当前设置，分享包的编辑不会改变源配置。</summary>
    public SettingsSharePackage CreatePackage(PanelSettings settings)
    {
        Validate(settings);
        return new() { Settings = Snapshot(settings) };
    }

    /// <summary>完整校验并复制可迁移设置，保留目标实例及本地账号、工作区绑定。</summary>
    public void ApplyTo(PanelSettings target, PanelSettings source)
    {
        var settings = CreatePackage(source).Settings;
        CopyTo(target, settings);
    }

    internal PanelSettings Snapshot(PanelSettings settings)
    {
        return JsonSerializer.Deserialize<PanelSettings>(JsonSerializer.SerializeToUtf8Bytes(settings, jsonOptions), jsonOptions)!;
    }

    internal void RestoreTo(PanelSettings target, PanelSettings snapshot)
    {
        CopyTo(target, snapshot);
    }

    private static void CopyTo(PanelSettings target, PanelSettings settings)
    {
        target.Language = settings.Language;
        target.Theme = settings.Theme;
        target.ColorScheme = settings.ColorScheme;
        target.UseWindowMenuOnMac = settings.UseWindowMenuOnMac;
        target.CheckForUpdatesOnStartup = settings.CheckForUpdatesOnStartup;
        target.Translation = settings.Translation;
        target.Reading = settings.Reading;
        target.ShowUserPinyin = settings.ShowUserPinyin;
        target.ShowGiftPinyin = settings.ShowGiftPinyin;
        target.ShowDanmuPinyin = settings.ShowDanmuPinyin;
        target.ShowSuperChatPinyin = settings.ShowSuperChatPinyin;
        target.PinyinLayout = settings.PinyinLayout;
        target.MessageTextScale = settings.MessageTextScale;
        target.CompactDanmuDisplay = settings.CompactDanmuDisplay;
        target.CompactDanmuWrap = settings.CompactDanmuWrap;
        target.CompactSuperChatWrap = settings.CompactSuperChatWrap;
        target.AutoScrollLongMessages = settings.AutoScrollLongMessages;
        target.HideEmoticonDanmu = settings.HideEmoticonDanmu;
        target.DanmuRegexFilter = settings.DanmuRegexFilter;
    }

    /// <summary>将包含原始 API Key 的设置快照写入调用方持有的流。</summary>
    public Task ExportAsync(PanelSettings settings, Stream destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return JsonSerializer.SerializeAsync(destination, CreatePackage(settings), jsonOptions, cancellationToken);
    }

    /// <summary>完整校验文件后返回独立设置，不修改运行配置或关闭输入流。</summary>
    public async Task<PanelSettings> ImportAsync(Stream source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SettingsSharePackage package;
        try
        {
            package = await JsonSerializer.DeserializeAsync<SettingsSharePackage>(source, jsonOptions, cancellationToken)
                ?? throw new InvalidDataException("The settings package is missing.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The settings file is malformed or has missing required fields.", exception);
        }

        if (package.Format != SettingsSharePackage.FORMAT || package.Version != SettingsSharePackage.CURRENT_VERSION)
        {
            throw new InvalidDataException("The settings file format or version is not supported.");
        }
        Validate(package.Settings);
        return package.Settings;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(typeInfo =>
        {
            if (typeInfo.Type != typeof(PanelSettings))
            {
                return;
            }
            foreach (var property in typeInfo.Properties.Where(property =>
                         property.Name is nameof(PanelSettings.DefaultAccountId) or nameof(PanelSettings.SelectedWorkspaceId)).ToArray())
            {
                typeInfo.Properties.Remove(property);
            }
        });
        return new() { WriteIndented = true, TypeInfoResolver = resolver };
    }

    private static void Validate(PanelSettings settings)
    {
        if (settings is null || string.IsNullOrWhiteSpace(settings.Language) ||
            settings.Theme is not ("system" or "light" or "dark") ||
            !double.IsFinite(settings.MessageTextScale) || settings.MessageTextScale is < 0.5 or > 2 ||
            !Enum.IsDefined(settings.PinyinLayout))
        {
            throw new InvalidDataException("The appearance or message display settings are invalid.");
        }
        ColorSchemeService.Validate(settings.ColorScheme);
        DanmuRegexFilter.Validate(settings.DanmuRegexFilter);
        var reading = settings.Reading;
        if (reading is null || reading.GiftAutoPinMinimum is < 0 or > 1000000000 || !Enum.IsDefined(reading.GiftReadResetPolicy))
        {
            throw new InvalidDataException("The reading settings are invalid.");
        }

        var translation = settings.Translation;
        if (translation is null || translation.BaseUrl is null || translation.ApiKey is null || translation.Model is null ||
            translation.TargetLanguage is null || translation.ExtraSystemPrompt is null || translation.Glossary is null ||
            !Enum.IsDefined(translation.DisplayMode) || !Enum.IsDefined(translation.BacklogMode) ||
            translation.Glossary.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Source) ||
                string.IsNullOrWhiteSpace(entry.Target) || string.IsNullOrWhiteSpace(entry.TargetLanguage)))
        {
            throw new InvalidDataException("The translation settings or glossary are invalid.");
        }
        if (translation.Enabled && (!Uri.TryCreate(translation.BaseUrl, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            string.IsNullOrWhiteSpace(translation.Model) || string.IsNullOrWhiteSpace(translation.TargetLanguage)))
        {
            throw new InvalidDataException("Enabled translation requires a valid endpoint, model and target language.");
        }
    }
}
