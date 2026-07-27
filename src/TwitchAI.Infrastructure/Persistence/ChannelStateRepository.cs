using Microsoft.Data.Sqlite;

namespace TwitchAI.Infrastructure.Persistence;

public sealed record ChannelStateRecord(
    string BroadcasterId,
    string BroadcasterLogin,
    string BroadcasterName,
    string ModeratorUserId,
    string? CategoryId,
    string? CategoryName,
    DateTimeOffset ObservedAt,
    long Revision = 1,
    bool IsVerified = true);

public interface IChannelStateRepository
{
    Task UpsertAsync(ChannelStateRecord state, CancellationToken cancellationToken = default);

    Task<ChannelStateRecord?> GetAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default);
}

public sealed class ChannelStateRepository : IChannelStateRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;

    public ChannelStateRepository(
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task UpsertAsync(
        ChannelStateRecord state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        if (state.CategoryId is not null)
        {
            await using var categoryCommand = connection.CreateCommand();
            categoryCommand.Transaction = transaction;
            categoryCommand.CommandText =
                """
                INSERT INTO twitch_categories(
                    category_id, display_name, last_seen_utc, last_verified_utc)
                VALUES(@category_id, @category_name, @observed_utc, NULL)
                ON CONFLICT(category_id) DO UPDATE SET
                    display_name = excluded.display_name,
                    last_seen_utc = excluded.last_seen_utc;
                """;
            categoryCommand.Parameters.AddWithValue("@category_id", state.CategoryId);
            categoryCommand.Parameters.AddWithValue(
                "@category_name",
                state.CategoryName ?? state.CategoryId);
            categoryCommand.Parameters.AddWithValue(
                "@observed_utc",
                SqliteData.ToDatabaseTimestamp(state.ObservedAt));
            await categoryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO channel_context(
                broadcaster_id, broadcaster_login, broadcaster_name,
                moderator_user_id, category_id, category_name, state_observed_utc,
                revision, is_verified)
            VALUES(
                @broadcaster_id, @broadcaster_login, @broadcaster_name,
                @moderator_user_id, @category_id, @category_name, @observed_utc,
                @revision, @is_verified)
            ON CONFLICT(broadcaster_id) DO UPDATE SET
                broadcaster_login = excluded.broadcaster_login,
                broadcaster_name = excluded.broadcaster_name,
                moderator_user_id = excluded.moderator_user_id,
                category_id = excluded.category_id,
                category_name = excluded.category_name,
                state_observed_utc = excluded.state_observed_utc,
                revision = CASE
                    WHEN channel_context.category_id IS excluded.category_id
                     AND channel_context.category_name IS excluded.category_name
                    THEN channel_context.revision
                    ELSE channel_context.revision + 1
                END,
                is_verified = excluded.is_verified;
            """;
        command.Parameters.AddWithValue("@broadcaster_id", state.BroadcasterId);
        command.Parameters.AddWithValue("@broadcaster_login", state.BroadcasterLogin);
        command.Parameters.AddWithValue("@broadcaster_name", state.BroadcasterName);
        command.Parameters.AddWithValue("@moderator_user_id", state.ModeratorUserId);
        command.Parameters.AddWithValue("@category_id", SqliteData.DbValue(state.CategoryId));
        command.Parameters.AddWithValue("@category_name", SqliteData.DbValue(state.CategoryName));
        command.Parameters.AddWithValue("@observed_utc", SqliteData.ToDatabaseTimestamp(state.ObservedAt));
        command.Parameters.AddWithValue("@revision", state.Revision);
        command.Parameters.AddWithValue("@is_verified", state.IsVerified ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChannelStateRecord?> GetAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT broadcaster_id, broadcaster_login, broadcaster_name,
                   moderator_user_id, category_id, category_name, state_observed_utc,
                   revision, is_verified
            FROM channel_context
            WHERE broadcaster_id = @broadcaster_id;
            """;
        command.Parameters.AddWithValue("@broadcaster_id", broadcasterId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ChannelStateRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            SqliteData.GetDateTimeOffset(reader, 6),
            reader.GetInt64(7),
            reader.GetBoolean(8));
    }
}
