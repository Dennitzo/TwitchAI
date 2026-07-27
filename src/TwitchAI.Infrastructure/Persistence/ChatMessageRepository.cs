using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace TwitchAI.Infrastructure.Persistence;

public interface IChatMessageRepository
{
    Task<bool> TryAddAsync(ChatMessageRecord message, CancellationToken cancellationToken = default);

    Task<ChatMessageRecord?> FindAsync(
        string broadcasterId,
        string twitchMessageId,
        CancellationToken cancellationToken = default);
}

public sealed class ChatMessageRepository : IChatMessageRepository
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;

    public ChatMessageRepository(
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task<bool> TryAddAsync(
        ChatMessageRecord message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        if (message.CategoryId is not null)
        {
            await UpsertCategoryAsync(connection, transaction, message, cancellationToken)
                .ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO chat_message_snapshots(
                twitch_message_id, eventsub_notification_id,
                broadcaster_id, chatter_user_id,
                chatter_login_original, chatter_name_original,
                message_text_original, lower_text, message_text_normalized,
                normalization_version, original_sha256, normalized_sha256,
                identity_fingerprint, category_id, category_name_snapshot,
                channel_context_revision, category_is_verified, ingress_sequence,
                shared_source_channel_id, badges_json, received_utc)
            VALUES(
                @message_id, @eventsub_notification_id,
                @broadcaster_id, @chatter_user_id,
                @chatter_login, @chatter_name,
                @message_original, @lower_text, @message_normalized,
                @normalization_version, @original_sha256, @normalized_sha256,
                @identity_fingerprint, @category_id, @category_name,
                @channel_context_revision, @category_is_verified, @ingress_sequence,
                @shared_source_channel_id, @badges_json, @received_utc);
            """;
        command.Parameters.AddWithValue("@message_id", message.TwitchMessageId);
        command.Parameters.AddWithValue(
            "@eventsub_notification_id",
            message.EventSubNotificationId ?? message.TwitchMessageId);
        command.Parameters.AddWithValue("@broadcaster_id", message.BroadcasterId);
        command.Parameters.AddWithValue("@chatter_user_id", message.ChatterUserId);
        command.Parameters.AddWithValue("@chatter_login", message.ChatterLoginOriginal);
        command.Parameters.AddWithValue("@chatter_name", message.ChatterNameOriginal);
        command.Parameters.AddWithValue("@message_original", message.MessageTextOriginal);
        command.Parameters.AddWithValue(
            "@lower_text",
            message.LowerText ?? message.MessageTextOriginal.ToLowerInvariant());
        command.Parameters.AddWithValue("@message_normalized", message.MessageTextNormalized);
        command.Parameters.AddWithValue("@normalization_version", message.NormalizationVersion);
        command.Parameters.AddWithValue(
            "@original_sha256",
            message.OriginalSha256 ?? ComputeSha256(message.MessageTextOriginal));
        command.Parameters.AddWithValue("@normalized_sha256", message.NormalizedSha256);
        command.Parameters.AddWithValue(
            "@identity_fingerprint",
            message.IdentityFingerprint ?? message.TwitchMessageId);
        command.Parameters.AddWithValue("@category_id", SqliteData.DbValue(message.CategoryId));
        command.Parameters.AddWithValue(
            "@category_name",
            SqliteData.DbValue(message.CategoryNameSnapshot));
        command.Parameters.AddWithValue(
            "@channel_context_revision",
            message.ChannelContextRevision);
        command.Parameters.AddWithValue(
            "@category_is_verified",
            message.CategoryIsVerified ? 1 : 0);
        command.Parameters.AddWithValue("@ingress_sequence", message.IngressSequence);
        command.Parameters.AddWithValue(
            "@shared_source_channel_id",
            SqliteData.DbValue(message.SharedSourceChannelId));
        command.Parameters.AddWithValue("@badges_json", message.BadgesJson);
        command.Parameters.AddWithValue("@received_utc", SqliteData.ToDatabaseTimestamp(message.ReceivedAt));

        var inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return inserted;
    }

    public async Task<ChatMessageRecord?> FindAsync(
        string broadcasterId,
        string twitchMessageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(twitchMessageId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT twitch_message_id, broadcaster_id, chatter_user_id,
                   chatter_login_original, chatter_name_original,
                   message_text_original, message_text_normalized,
                   normalization_version, normalized_sha256,
                   category_id, category_name_snapshot, received_utc
            FROM chat_message_snapshots
            WHERE broadcaster_id = @broadcaster_id
              AND twitch_message_id = @message_id;
            """;
        command.Parameters.AddWithValue("@broadcaster_id", broadcasterId);
        command.Parameters.AddWithValue("@message_id", twitchMessageId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ChatMessageRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            SqliteData.GetDateTimeOffset(reader, 11));
    }

    private static async Task UpsertCategoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessageRecord message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO twitch_categories(
                category_id, display_name, last_seen_utc, last_verified_utc)
            VALUES(@category_id, @display_name, @last_seen_utc, NULL)
            ON CONFLICT(category_id) DO UPDATE SET
                display_name = excluded.display_name,
                last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("@category_id", message.CategoryId!);
        command.Parameters.AddWithValue(
            "@display_name",
            message.CategoryNameSnapshot ?? message.CategoryId!);
        command.Parameters.AddWithValue("@last_seen_utc", SqliteData.ToDatabaseTimestamp(message.ReceivedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeSha256(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
