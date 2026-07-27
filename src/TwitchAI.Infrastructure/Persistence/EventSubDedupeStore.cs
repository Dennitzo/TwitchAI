namespace TwitchAI.Infrastructure.Persistence;

public interface IEventSubDedupeStore
{
    Task<bool> TryMarkProcessedAsync(
        string eventMessageId,
        string subscriptionType,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default);

    Task<int> DeleteOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default);

    Task ReleaseProcessedAsync(
        string eventMessageId,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class EventSubDedupeStore : IEventSubDedupeStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;

    public EventSubDedupeStore(
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task<bool> TryMarkProcessedAsync(
        string eventMessageId,
        string subscriptionType,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionType);

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO processed_eventsub_events(
                eventsub_message_id, subscription_type, received_utc)
            VALUES(@message_id, @subscription_type, @received_utc);
            """;
        command.Parameters.AddWithValue("@message_id", eventMessageId);
        command.Parameters.AddWithValue("@subscription_type", subscriptionType);
        command.Parameters.AddWithValue("@received_utc", SqliteData.ToDatabaseTimestamp(receivedAt));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<int> DeleteOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM processed_eventsub_events
            WHERE received_utc < @cutoff;
            """;
        command.Parameters.AddWithValue("@cutoff", SqliteData.ToDatabaseTimestamp(cutoff));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseProcessedAsync(
        string eventMessageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventMessageId);
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM processed_eventsub_events
            WHERE eventsub_message_id = @message_id;
            """;
        command.Parameters.AddWithValue("@message_id", eventMessageId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
