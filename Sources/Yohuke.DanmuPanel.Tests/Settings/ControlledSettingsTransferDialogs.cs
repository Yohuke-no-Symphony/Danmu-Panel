using Avalonia.Controls;
using Yohuke.DanmuPanel.Views.Settings;

namespace Yohuke.DanmuPanel.Tests.Settings;

internal sealed class ControlledSettingsTransferDialogs : ISettingsTransferDialogs
{
    internal Stream? ImportSource { get; set; }
    internal Stream? ExportDestination { get; set; }
    internal Exception? ImportException { get; set; }
    internal Task<bool> Confirmation { get; set; } = Task.FromResult(true);
    internal TaskCompletionSource<bool> ConfirmationOpened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int ImportRequests { get; private set; }
    internal int ExportRequests { get; private set; }
    internal int ConfirmationRequests { get; private set; }
    internal Window? LastImportOwner { get; private set; }
    internal Window? LastExportOwner { get; private set; }

    /// <summary>返回测试提供的导入流或选择错误。</summary>
    public Task<Stream?> OpenImportAsync(Window owner)
    {
        ImportRequests++;
        LastImportOwner = owner;
        return ImportException is { } exception ? Task.FromException<Stream?>(exception) : Task.FromResult(ImportSource);
    }

    /// <summary>返回测试提供的导出流。</summary>
    public Task<Stream?> OpenExportAsync(Window owner)
    {
        ExportRequests++;
        LastExportOwner = owner;
        return Task.FromResult(ExportDestination);
    }

    /// <summary>通知确认已打开，并等待测试控制的选择结果。</summary>
    public Task<bool> ConfirmImportAsync(Window owner)
    {
        ConfirmationRequests++;
        ConfirmationOpened.TrySetResult(true);
        return Confirmation;
    }
}
