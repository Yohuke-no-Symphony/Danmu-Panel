using System.Collections.ObjectModel;
using System.Text.Json;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Services.Appearance;
using Yohuke.DanmuPanel.Services.Messages;
using Yohuke.DanmuPanel.Services.Translation;

namespace Yohuke.DanmuPanel.Services;

/// <summary>通过原子替换及可恢复备份保存阅读台配置。</summary>
public sealed class ConfigurationService
{
    private static readonly JsonSerializerOptions JSON_OPTIONS = new() { WriteIndented = true, IgnoreReadOnlyProperties = true };
    private readonly SemaphoreSlim writer = new(1, 1);
    public string DirectoryPath { get; }

    /// <summary>使用独立产品数据目录。</summary>
    public ConfigurationService(string? directory = null)
    {
        DirectoryPath = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yohuke.DanmuPanel");
        Directory.CreateDirectory(DirectoryPath);
    }

    /// <summary>加载配置，损坏文件保留原件并恢复最近备份。</summary>
    public PanelConfiguration Load()
    {
        var configuration = new PanelConfiguration
        {
            Settings = Read<PanelSettings>("settings.json"),
            Accounts = Read<ObservableCollection<BilibiliAccount>>("accounts.json"),
            Workspaces = Read<ObservableCollection<WorkspaceState>>("workspaces.json")
        };
        TranslationRequestFactory.PreserveCacheIdentity(configuration.Settings.Translation);
        return configuration;
    }

    /// <summary>串行保存当前配置快照。</summary>
    public async Task SaveAsync(PanelConfiguration configuration)
    {
        ColorSchemeService.Validate(configuration.Settings.ColorScheme);
        MessageBorderSettingsValidator.Validate(configuration.Settings.MessageBorders);
        DanmuRegexFilter.Validate(configuration.Settings.DanmuRegexFilter);
        MessageColumnSettingsValidator.Validate(configuration.Settings.MessageColumns);
        MessageRetentionSettingsValidator.Validate(configuration.Settings.MessageRetentionCount);
        var snapshots = new Dictionary<string, string>
        {
            ["settings.json"] = JsonSerializer.Serialize(configuration.Settings, JSON_OPTIONS),
            ["accounts.json"] = JsonSerializer.Serialize(configuration.Accounts, JSON_OPTIONS),
            ["workspaces.json"] = JsonSerializer.Serialize(configuration.Workspaces, JSON_OPTIONS)
        };
        await writer.WaitAsync();
        try
        {
            foreach (var (name, json) in snapshots)
            {
                var path = Path.Combine(DirectoryPath, name);
                var temporary = path + ".tmp";
                await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    await stream.WriteAsync(bytes);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                {
                    File.Copy(path, path + ".bak", true);
                }
                File.Move(temporary, path, true);
            }
        }
        finally
        {
            writer.Release();
        }
    }

    private T Read<T>(string name) where T : new()
    {
        var path = Path.Combine(DirectoryPath, name);
        if (TryRead(path, out T? value))
        {
            return value!;
        }
        var backup = path + ".bak";
        if (TryRead(backup, out T? recovered))
        {
            File.Copy(backup, path, true);
            return recovered!;
        }
        return new();
    }

    private bool TryRead<T>(string path, out T? value)
    {
        value = default;
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            value = JsonSerializer.Deserialize<T>(File.ReadAllText(path), JSON_OPTIONS) ?? throw new JsonException("Null configuration");
            if (value is PanelSettings settings)
            {
                ColorSchemeService.Validate(settings.ColorScheme);
                MessageBorderSettingsValidator.Validate(settings.MessageBorders);
                DanmuRegexFilter.Validate(settings.DanmuRegexFilter);
                MessageColumnSettingsValidator.Validate(settings.MessageColumns);
                MessageRetentionSettingsValidator.Validate(settings.MessageRetentionCount);
            }
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            var corrupt = Path.Combine(DirectoryPath, "Corrupt");
            Directory.CreateDirectory(corrupt);
            File.Move(path, Path.Combine(corrupt, $"{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}-{Path.GetFileName(path)}"));
            return false;
        }
    }
}
