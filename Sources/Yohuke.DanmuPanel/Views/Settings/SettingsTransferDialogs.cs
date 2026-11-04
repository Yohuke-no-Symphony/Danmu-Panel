using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Yohuke.DanmuPanel.I18n;

namespace Yohuke.DanmuPanel.Views.Settings;

internal sealed class SettingsTransferDialogs : ISettingsTransferDialogs
{
    /// <summary>选择设置文件并打开由调用方释放的只读流，取消时返回空。</summary>
    public async Task<Stream?> OpenImportAsync(Window owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new()
        {
            Title = Localization.Get("Settings.Import"),
            AllowMultiple = false,
            FileTypeFilter = [new(Localization.Get("Settings.FileType")) { Patterns = ["*.dnsettings.json", "*.json"] }]
        });
        return files.FirstOrDefault() is { } file ? await file.OpenReadAsync() : null;
    }

    /// <summary>选择导出路径并打开由调用方释放的写入流，取消时返回空。</summary>
    public async Task<Stream?> OpenExportAsync(Window owner)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new()
        {
            Title = Localization.Get("Settings.Export"),
            SuggestedFileName = "Yohuke.DanmuPanel.dnsettings.json",
            DefaultExtension = "dnsettings.json",
            FileTypeChoices = [new(Localization.Get("Settings.FileType")) { Patterns = ["*.dnsettings.json"] }],
            ShowOverwritePrompt = true
        });
        return file is null ? null : await file.OpenWriteAsync();
    }

    /// <summary>在覆盖当前全局设置前说明范围和窗口关闭行为。</summary>
    public Task<bool> ConfirmImportAsync(Window owner)
    {
        return new ConfirmationWindow(Localization.Get("Settings.Import"), Localization.Get("Settings.ImportConfirm"))
            .ShowDialog<bool>(owner);
    }
}
