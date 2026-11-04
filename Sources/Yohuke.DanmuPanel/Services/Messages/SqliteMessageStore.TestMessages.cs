using Yohuke.DanmuPanel.Models.Messages;
using Yohuke.DanmuPanel.Models.Testing;

namespace Yohuke.DanmuPanel.Services.Messages;

public sealed partial class SqliteMessageStore
{
    /// <inheritdoc />
    public Task<int> AppendTestMessagesAsync(IReadOnlyList<MessageRecord> messages, MessageReadOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(messages.Count, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(messages.Count, TestMessageRequest.MAX_BATCH_COUNT);
        var snapshot = messages.ToArray();
        var roomTabId = snapshot[0].RoomTabId;
        ArgumentException.ThrowIfNullOrWhiteSpace(roomTabId);
        if (snapshot.Any(message => !message.IsTest || message.RoomTabId != roomTabId ||
            !string.IsNullOrEmpty(message.MessageId) || !string.IsNullOrEmpty(message.ComboId)) ||
            snapshot.Select(message => message.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException("A test batch must contain unique local messages from one room.", nameof(messages));
        }

        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "INSERT OR IGNORE INTO message_rooms(id) VALUES($room);", ("$room", roomTabId));
            var generation = ReadGeneration(connection, transaction, roomTabId);
            WriteOptions(connection, transaction, options);
            foreach (var message in snapshot)
            {
                if (ReadMessage(connection, transaction, message.Id) is not null)
                {
                    throw new ArgumentException("A test message ID must be unique.", nameof(messages));
                }
                var record = message with
                {
                    Generation = generation, IsRead = false, IsPinned = false, PinnedAt = null,
                    HasBeenRead = false, ReadQuantity = 0, ReadTotalPrice = 0
                };
                WriteMessage(connection, transaction, record with { IsPriority = IsPriority(record, options) });
            }
            transaction.Commit();
            return snapshot.Length;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<int> ClearTestMessagesAsync(string roomTabId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roomTabId);
        return EnqueueAsync(connection =>
        {
            using var transaction = connection.BeginTransaction();
            var removed = Execute(connection, transaction,
                "DELETE FROM message_records WHERE room_tab_id=$room AND is_test=1;", ("$room", roomTabId));
            transaction.Commit();
            return removed;
        }, cancellationToken);
    }
}
