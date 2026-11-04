using Avalonia.Controls;

namespace Yohuke.DanmuPanel.Views.Settings;

internal interface ISettingsTransferDialogs
{
    Task<Stream?> OpenImportAsync(Window owner);
    Task<Stream?> OpenExportAsync(Window owner);
    Task<bool> ConfirmImportAsync(Window owner);
}
