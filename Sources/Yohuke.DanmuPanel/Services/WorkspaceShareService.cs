using System.Globalization;
using System.Text.Json;
using Yohuke.DanmuPanel.I18n;
using Yohuke.DanmuPanel.Models.State;
using Yohuke.DanmuPanel.Models.Workspace;

namespace Yohuke.DanmuPanel.Services;

/// <summary>导入导出 Neo v1 工作区公共资料，始终创建新的本地阅读身份。</summary>
public sealed class WorkspaceShareService
{
    private static readonly JsonSerializerOptions JSON_OPTIONS = new() { WriteIndented = true };

    /// <summary>创建不含凭据、历史或 Panel 配置的分享包。</summary>
    public WorkspaceSharePackage CreatePackage(WorkspaceState workspace)
    {
        return new()
        {
            WorkspaceName = workspace.Name,
            Rooms = workspace.Rooms.Select(room => new WorkspaceShareRoom
            {
                RoomId = room.RoomId, RoomName = room.Name, OwnerUid = room.OwnerUid
            }).ToList()
        };
    }

    /// <summary>将公共资料写入选择的文件流。</summary>
    public Task ExportAsync(WorkspaceState workspace, Stream destination, CancellationToken cancellationToken = default)
    {
        return JsonSerializer.SerializeAsync(destination, CreatePackage(workspace), JSON_OPTIONS, cancellationToken);
    }

    /// <summary>从 Neo 或 Panel 分享流导入，未知的 Neo 业务字段不参与本地配置。</summary>
    public async Task<WorkspaceState> ImportAsync(IEnumerable<WorkspaceState> existing, Stream source,
        CancellationToken cancellationToken = default)
    {
        var package = await JsonSerializer.DeserializeAsync<WorkspaceSharePackage>(source, JSON_OPTIONS, cancellationToken)
            ?? throw new InvalidDataException(Localization.Get("Workspace.InvalidShare"));
        return CreateWorkspace(existing, package);
    }

    /// <summary>校验完整分享包后，创建拥有唯一名字和全新标签 ID 的工作区。</summary>
    public WorkspaceState CreateWorkspace(IEnumerable<WorkspaceState> existing, WorkspaceSharePackage package)
    {
        if (package.Version != 1 || package.Rooms is null || package.Rooms.Any(room => room is null ||
                !IsPositiveId(room.RoomId) || !string.IsNullOrWhiteSpace(room.OwnerUid) && !IsPositiveId(room.OwnerUid)))
        {
            throw new InvalidDataException(Localization.Get("Workspace.InvalidShare"));
        }
        var names = existing.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseName = string.IsNullOrWhiteSpace(package.WorkspaceName)
            ? Localization.Get("Workspace.ImportedName") : package.WorkspaceName.Trim();
        var name = baseName;
        for (var suffix = 2; names.Contains(name); suffix++)
        {
            name = $"{baseName} ({suffix})";
        }
        return new()
        {
            Name = name,
            Rooms = new(package.Rooms.Select(room => new RoomTabState
            {
                RoomId = room.RoomId.Trim(),
                Name = string.IsNullOrWhiteSpace(room.RoomName) ? room.RoomId.Trim() : room.RoomName.Trim(),
                OwnerUid = room.OwnerUid?.Trim() ?? ""
            }))
        };
    }

    internal static bool IsPositiveId(string? value)
    {
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
    }
}
