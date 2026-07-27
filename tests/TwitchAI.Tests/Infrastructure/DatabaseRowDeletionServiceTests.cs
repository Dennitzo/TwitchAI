using System.Globalization;
using Microsoft.Data.Sqlite;
using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class DatabaseRowDeletionServiceTests
{
    [Fact]
    public async Task Delete_ClosedCasePackage_RemovesFkGraphAndRevertsExactFeedbackCounters()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await InsertClosedCasePackageAsync(database);
        var invalidator = new RecordingPolicyCacheInvalidator();
        var service = new DatabaseRowDeletionService(
            database.ConnectionFactory,
            cacheInvalidators: [invalidator]);
        var request = new DatabaseRowDeletionRequest(
            "moderation_cases",
            fixture.CaseId.ToString(CultureInfo.InvariantCulture));

        var preview = await service.PreviewAsync(request);

        Assert.True(preview.CanDelete);
        Assert.NotNull(preview.ConfirmationToken);
        Assert.Contains(
            preview.Impacts,
            impact => impact.TableName == "phrase_feedback" && impact.RowCount == 3);
        Assert.Contains(
            preview.Impacts,
            impact => impact.TableName == "chat_message_snapshots" && impact.RowCount == 1);

        var result = await service.DeleteAsync(request, preview.ConfirmationToken);

        Assert.Equal(2, result.PositiveFeedbackReverted);
        Assert.Equal(1, result.NegativeFeedbackReverted);
        var invalidation = Assert.Single(invalidator.Changes);
        Assert.Equal(GlobalPolicyStorage.BroadcasterId, invalidation.BroadcasterId);
        Assert.Null(invalidation.ScopeKind);
        Assert.Null(invalidation.CategoryId);
        Assert.False(invalidation.AllowlistChanged);
        Assert.Null(invalidation.NewRevision);
        await using var connection = await database.OpenConnectionAsync();
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                $"SELECT COUNT(*) FROM moderation_cases WHERE case_id = {fixture.CaseId};"));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                $"SELECT COUNT(*) FROM phrase_feedback WHERE case_id = {fixture.CaseId};"));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                $"SELECT COUNT(*) FROM action_attempts WHERE case_id = {fixture.CaseId};"));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                connection,
                $"""
                 SELECT COUNT(*)
                 FROM chat_message_snapshots
                 WHERE twitch_message_id = '{fixture.MessageId}';
                 """));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                connection,
                $"""
                 SELECT COUNT(*)
                 FROM processed_eventsub_events
                 WHERE eventsub_message_id = '{fixture.EventSubId}';
                 """));
        Assert.Equal(
            "3|3",
            await ScalarAsync<string>(
                connection,
                """
                SELECT positive_confirmations || '|' || negative_confirmations
                FROM phrase_rules
                WHERE normalized_phrase = 'counter rollback phrase';
                """));

        await using var audit = connection.CreateCommand();
        audit.CommandText =
            """
            SELECT
                target_table, target_id_sha256, deleted_counts_json,
                positive_feedback_reverted, negative_feedback_reverted
            FROM data_deletion_audit;
            """;
        await using var reader = await audit.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("moderation_cases", reader.GetString(0));
        Assert.Equal(64, reader.GetString(1).Length);
        Assert.NotEqual(
            fixture.CaseId.ToString(CultureInfo.InvariantCulture),
            reader.GetString(1));
        Assert.DoesNotContain(fixture.MessageId, reader.GetString(2), StringComparison.Ordinal);
        Assert.Equal(2, reader.GetInt64(3));
        Assert.Equal(1, reader.GetInt64(4));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.DeleteAsync(request, preview.ConfirmationToken));
    }

    [Fact]
    public async Task Preview_BlocksOpenCasesProtectedTablesAndYoungOperationalLogs()
    {
        await using var database = await TestDatabase.CreateAsync();
        var openCaseId = await InsertOpenCaseAsync(database);
        var service = new DatabaseRowDeletionService(database.ConnectionFactory);

        var openCase = await service.PreviewAsync(
            new DatabaseRowDeletionRequest(
                "moderation_cases",
                openCaseId.ToString(CultureInfo.InvariantCulture)));
        var protectedConfiguration = await service.PreviewAsync(
            new DatabaseRowDeletionRequest("blacklist_rules", "1"));
        var standaloneAnnotation = await service.PreviewAsync(
            new DatabaseRowDeletionRequest("record_annotations", "1"));

        await using (var connection = await database.OpenConnectionAsync())
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO processed_eventsub_events(
                    eventsub_message_id, subscription_type, received_utc)
                VALUES(
                    'recent-event', 'channel.chat.message', @now);
                """;
            insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }

        var recentEvent = await service.PreviewAsync(
            new DatabaseRowDeletionRequest(
                "processed_eventsub_events",
                "recent-event"));

        Assert.False(openCase.CanDelete);
        Assert.Contains(
            "abgeschlossene",
            openCase.BlockReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(protectedConfiguration.CanDelete);
        Assert.False(standaloneAnnotation.CanDelete);
        Assert.False(recentEvent.CanDelete);
        Assert.Contains(
            "24 Stunden",
            recentEvent.BlockReason,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_LearningFeedbackWithoutPhraseFeedback_RevisionsAndInvalidatesScope()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await InsertClosedCasePackageAsync(database);
        long revisionBefore;
        await using (var connection = await database.OpenConnectionAsync())
        {
            await using (var removePhraseFeedback = connection.CreateCommand())
            {
                removePhraseFeedback.CommandText =
                    """
                    DELETE FROM phrase_feedback
                    WHERE case_id = @case_id;

                    UPDATE moderation_feedback
                    SET rule_code = NULL
                    WHERE case_id = @case_id;
                    """;
                removePhraseFeedback.Parameters.AddWithValue("@case_id", fixture.CaseId);
                await removePhraseFeedback.ExecuteNonQueryAsync();
            }

            revisionBefore = await ScalarAsync<long>(
                connection,
                """
                SELECT revision
                FROM policy_sets
                WHERE broadcaster_id = '*'
                  AND set_code = 'general'
                  AND archived_utc IS NULL;
                """);
        }

        var invalidator = new RecordingPolicyCacheInvalidator();
        var service = new DatabaseRowDeletionService(
            database.ConnectionFactory,
            cacheInvalidators: [invalidator]);
        var request = new DatabaseRowDeletionRequest(
            "moderation_cases",
            fixture.CaseId.ToString(CultureInfo.InvariantCulture));
        var preview = await service.PreviewAsync(request);

        var result = await service.DeleteAsync(request, preview.ConfirmationToken!);

        Assert.Equal(0, result.PositiveFeedbackReverted);
        Assert.Equal(0, result.NegativeFeedbackReverted);
        Assert.DoesNotContain(
            result.DeletedRows,
            impact => impact.TableName == "phrase_feedback");
        Assert.Single(invalidator.Changes);
        await using var verify = await database.OpenConnectionAsync();
        Assert.Equal(
            revisionBefore + 1,
            await ScalarAsync<long>(
                verify,
                """
                SELECT revision
                FROM policy_sets
                WHERE broadcaster_id = '*'
                  AND set_code = 'general'
                  AND archived_utc IS NULL;
                """));
        Assert.Equal(
            "5|4",
            await ScalarAsync<string>(
                verify,
                """
                SELECT positive_confirmations || '|' || negative_confirmations
                FROM phrase_rules
                WHERE normalized_phrase = 'counter rollback phrase';
                """));
    }

    [Fact]
    public async Task Delete_OperationalLog_RequiresFreshPreviewAndPersistsOnlyHashedTarget()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO ai_run_metrics(
                    correlation_id, model_id, policy_hash, message_count,
                    prompt_tokens, completion_tokens, total_tokens,
                    elapsed_ms, succeeded, requested_utc)
                VALUES(
                    'old-run', 'model', 'policy', 1,
                    10, 2, 12,
                    20, 1, @old);
                """;
            insert.Parameters.AddWithValue(
                "@old",
                DateTimeOffset.UtcNow.Subtract(TimeSpan.FromHours(25)).ToString("O"));
            await insert.ExecuteNonQueryAsync();
        }

        var service = new DatabaseRowDeletionService(database.ConnectionFactory);
        var request = new DatabaseRowDeletionRequest("ai_run_metrics", "1");
        var stalePreview = await service.PreviewAsync(request);
        Assert.True(stalePreview.CanDelete);

        await using (var connection = await database.OpenConnectionAsync())
        await using (var mutate = connection.CreateCommand())
        {
            mutate.CommandText =
                "UPDATE ai_run_metrics SET elapsed_ms = elapsed_ms + 1 WHERE request_id = 1;";
            await mutate.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteAsync(request, stalePreview.ConfirmationToken!));

        var currentPreview = await service.PreviewAsync(request);
        var deleted = await service.DeleteAsync(request, currentPreview.ConfirmationToken!);
        Assert.Equal(
            new DatabaseRowDeletionImpact("ai_run_metrics", 1),
            Assert.Single(deleted.DeletedRows));

        await using var verify = await database.OpenConnectionAsync();
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                verify,
                "SELECT COUNT(*) FROM ai_run_metrics WHERE request_id = 1;"));
        var targetHash = await ScalarAsync<string>(
            verify,
            """
            SELECT target_id_sha256
            FROM data_deletion_audit
            WHERE target_table = 'ai_run_metrics';
            """);
        Assert.Equal(64, targetHash.Length);
        Assert.NotEqual("1", targetHash);
    }

    private static async Task<ClosedCaseFixture> InsertClosedCasePackageAsync(
        TestDatabase database)
    {
        const string messageId = "delete-package-message";
        const string eventSubId = "delete-package-event";
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();

        await using (var phrase = connection.CreateCommand())
        {
            phrase.Transaction = transaction;
            phrase.CommandText =
                """
                INSERT INTO phrase_rules(
                    rule_id, phrase, normalized_phrase, enabled,
                    manual_override, positive_confirmations, negative_confirmations,
                    row_version, created_utc, updated_utc)
                SELECT
                    r.rule_id, 'counter rollback phrase', 'counter rollback phrase', 1,
                    'auto', 5, 4,
                    1, @now, @now
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND r.rule_code = 'G_OTHER_ABUSE'
                  AND r.archived_utc IS NULL;
                """;
            phrase.Parameters.AddWithValue("@now", now);
            await phrase.ExecuteNonQueryAsync();
        }

        await InsertMessageAsync(
            connection,
            transaction,
            messageId,
            eventSubId,
            now);

        long caseId;
        await using (var moderationCase = connection.CreateCommand())
        {
            moderationCase.Transaction = transaction;
            moderationCase.CommandText =
                """
                INSERT INTO moderation_cases(
                    twitch_message_id, broadcaster_id, chatter_user_id,
                    chatter_name_snapshot, message_text_original,
                    message_text_normalized, effective_policy_hash,
                    policy_revisions_json, message_snapshot_json,
                    model_id, envelope_json, proposed_action,
                    proposed_timeout_seconds, violation_rule_code,
                    reason, policy_set, trigger_phrases_json,
                    confidence, status, row_version, created_utc, resolved_utc)
                VALUES(
                    @message_id, 'channel-a', 'user-a',
                    'User A', 'Counter rollback phrase',
                    'counter rollback phrase', 'policy',
                    '{}', '{}',
                    'model', '{}', 'timeout',
                    600, 'G_OTHER_ABUSE',
                    'Test', 'general', '["counter rollback phrase"]',
                    0.9, 'executed', 2, @now, @now);
                SELECT last_insert_rowid();
                """;
            moderationCase.Parameters.AddWithValue("@message_id", messageId);
            moderationCase.Parameters.AddWithValue("@now", now);
            caseId = Convert.ToInt64(
                await moderationCase.ExecuteScalarAsync(),
                CultureInfo.InvariantCulture);
        }

        await using (var feedback = connection.CreateCommand())
        {
            feedback.Transaction = transaction;
            feedback.CommandText =
                """
                INSERT INTO moderation_feedback(
                    case_id, operator_decision, actual_action,
                    actual_timeout_seconds, actual_reason,
                    eligible_for_learning, human_approved, twitch_executed,
                    learning_accepted, decided_utc, review_completed, rule_code)
                VALUES(
                    @case_id, 'executed', 'timeout',
                    600, 'Test',
                    1, 1, 1,
                    1, @now, 1, 'G_OTHER_ABUSE');

                INSERT INTO phrase_feedback(
                    case_id, source_kind, source_event_id, label_action,
                    rule_code, trigger_phrase, policy_set, is_positive,
                    message_normalized, eligible, confirmed_utc)
                VALUES
                    (@case_id, 'operator_execute', 'delete-feedback-positive-1',
                     'timeout', 'G_OTHER_ABUSE', 'counter rollback phrase',
                     'general', 1, 'counter rollback phrase', 1, @now),
                    (@case_id, 'operator_execute', 'delete-feedback-positive-2',
                     'timeout', 'G_OTHER_ABUSE', 'counter rollback phrase',
                     'general', 1, 'counter rollback phrase', 1, @now),
                    (@case_id, 'operator_discard', 'delete-feedback-negative-1',
                     'none', 'G_OTHER_ABUSE', 'counter rollback phrase',
                     'general', 0, 'counter rollback phrase', 1, @now);

                INSERT INTO action_attempts(
                    case_id, broadcaster_id, twitch_message_id,
                    target_user_id, action, timeout_seconds, reason,
                    status, requested_utc, completed_utc, http_status, rule_code)
                VALUES(
                    @case_id, 'channel-a', @message_id,
                    'user-a', 'timeout', 600, 'Test',
                    'succeeded', @now, @now, 204, 'G_OTHER_ABUSE');

                INSERT INTO message_availability_events(
                    eventsub_message_id, broadcaster_id, twitch_message_id,
                    target_user_id, availability, occurred_utc, received_utc,
                    correlation_kind, learning_state, case_id)
                VALUES(
                    'delete-availability', 'channel-a', @message_id,
                    'user-a', 'deleted_exact', @now, @now,
                    'local', 'accepted', @case_id);

                INSERT INTO record_annotations(
                    record_type, record_id, annotation_kind,
                    annotation_text, created_utc)
                VALUES(
                    'moderation_cases', CAST(@case_id AS TEXT), 'note',
                    'package note', @now);

                INSERT INTO processed_eventsub_events(
                    eventsub_message_id, subscription_type, received_utc)
                VALUES(
                    @eventsub_id, 'channel.chat.message', @now);
                """;
            feedback.Parameters.AddWithValue("@case_id", caseId);
            feedback.Parameters.AddWithValue("@message_id", messageId);
            feedback.Parameters.AddWithValue("@eventsub_id", eventSubId);
            feedback.Parameters.AddWithValue("@now", now);
            await feedback.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
        return new ClosedCaseFixture(caseId, messageId, eventSubId);
    }

    private static async Task<long> InsertOpenCaseAsync(TestDatabase database)
    {
        const string messageId = "open-case-message";
        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();
        await InsertMessageAsync(
            connection,
            transaction,
            messageId,
            "open-case-event",
            now);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO moderation_cases(
                twitch_message_id, broadcaster_id, chatter_user_id,
                chatter_name_snapshot, message_text_original,
                message_text_normalized, effective_policy_hash,
                policy_revisions_json, message_snapshot_json,
                model_id, envelope_json, proposed_action,
                proposed_timeout_seconds, violation_rule_code,
                reason, policy_set, trigger_phrases_json,
                status, row_version, created_utc)
            VALUES(
                @message_id, 'channel-a', 'user-a',
                'User A', 'Open', 'open', 'policy',
                '{}', '{}',
                'model', '{}', 'timeout',
                600, 'G_OTHER_ABUSE',
                'Test', 'general', '[]',
                'pending', 1, @now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@message_id", messageId);
        command.Parameters.AddWithValue("@now", now);
        var caseId = Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            CultureInfo.InvariantCulture);
        await transaction.CommitAsync();
        return caseId;
    }

    private static async Task InsertMessageAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string messageId,
        string eventSubId,
        string now)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO chat_message_snapshots(
                twitch_message_id, eventsub_notification_id,
                broadcaster_id, chatter_user_id,
                chatter_login_original, chatter_name_original,
                message_text_original, lower_text, message_text_normalized,
                normalization_version, original_sha256, normalized_sha256,
                identity_fingerprint, channel_context_revision,
                category_is_verified, ingress_sequence, badges_json, received_utc)
            VALUES(
                @message_id, @eventsub_id,
                'channel-a', 'user-a',
                'user-a', 'User A',
                'Message', 'message', 'message',
                1, 'original', 'normalized',
                'fingerprint', 1,
                1, 1, '{}', @now);
            """;
        command.Parameters.AddWithValue("@message_id", messageId);
        command.Parameters.AddWithValue("@eventsub_id", eventSubId);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (T)Convert.ChangeType(
            result,
            typeof(T),
            CultureInfo.InvariantCulture);
    }

    private sealed record ClosedCaseFixture(
        long CaseId,
        string MessageId,
        string EventSubId);

    private sealed class RecordingPolicyCacheInvalidator : IPolicyCacheInvalidator
    {
        public List<PolicyChangeSet> Changes { get; } = [];

        public void Invalidate(PolicyChangeSet changeSet)
        {
            Changes.Add(changeSet);
        }
    }
}
