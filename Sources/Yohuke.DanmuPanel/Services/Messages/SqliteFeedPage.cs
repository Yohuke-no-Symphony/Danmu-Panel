namespace Yohuke.DanmuPanel.Services.Messages;

internal readonly record struct SqliteFeedPage(IReadOnlyList<SqliteFeedRow> Rows, bool HasOlder, bool HasNewer);
