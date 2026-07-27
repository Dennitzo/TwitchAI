using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.Infrastructure.Persistence;

public interface IMessageAvailabilityStore
{
    Task RecordDeletedAsync(
        TwitchMessageDeletedEvent deleted,
        CancellationToken cancellationToken = default);

    Task RecordUserClearAsync(
        TwitchUserMessagesClearedEvent cleared,
        CancellationToken cancellationToken = default);

    Task RecordRoomClearAsync(
        TwitchRoomClearedEvent cleared,
        CancellationToken cancellationToken = default);

    Task RecordGapAsync(
        TwitchEventSubGapEvent gap,
        CancellationToken cancellationToken = default);

    Task RecordOfflineGapForPendingCasesAsync(
        DateTimeOffset throughReceivedAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    Task<MessageAvailabilityAssessment> AssessAsync(
        string channelId,
        string twitchMessageId,
        string twitchUserId,
        DateTimeOffset messageReceivedAt,
        CancellationToken cancellationToken = default);

    Task<bool> AcceptExternalDeleteFeedbackAsync(
        string eventSubMessageId,
        long caseId,
        string triggerPhrase,
        PolicySetCode policySet,
        CancellationToken cancellationToken = default);

    Task<int> DeleteUnlinkedOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}

public sealed class SqliteMessageAvailabilityStore : IMessageAvailabilityStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly IChatTextNormalizer _normalizer;

    public SqliteMessageAvailabilityStore(
        ISqliteConnectionFactory connectionFactory,
        IChatTextNormalizer normalizer,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _normalizer = normalizer;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task RecordDeletedAsync(
        TwitchMessageDeletedEvent deleted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deleted);
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        var caseId = await FindCaseIdAsync(
            connection,
            transaction,
            deleted.BroadcasterId,
            deleted.MessageId,
            cancellationToken).ConfigureAwait(false);
        var correlation = await ClassifyDeleteCorrelationAsync(
            connection,
            transaction,
            deleted.BroadcasterId,
            deleted.MessageId,
            caseId,
            cancellationToken).ConfigureAwait(false);
        var learningState =
            correlation == "external_unmatched" && caseId.HasValue
                ? "pending_review"
                : "unavailable";

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO message_availability_events(
                    eventsub_message_id, broadcaster_id, twitch_message_id,
                    target_user_id, availability, occurred_utc, received_utc,
                    correlation_kind, learning_state, case_id)
                VALUES(
                    @event_id, @broadcaster_id, @message_id,
                    @target_user_id, 'deleted_exact', @occurred_utc, @received_utc,
                    @correlation, @learning_state, @case_id);
                """;
            command.Parameters.AddWithValue("@event_id", deleted.EventMessageId);
            command.Parameters.AddWithValue("@broadcaster_id", deleted.BroadcasterId);
            command.Parameters.AddWithValue("@message_id", deleted.MessageId);
            command.Parameters.AddWithValue("@target_user_id", deleted.TargetUserId);
            command.Parameters.AddWithValue(
                "@occurred_utc",
                SqliteData.ToDatabaseTimestamp(deleted.ObservedAt));
            command.Parameters.AddWithValue(
                "@received_utc",
                SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("@correlation", correlation);
            command.Parameters.AddWithValue("@learning_state", learningState);
            command.Parameters.AddWithValue("@case_id", SqliteData.DbValue(caseId));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // The availability tombstone is authoritative, but the moderation case stays
        // pending until the operator clicks Execute. The executor then observes this
        // event, performs the row-version guarded transition and opens the explicit
        // learn/without-learning review. Advancing the case here would make that
        // human decision unreachable and would invalidate the UI's expected version.

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteUnlinkedOlderThanAsync(
        DateTimeOffset cutoff,
        CancellationToken cancellationToken = default)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var events = connection.CreateCommand();
        events.Transaction = transaction;
        events.CommandText =
            """
            DELETE FROM message_availability_events
            WHERE case_id IS NULL
              AND received_utc < @cutoff;
            """;
        events.Parameters.AddWithValue(
            "@cutoff",
            SqliteData.ToDatabaseTimestamp(cutoff));
        var deleted = await events.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var watermarks = connection.CreateCommand();
        watermarks.Transaction = transaction;
        watermarks.CommandText =
            """
            DELETE FROM message_clear_watermarks
            WHERE cleared_utc < @cutoff;
            """;
        watermarks.Parameters.AddWithValue(
            "@cutoff",
            SqliteData.ToDatabaseTimestamp(cutoff));
        deleted += await watermarks.ExecuteNonQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    public Task RecordUserClearAsync(
        TwitchUserMessagesClearedEvent cleared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cleared);
        return RecordClearAsync(
            cleared.EventMessageId,
            cleared.BroadcasterId,
            cleared.TargetUserId,
            "user",
            "cleared_for_user",
            cleared.ObservedAt,
            cancellationToken);
    }

    public Task RecordRoomClearAsync(
        TwitchRoomClearedEvent cleared,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cleared);
        return RecordClearAsync(
            cleared.EventMessageId,
            cleared.BroadcasterId,
            null,
            "room",
            "room_cleared_hidden",
            cleared.ObservedAt,
            cancellationToken);
    }

    public async Task RecordGapAsync(
        TwitchEventSubGapEvent gap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gap);
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT OR IGNORE INTO message_availability_events(
                eventsub_message_id, broadcaster_id, twitch_message_id,
                target_user_id, availability, occurred_utc, received_utc,
                correlation_kind, learning_state, case_id)
            VALUES(
                @event_id, @broadcaster_id, NULL,
                NULL, 'unknown_after_event_gap', @occurred_utc, @received_utc,
                'external_unmatched', 'unavailable', NULL);
            """;
        command.Parameters.AddWithValue("@event_id", gap.EventMessageId);
        command.Parameters.AddWithValue("@broadcaster_id", gap.BroadcasterId);
        command.Parameters.AddWithValue(
            "@occurred_utc",
            SqliteData.ToDatabaseTimestamp(gap.ObservedAt));
        command.Parameters.AddWithValue(
            "@received_utc",
            SqliteData.ToDatabaseTimestamp(gap.ThroughReceivedAt));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordOfflineGapForPendingCasesAsync(
        DateTimeOffset throughReceivedAtUtc,
        CancellationToken cancellationToken = default)
    {
        List<string> channelIds = [];
        await using (var connection = await _connectionFactory
                         .OpenConnectionAsync(cancellationToken)
                         .ConfigureAwait(false))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT DISTINCT broadcaster_id
                FROM moderation_cases
                WHERE status = 'pending';
                """;
            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                channelIds.Add(reader.GetString(0));
            }
        }

        foreach (var channelId in channelIds)
        {
            await RecordGapAsync(
                    new TwitchEventSubGapEvent(
                        $"local-offline-gap:{Guid.NewGuid():N}",
                        channelId,
                        throughReceivedAtUtc,
                        throughReceivedAtUtc,
                        "application_restart"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task<MessageAvailabilityAssessment> AssessAsync(
        string channelId,
        string twitchMessageId,
        string twitchUserId,
        DateTimeOffset messageReceivedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(twitchMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(twitchUserId);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT availability, received_utc
                FROM message_availability_events
                WHERE broadcaster_id = @channel_id
                  AND twitch_message_id = @message_id
                ORDER BY received_utc DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@channel_id", channelId);
            command.Parameters.AddWithValue("@message_id", twitchMessageId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new MessageAvailabilityAssessment(
                    ParseAvailability(reader.GetString(0)),
                    SqliteData.GetDateTimeOffset(reader, 1),
                    "persisted-exact-event",
                    1);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT availability, cleared_utc, revision
                FROM message_clear_watermarks
                WHERE broadcaster_id = @channel_id
                  AND (clear_kind = 'room' OR
                       (clear_kind = 'user' AND target_user_id = @user_id))
                  AND cleared_utc >= @message_received_utc
                ORDER BY cleared_utc DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@channel_id", channelId);
            command.Parameters.AddWithValue("@user_id", twitchUserId);
            command.Parameters.AddWithValue(
                "@message_received_utc",
                SqliteData.ToDatabaseTimestamp(messageReceivedAt));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return new MessageAvailabilityAssessment(
                    ParseAvailability(reader.GetString(0)),
                    SqliteData.GetDateTimeOffset(reader, 1),
                    "persisted-clear-watermark",
                    reader.GetInt64(2));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT received_utc
                FROM message_availability_events
                WHERE broadcaster_id = @channel_id
                  AND twitch_message_id IS NULL
                  AND availability = 'unknown_after_event_gap'
                  AND received_utc >= @message_received_utc
                ORDER BY received_utc DESC
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@channel_id", channelId);
            command.Parameters.AddWithValue(
                "@message_received_utc",
                SqliteData.ToDatabaseTimestamp(messageReceivedAt));
            var result = await command.ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (result is string timestamp)
            {
                return new MessageAvailabilityAssessment(
                    MessageAvailability.UnknownAfterEventGap,
                    SqliteData.ParseDateTimeOffset(timestamp),
                    "persisted-eventsub-gap",
                    1);
            }
        }

        var now = DateTimeOffset.UtcNow;
        return now - messageReceivedAt > TimeSpan.FromHours(6)
            ? new MessageAvailabilityAssessment(
                MessageAvailability.ExpiredNotDeletable,
                now,
                "twitch-delete-window-expired",
                1)
            : new MessageAvailabilityAssessment(
                MessageAvailability.ObservedActive,
                now,
                "no-unavailability-evidence",
                1);
    }

    public async Task<bool> AcceptExternalDeleteFeedbackAsync(
        string eventSubMessageId,
        long caseId,
        string triggerPhrase,
        PolicySetCode policySet,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSubMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerPhrase);
        var normalizedPhrase = _normalizer.Normalize(triggerPhrase);

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        string? categoryId;
        string messageNormalized;
        string? violationRuleCode;
        string storedPolicySet;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT c.category_id, c.message_text_normalized,
                       c.violation_rule_code, c.policy_set
                FROM moderation_cases c
                INNER JOIN message_availability_events e ON e.case_id = c.case_id
                WHERE c.case_id = @case_id
                  AND e.eventsub_message_id = @event_id
                  AND e.learning_state = 'pending_review';
                """;
            read.Parameters.AddWithValue("@case_id", caseId);
            read.Parameters.AddWithValue("@event_id", eventSubMessageId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }

            categoryId = reader.IsDBNull(0) ? null : reader.GetString(0);
            messageNormalized = reader.GetString(1);
            violationRuleCode = reader.IsDBNull(2) ? null : reader.GetString(2);
            storedPolicySet = reader.GetString(3);
        }

        var requestedPolicySet = ToDatabasePolicySet(policySet);
        if (string.IsNullOrWhiteSpace(violationRuleCode)
            || !string.Equals(
                storedPolicySet,
                requestedPolicySet,
                StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        var eligible = ModerationRuleCodes.AllowsTriggerLearning(violationRuleCode)
            && await IncrementPhraseConfirmationAsync(
            connection,
            transaction,
            normalizedPhrase,
            requestedPolicySet,
            violationRuleCode,
            cancellationToken).ConfigureAwait(false);

        if (ModerationRuleCodes.AllowsTriggerLearning(violationRuleCode))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO phrase_feedback(
                    case_id, source_kind, source_event_id, label_action,
                    rule_code, trigger_phrase, policy_set, is_positive,
                    category_id, message_normalized, eligible, confirmed_utc)
                VALUES(
                    @case_id, 'external_delete', @event_id, 'delete',
                    @rule_code, @trigger_phrase, @policy_set, 1,
                    @category_id, @message_normalized, @eligible, @confirmed_utc);
                """;
            insert.Parameters.AddWithValue("@case_id", caseId);
            insert.Parameters.AddWithValue("@event_id", eventSubMessageId);
            insert.Parameters.AddWithValue("@rule_code", violationRuleCode);
            insert.Parameters.AddWithValue("@trigger_phrase", normalizedPhrase);
            insert.Parameters.AddWithValue("@policy_set", requestedPolicySet);
            insert.Parameters.AddWithValue("@category_id", SqliteData.DbValue(categoryId));
            insert.Parameters.AddWithValue("@message_normalized", messageNormalized);
            insert.Parameters.AddWithValue("@eligible", eligible ? 1 : 0);
            insert.Parameters.AddWithValue(
                "@confirmed_utc",
                SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE message_availability_events
                SET learning_state = 'accepted'
                WHERE eventsub_message_id = @event_id
                  AND learning_state = 'pending_review';
                """;
            update.Parameters.AddWithValue("@event_id", eventSubMessageId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task RecordClearAsync(
        string eventId,
        string broadcasterId,
        string? targetUserId,
        string clearKind,
        string availability,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow;

        await using (var insertEvent = connection.CreateCommand())
        {
            insertEvent.Transaction = transaction;
            insertEvent.CommandText =
                """
                INSERT OR IGNORE INTO message_availability_events(
                    eventsub_message_id, broadcaster_id, twitch_message_id,
                    target_user_id, availability, occurred_utc, received_utc,
                    correlation_kind, learning_state, case_id)
                VALUES(
                    @event_id, @broadcaster_id, NULL,
                    @target_user_id, @availability, @occurred_utc, @received_utc,
                    'external_unmatched', 'unavailable', NULL);
                """;
            insertEvent.Parameters.AddWithValue("@event_id", eventId);
            insertEvent.Parameters.AddWithValue("@broadcaster_id", broadcasterId);
            insertEvent.Parameters.AddWithValue(
                "@target_user_id",
                SqliteData.DbValue(targetUserId));
            insertEvent.Parameters.AddWithValue("@availability", availability);
            insertEvent.Parameters.AddWithValue(
                "@occurred_utc",
                SqliteData.ToDatabaseTimestamp(occurredAt));
            insertEvent.Parameters.AddWithValue(
                "@received_utc",
                SqliteData.ToDatabaseTimestamp(now));
            await insertEvent.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var deleteOld = connection.CreateCommand())
        {
            deleteOld.Transaction = transaction;
            deleteOld.CommandText =
                """
                DELETE FROM message_clear_watermarks
                WHERE broadcaster_id = @broadcaster_id
                  AND clear_kind = @clear_kind
                  AND target_user_id IS @target_user_id;
                """;
            deleteOld.Parameters.AddWithValue("@broadcaster_id", broadcasterId);
            deleteOld.Parameters.AddWithValue("@clear_kind", clearKind);
            deleteOld.Parameters.AddWithValue(
                "@target_user_id",
                SqliteData.DbValue(targetUserId));
            await deleteOld.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var insertWatermark = connection.CreateCommand())
        {
            insertWatermark.Transaction = transaction;
            insertWatermark.CommandText =
                """
                INSERT INTO message_clear_watermarks(
                    broadcaster_id, target_user_id, clear_kind, cleared_utc,
                    availability, revision, eventsub_message_id)
                VALUES(
                    @broadcaster_id, @target_user_id, @clear_kind, @cleared_utc,
                    @availability, 1, @event_id);
                """;
            insertWatermark.Parameters.AddWithValue("@broadcaster_id", broadcasterId);
            insertWatermark.Parameters.AddWithValue(
                "@target_user_id",
                SqliteData.DbValue(targetUserId));
            insertWatermark.Parameters.AddWithValue("@clear_kind", clearKind);
            insertWatermark.Parameters.AddWithValue(
                "@cleared_utc",
                SqliteData.ToDatabaseTimestamp(occurredAt));
            insertWatermark.Parameters.AddWithValue("@availability", availability);
            insertWatermark.Parameters.AddWithValue("@event_id", eventId);
            await insertWatermark.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long?> FindCaseIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string broadcasterId,
        string twitchMessageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT case_id
            FROM moderation_cases
            WHERE broadcaster_id = @broadcaster_id
              AND twitch_message_id = @message_id;
            """;
        command.Parameters.AddWithValue("@broadcaster_id", broadcasterId);
        command.Parameters.AddWithValue("@message_id", twitchMessageId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<string> ClassifyDeleteCorrelationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string broadcasterId,
        string twitchMessageId,
        long? caseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT status
            FROM action_attempts
            WHERE broadcaster_id = @broadcaster_id
              AND twitch_message_id = @message_id
              AND action = 'delete'
            ORDER BY execution_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@broadcaster_id", broadcasterId);
        command.Parameters.AddWithValue("@message_id", twitchMessageId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is string status)
        {
            return status == "outcome_unknown" ? "local_or_ambiguous" : "local";
        }

        return caseId.HasValue ? "external_unmatched" : "orphan";
    }

    private static async Task<bool> IncrementPhraseConfirmationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedPhrase,
        string policySet,
        string ruleCode,
        CancellationToken cancellationToken)
    {
        long? phraseId = null;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT p.phrase_id
                FROM phrase_rules p
                INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE p.normalized_phrase = @phrase
                  AND s.set_code = @policy_set
                  AND r.rule_code = @rule_code
                  AND s.broadcaster_id = @global_scope
                  AND s.enabled = 1
                  AND s.archived_utc IS NULL
                  AND p.archived_utc IS NULL
                  AND r.enabled = 1
                  AND r.archived_utc IS NULL
                LIMIT 1;
                """;
            read.Parameters.AddWithValue("@phrase", normalizedPhrase);
            read.Parameters.AddWithValue("@policy_set", policySet);
            read.Parameters.AddWithValue("@rule_code", ruleCode);
            read.Parameters.AddWithValue(
                "@global_scope",
                GlobalPolicyStorage.BroadcasterId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                phraseId = reader.GetInt64(0);
            }
        }

        if (phraseId.HasValue)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE phrase_rules
                SET positive_confirmations = positive_confirmations + 1,
                    row_version = row_version + 1,
                    updated_utc = @updated_utc
                WHERE phrase_id = @phrase_id;
                """;
            update.Parameters.AddWithValue(
                "@updated_utc",
                SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("@phrase_id", phraseId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return phraseId.HasValue;
    }

    private static MessageAvailability ParseAvailability(string value)
    {
        return value switch
        {
            "deleted_exact" => MessageAvailability.DeletedExact,
            "cleared_for_user" => MessageAvailability.ClearedForUser,
            "room_cleared_hidden" => MessageAvailability.RoomClearedHidden,
            "outcome_unknown" => MessageAvailability.OutcomeUnknown,
            _ => MessageAvailability.UnknownAfterEventGap,
        };
    }

    private static string ToDatabasePolicySet(PolicySetCode policySet)
    {
        return policySet switch
        {
            PolicySetCode.General => "general",
            PolicySetCode.Backseatgaming => "backseatgaming",
            _ => throw new ArgumentOutOfRangeException(nameof(policySet), policySet, null),
        };
    }
}

internal sealed class AvailabilityRetentionHostedService(
    IMessageAvailabilityStore availabilityStore)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await availabilityStore.DeleteUnlinkedOlderThanAsync(
                    DateTimeOffset.UtcNow - TimeSpan.FromHours(24),
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (SqliteException)
            {
                // A later cycle retries cleanup. Moderation safety never depends
                // on deleting historical tombstones early.
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }
}
