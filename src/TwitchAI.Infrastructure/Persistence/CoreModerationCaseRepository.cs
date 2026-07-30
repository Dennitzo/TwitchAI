using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using CoreCaseStatus = TwitchAI.Core.Models.ModerationCaseStatus;
using CoreModerationCase = TwitchAI.Core.Models.ModerationCase;

namespace TwitchAI.Infrastructure.Persistence;

public sealed class CoreModerationCaseRepository : IModerationCaseRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly IChatTextNormalizer _normalizer;

    public CoreModerationCaseRepository(
        ISqliteConnectionFactory connectionFactory,
        IChatTextNormalizer? normalizer = null,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _normalizer = normalizer ?? new ChatTextNormalizer();
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async ValueTask<CoreModerationCase?> GetAsync(
        long caseId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT case_id, row_version, message_snapshot_json,
                   proposed_action, proposed_timeout_seconds, reason,
                   policy_set, trigger_phrases_json, confidence,
                   effective_policy_hash, status, created_utc,
                   batch_id, local_match_codes_json, ai_latency_ms,
                   violation_rule_code
            FROM moderation_cases
            WHERE case_id = @case_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var message = JsonSerializer.Deserialize<ChatMessageSnapshot>(
            reader.GetString(2),
            JsonOptions) ?? throw new InvalidDataException(
            $"Moderation case '{caseId}' contains an invalid message snapshot.");
        var triggers = JsonSerializer.Deserialize<string[]>(reader.GetString(7), JsonOptions) ?? [];
        var localMatchCodes = JsonSerializer.Deserialize<string[]>(
            reader.GetString(13),
            JsonOptions) ?? [];
        return new CoreModerationCase(
            reader.GetInt64(0),
            reader.GetInt64(1),
            message,
            ParseAction(reader.GetString(3)),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            reader.GetString(5),
            ParsePolicySet(reader.GetString(6)),
            triggers,
            reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
            reader.GetString(9),
            ParseStatus(reader.GetString(10)),
            SqliteData.GetDateTimeOffset(reader, 11),
            reader.IsDBNull(12)
                ? null
                : Guid.Parse(reader.GetString(12)),
            localMatchCodes,
            reader.IsDBNull(14) ? null : reader.GetInt64(14))
        {
            RuleCode = reader.IsDBNull(15) ? null : reader.GetString(15),
        };
    }

    public async ValueTask<long> AddAsync(
        ChatMessageSnapshot message,
        ValidatedModerationDecision decision,
        string policyRevisionFingerprint,
        CancellationToken cancellationToken = default)
    {
        return await AddAsync(
            message,
            decision,
            policyRevisionFingerprint,
            ModerationCaseCreationMetadata.Empty,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<long> AddAsync(
        ChatMessageSnapshot message,
        ValidatedModerationDecision decision,
        string policyRevisionFingerprint,
        ModerationCaseCreationMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        var result = await AddCoreAsync(
                message,
                decision,
                policyRevisionFingerprint,
                metadata,
                suppressActiveDuplicate: false,
                cancellationToken)
            .ConfigureAwait(false);
        return result.CaseId;
    }

    public ValueTask<ModerationCaseAddResult> AddIfNoActiveDuplicateAsync(
        ChatMessageSnapshot message,
        ValidatedModerationDecision decision,
        string policyRevisionFingerprint,
        ModerationCaseCreationMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        return AddCoreAsync(
            message,
            decision,
            policyRevisionFingerprint,
            metadata,
            suppressActiveDuplicate: true,
            cancellationToken);
    }

    private async ValueTask<ModerationCaseAddResult> AddCoreAsync(
        ChatMessageSnapshot message,
        ValidatedModerationDecision decision,
        string policyRevisionFingerprint,
        ModerationCaseCreationMetadata metadata,
        bool suppressActiveDuplicate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyRevisionFingerprint);
        if (!ModerationProposalEligibility.CanBeReviewed(decision))
        {
            throw new ArgumentException(
                "A pending moderation case requires a valid action, policy set and violation category.",
                nameof(decision));
        }
        if (metadata.ReanalysisCaseId.HasValue !=
            metadata.ReanalysisCaseRowVersion.HasValue)
        {
            throw new ArgumentException(
                "A reanalysis requires both the source case ID and its expected row version.",
                nameof(metadata));
        }

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        if (suppressActiveDuplicate && !metadata.ReanalysisCaseId.HasValue)
        {
            var duplicateCaseId = await FindActiveDuplicateAsync(
                    connection,
                    transaction,
                    message,
                    cancellationToken)
                .ConfigureAwait(false);
            if (duplicateCaseId is long existingCaseId)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new ModerationCaseAddResult(existingCaseId, IsCreated: false);
            }
        }

        await UpsertCategoryAsync(connection, transaction, message, cancellationToken)
            .ConfigureAwait(false);
        await InsertMessageSnapshotAsync(connection, transaction, message, cancellationToken)
            .ConfigureAwait(false);
        await EnsureIdentityMatchesAsync(connection, transaction, message, cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var localMatchCodes = metadata.LocalMatchCodes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Select(code => code.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var policySet = ToDatabasePolicySet(
            decision.PolicySet ??
            throw new ArgumentException("A moderation decision requires a policy set."));

        void AddMutableParameters(SqliteCommand target)
        {
            target.Parameters.AddWithValue("@message_id", message.Identity.TwitchMessageId);
            target.Parameters.AddWithValue("@policy_hash", policyRevisionFingerprint);
            target.Parameters.AddWithValue(
                "@policy_revisions",
                JsonSerializer.Serialize(
                    new { fingerprint = policyRevisionFingerprint },
                    JsonOptions));
            target.Parameters.AddWithValue(
                "@message_snapshot",
                JsonSerializer.Serialize(message, JsonOptions));
            target.Parameters.AddWithValue(
                "@decision_json",
                JsonSerializer.Serialize(decision, JsonOptions));
            target.Parameters.AddWithValue(
                "@batch_id",
                metadata.BatchId is { } batchId
                    ? batchId.ToString("D")
                    : DBNull.Value);
            target.Parameters.AddWithValue(
                "@local_match_codes",
                JsonSerializer.Serialize(localMatchCodes, JsonOptions));
            target.Parameters.AddWithValue(
                "@ai_latency_ms",
                SqliteData.DbValue(metadata.AiLatencyMilliseconds));
            target.Parameters.AddWithValue("@action", ToDatabaseAction(decision.Action));
            target.Parameters.AddWithValue(
                "@timeout_seconds",
                SqliteData.DbValue(decision.TimeoutSeconds));
            target.Parameters.AddWithValue(
                "@violation_rule_code",
                decision.RuleCode);
            target.Parameters.AddWithValue("@reason", decision.Reason);
            target.Parameters.AddWithValue("@policy_set", policySet);
            target.Parameters.AddWithValue(
                "@trigger_phrases",
                JsonSerializer.Serialize(decision.TriggerPhrases, JsonOptions));
            target.Parameters.AddWithValue("@confidence", decision.Confidence);
        }

        await using (var refresh = connection.CreateCommand())
        {
            refresh.Transaction = transaction;
            refresh.CommandText =
                """
                UPDATE moderation_cases
                SET effective_policy_hash = @policy_hash,
                    policy_revisions_json = @policy_revisions,
                    message_snapshot_json = @message_snapshot,
                    envelope_json = @decision_json,
                    batch_id = @batch_id,
                    local_match_codes_json = @local_match_codes,
                    ai_latency_ms = @ai_latency_ms,
                    proposed_action = @action,
                    proposed_timeout_seconds = @timeout_seconds,
                    violation_rule_code = @violation_rule_code,
                    reason = @reason,
                    policy_set = @policy_set,
                    trigger_phrases_json = @trigger_phrases,
                    confidence = @confidence,
                    status = 'pending',
                    row_version = row_version + 1,
                    resolved_utc = NULL
                WHERE twitch_message_id = @message_id
                  AND status = 'stale_policy'
                  AND (
                      @reanalysis_case_id IS NULL OR
                      (case_id = @reanalysis_case_id AND
                       row_version = @reanalysis_row_version)
                  )
                RETURNING case_id;
                """;
            AddMutableParameters(refresh);
            refresh.Parameters.AddWithValue(
                "@reanalysis_case_id",
                SqliteData.DbValue(metadata.ReanalysisCaseId));
            refresh.Parameters.AddWithValue(
                "@reanalysis_row_version",
                SqliteData.DbValue(metadata.ReanalysisCaseRowVersion));
            var refreshedCaseId = await refresh
                .ExecuteScalarAsync(cancellationToken)
                .ConfigureAwait(false);
            if (refreshedCaseId is not null)
            {
                await using var removeLegacyStaleFeedback = connection.CreateCommand();
                removeLegacyStaleFeedback.Transaction = transaction;
                removeLegacyStaleFeedback.CommandText =
                    """
                    DELETE FROM moderation_feedback
                    WHERE case_id = @case_id
                      AND twitch_executed = 0
                      AND eligible_for_learning = 0;
                    """;
                removeLegacyStaleFeedback.Parameters.AddWithValue(
                    "@case_id",
                    Convert.ToInt64(
                        refreshedCaseId,
                        CultureInfo.InvariantCulture));
                await removeLegacyStaleFeedback
                    .ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ModerationCaseAddResult(
                    Convert.ToInt64(
                        refreshedCaseId,
                        CultureInfo.InvariantCulture),
                    IsCreated: true);
            }

            if (metadata.ReanalysisCaseId.HasValue)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "The stale moderation case changed before the reanalysis result could be stored.");
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO moderation_cases(
                twitch_message_id, broadcaster_id, chatter_user_id,
                chatter_name_snapshot, message_text_original, message_text_normalized,
                category_id, category_name_snapshot,
                effective_policy_hash, policy_revisions_json, message_snapshot_json,
                matched_allowlist_id, model_id, envelope_json, batch_id,
                local_match_codes_json, ai_latency_ms,
                proposed_action, proposed_timeout_seconds, violation_rule_code,
                reason, policy_set, trigger_phrases_json, confidence,
                status, row_version, created_utc)
            VALUES(
                @message_id, @broadcaster_id, @chatter_user_id,
                @chatter_name, @message_original, @message_normalized,
                @category_id, @category_name,
                @policy_hash, @policy_revisions, @message_snapshot,
                NULL, 'local-lm-studio', @decision_json, @batch_id,
                @local_match_codes, @ai_latency_ms,
                @action, @timeout_seconds, @violation_rule_code,
                @reason, @policy_set, @trigger_phrases, @confidence,
                'pending', 1, @created_utc);
            SELECT last_insert_rowid();
            """;
        AddMutableParameters(command);
        command.Parameters.AddWithValue("@broadcaster_id", message.Identity.ChannelId);
        command.Parameters.AddWithValue("@chatter_user_id", message.Identity.TwitchUserId);
        command.Parameters.AddWithValue("@chatter_name", message.Identity.DisplayName);
        command.Parameters.AddWithValue("@message_original", message.Text.OriginalText);
        command.Parameters.AddWithValue("@message_normalized", message.Text.RuleNormalizedText);
        command.Parameters.AddWithValue(
            "@category_id",
            SqliteData.DbValue(message.ChannelContext.CategoryId));
        command.Parameters.AddWithValue(
            "@category_name",
            SqliteData.DbValue(message.ChannelContext.CategoryDisplayName));
        command.Parameters.AddWithValue("@created_utc", SqliteData.ToDatabaseTimestamp(now));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var caseId = Convert.ToInt64(result, CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new ModerationCaseAddResult(caseId, IsCreated: true);
    }

    private static async ValueTask<long?> FindActiveDuplicateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessageSnapshot message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT case_id
            FROM moderation_cases
            WHERE broadcaster_id = @broadcaster_id
              AND chatter_user_id = @chatter_user_id
              AND message_text_normalized = @message_text_normalized
              AND status IN ('pending', 'executing', 'outcome_unknown')
            ORDER BY case_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue(
            "@broadcaster_id",
            message.Identity.ChannelId);
        command.Parameters.AddWithValue(
            "@chatter_user_id",
            message.Identity.TwitchUserId);
        command.Parameters.AddWithValue(
            "@message_text_normalized",
            message.Text.RuleNormalizedText);
        var result = await command.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return result is null or DBNull
            ? null
            : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public async ValueTask<bool> TryTransitionAsync(
        long caseId,
        long expectedRowVersion,
        CoreCaseStatus fromStatus,
        CoreCaseStatus toStatus,
        CancellationToken cancellationToken = default)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var decidedAt = DateTimeOffset.UtcNow;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                UPDATE moderation_cases
                SET status = @to_status,
                    row_version = row_version + 1,
                    resolved_utc = CASE
                        WHEN @is_terminal = 1 THEN @resolved_utc
                        ELSE resolved_utc
                    END
                WHERE case_id = @case_id
                  AND row_version = @expected_version
                  AND status = @from_status;
                """;
            command.Parameters.AddWithValue("@to_status", ToDatabaseStatus(toStatus));
            command.Parameters.AddWithValue("@from_status", ToDatabaseStatus(fromStatus));
            command.Parameters.AddWithValue("@case_id", caseId);
            command.Parameters.AddWithValue("@expected_version", expectedRowVersion);
            command.Parameters.AddWithValue("@is_terminal", IsTerminal(toStatus) ? 1 : 0);
            command.Parameters.AddWithValue(
                "@resolved_utc",
                SqliteData.ToDatabaseTimestamp(decidedAt));
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        if (toStatus == CoreCaseStatus.Discarded)
        {
            await WriteDiscardFeedbackAsync(
                connection,
                transaction,
                caseId,
                decidedAt,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<bool> TryCompleteReanalysisWithoutActionAsync(
        long caseId,
        long expectedRowVersion,
        bool wasWhitelisted,
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
            UPDATE moderation_cases
            SET status = @status,
                row_version = row_version + 1,
                resolved_utc = @resolved_utc,
                proposed_action = 'none',
                proposed_timeout_seconds = NULL,
                violation_rule_code = NULL,
                reason = '',
                trigger_phrases_json = '[]'
            WHERE case_id = @case_id
              AND row_version = @expected_version
              AND status = 'stale_policy';
            """;
        command.Parameters.AddWithValue(
            "@status",
            wasWhitelisted ? "allowlisted" : "discarded");
        command.Parameters.AddWithValue("@case_id", caseId);
        command.Parameters.AddWithValue("@expected_version", expectedRowVersion);
        command.Parameters.AddWithValue(
            "@resolved_utc",
            SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private async Task WriteDiscardFeedbackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long caseId,
        DateTimeOffset decidedAt,
        CancellationToken cancellationToken)
    {
        string? categoryId;
        string messageNormalized;
        string policySet;
        string? violationRuleCode;
        string[] triggerPhrases;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT category_id, message_text_normalized,
                       policy_set, violation_rule_code, trigger_phrases_json
                FROM moderation_cases
                WHERE case_id = @case_id;
                """;
            read.Parameters.AddWithValue("@case_id", caseId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Moderation case '{caseId}' disappeared during discard.");
            }

            categoryId = reader.IsDBNull(0) ? null : reader.GetString(0);
            messageNormalized = reader.GetString(1);
            policySet = reader.GetString(2);
            violationRuleCode = reader.IsDBNull(3) ? null : reader.GetString(3);
            triggerPhrases =
                JsonSerializer.Deserialize<string[]>(reader.GetString(4), JsonOptions) ?? [];
        }

        var normalizedTriggers =
            ModerationRuleCodes.AllowsTriggerLearning(violationRuleCode)
                ? triggerPhrases
                    .Select(_normalizer.Normalize)
                    .Where(item => item.Length > 0)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
                : [];
        await using (var feedback = connection.CreateCommand())
        {
            feedback.Transaction = transaction;
            feedback.CommandText =
                """
                INSERT INTO moderation_feedback(
                    case_id, operator_decision, actual_action,
                    actual_timeout_seconds, actual_reason, discard_reason, rule_code,
                    eligible_for_learning, human_approved, twitch_executed,
                    learning_accepted, review_completed, decided_utc)
                VALUES(
                    @case_id, 'discarded', 'none',
                    NULL, NULL, 'operator_discard', @rule_code,
                    1, 1, 0,
                    @learning_accepted, 1, @decided_utc);
                """;
            feedback.Parameters.AddWithValue("@case_id", caseId);
            feedback.Parameters.AddWithValue(
                "@rule_code",
                SqliteData.DbValue(violationRuleCode));
            feedback.Parameters.AddWithValue(
                "@learning_accepted",
                normalizedTriggers.Length > 0 ? 1 : 0);
            feedback.Parameters.AddWithValue(
                "@decided_utc",
                SqliteData.ToDatabaseTimestamp(decidedAt));
            await feedback.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var index = 0; index < normalizedTriggers.Length; index++)
        {
            var triggerPhrase = normalizedTriggers[index];
            var ruleCode = await IncrementNegativeConfirmationAsync(
                connection,
                transaction,
                policySet,
                violationRuleCode,
                triggerPhrase,
                cancellationToken).ConfigureAwait(false) ?? violationRuleCode;
            await using var phraseFeedback = connection.CreateCommand();
            phraseFeedback.Transaction = transaction;
            phraseFeedback.CommandText =
                """
                INSERT INTO phrase_feedback(
                    case_id, source_kind, source_event_id, label_action,
                    rule_code, trigger_phrase, policy_set, is_positive,
                    category_id, message_normalized, eligible, confirmed_utc)
                VALUES(
                    @case_id, 'operator_discard', @source_event_id, 'none',
                    @rule_code, @trigger_phrase, @policy_set, 0,
                    @category_id, @message_normalized, 1, @confirmed_utc);
                """;
            phraseFeedback.Parameters.AddWithValue("@case_id", caseId);
            phraseFeedback.Parameters.AddWithValue(
                "@source_event_id",
                $"discard:{caseId}:{index}");
            phraseFeedback.Parameters.AddWithValue(
                "@rule_code",
                SqliteData.DbValue(ruleCode));
            phraseFeedback.Parameters.AddWithValue("@trigger_phrase", triggerPhrase);
            phraseFeedback.Parameters.AddWithValue("@policy_set", policySet);
            phraseFeedback.Parameters.AddWithValue(
                "@category_id",
                SqliteData.DbValue(categoryId));
            phraseFeedback.Parameters.AddWithValue(
                "@message_normalized",
                messageNormalized);
            phraseFeedback.Parameters.AddWithValue(
                "@confirmed_utc",
                SqliteData.ToDatabaseTimestamp(decidedAt));
            await phraseFeedback.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> IncrementNegativeConfirmationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string policySet,
        string? selectedRuleCode,
        string triggerPhrase,
        CancellationToken cancellationToken)
    {
        long? phraseId = null;
        string? ruleCode = null;
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                """
                SELECT p.phrase_id, r.rule_code
                FROM phrase_rules p
                INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = @global_scope
                  AND s.set_code = @policy_set
                  AND r.rule_code = @rule_code
                  AND p.normalized_phrase = @trigger_phrase
                  AND p.archived_utc IS NULL
                  AND r.archived_utc IS NULL
                LIMIT 1;
                """;
            read.Parameters.AddWithValue(
                "@global_scope",
                GlobalPolicyStorage.BroadcasterId);
            read.Parameters.AddWithValue("@policy_set", policySet);
            read.Parameters.AddWithValue(
                "@rule_code",
                SqliteData.DbValue(selectedRuleCode));
            read.Parameters.AddWithValue("@trigger_phrase", triggerPhrase);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                phraseId = reader.GetInt64(0);
                ruleCode = reader.GetString(1);
            }
        }

        if (phraseId.HasValue)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE phrase_rules
                SET negative_confirmations = negative_confirmations + 1,
                    row_version = row_version + 1,
                    updated_utc = @updated_utc
                WHERE phrase_id = @phrase_id;
                """;
            update.Parameters.AddWithValue("@phrase_id", phraseId.Value);
            update.Parameters.AddWithValue(
                "@updated_utc",
                SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return ruleCode;
    }

    private static async Task UpsertCategoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessageSnapshot message,
        CancellationToken cancellationToken)
    {
        if (message.ChannelContext.CategoryId is null)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO twitch_categories(
                category_id, display_name, last_seen_utc, last_verified_utc)
            VALUES(@category_id, @display_name, @observed_utc, @verified_utc)
            ON CONFLICT(category_id) DO UPDATE SET
                display_name = excluded.display_name,
                last_seen_utc = excluded.last_seen_utc,
                last_verified_utc = excluded.last_verified_utc;
            """;
        command.Parameters.AddWithValue("@category_id", message.ChannelContext.CategoryId);
        command.Parameters.AddWithValue(
            "@display_name",
            message.ChannelContext.CategoryDisplayName);
        command.Parameters.AddWithValue(
            "@observed_utc",
            SqliteData.ToDatabaseTimestamp(message.ChannelContext.ObservedAtUtc));
        command.Parameters.AddWithValue(
            "@verified_utc",
            message.ChannelContext.IsVerified
                ? SqliteData.ToDatabaseTimestamp(message.ChannelContext.ObservedAtUtc)
                : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertMessageSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessageSnapshot message,
        CancellationToken cancellationToken)
    {
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
                @message_id, @event_id,
                @channel_id, @user_id,
                @login, @display_name,
                @original_text, @lower_text, @normalized_text,
                @normalization_version, @original_sha256, @normalized_sha256,
                @identity_fingerprint, @category_id, @category_name,
                @context_revision, @is_verified, @ingress_sequence,
                @source_channel_id, @badges_json, @received_utc);
            """;
        command.Parameters.AddWithValue("@message_id", message.Identity.TwitchMessageId);
        command.Parameters.AddWithValue(
            "@event_id",
            message.Identity.EventSubNotificationId);
        command.Parameters.AddWithValue("@channel_id", message.Identity.ChannelId);
        command.Parameters.AddWithValue("@user_id", message.Identity.TwitchUserId);
        command.Parameters.AddWithValue("@login", message.Identity.Login);
        command.Parameters.AddWithValue("@display_name", message.Identity.DisplayName);
        command.Parameters.AddWithValue("@original_text", message.Text.OriginalText);
        command.Parameters.AddWithValue("@lower_text", message.Text.LowerText);
        command.Parameters.AddWithValue(
            "@normalized_text",
            message.Text.RuleNormalizedText);
        command.Parameters.AddWithValue(
            "@normalization_version",
            message.Text.NormalizationVersion);
        command.Parameters.AddWithValue("@original_sha256", message.Text.OriginalSha256);
        command.Parameters.AddWithValue(
            "@normalized_sha256",
            message.Text.RuleNormalizedSha256);
        command.Parameters.AddWithValue(
            "@identity_fingerprint",
            message.Identity.IdentityFingerprint);
        command.Parameters.AddWithValue(
            "@category_id",
            SqliteData.DbValue(message.ChannelContext.CategoryId));
        command.Parameters.AddWithValue(
            "@category_name",
            message.ChannelContext.CategoryDisplayName);
        command.Parameters.AddWithValue(
            "@context_revision",
            message.ChannelContext.Revision);
        command.Parameters.AddWithValue(
            "@is_verified",
            message.ChannelContext.IsVerified ? 1 : 0);
        command.Parameters.AddWithValue("@ingress_sequence", message.IngressSequence);
        command.Parameters.AddWithValue(
            "@source_channel_id",
            SqliteData.DbValue(message.Identity.SharedChatSourceChannelId));
        command.Parameters.AddWithValue(
            "@badges_json",
            JsonSerializer.Serialize(message.Identity.Badges, JsonOptions));
        command.Parameters.AddWithValue(
            "@received_utc",
            SqliteData.ToDatabaseTimestamp(message.Identity.ReceivedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task EnsureIdentityMatchesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ChatMessageSnapshot message,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT identity_fingerprint
            FROM chat_message_snapshots
            WHERE twitch_message_id = @message_id;
            """;
        command.Parameters.AddWithValue("@message_id", message.Identity.TwitchMessageId);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(result as string, message.Identity.IdentityFingerprint))
        {
            throw new InvalidOperationException(
                "A Twitch message ID is already bound to a different immutable identity.");
        }
    }

    private static ModerationActionKind ParseAction(string value)
    {
        return value switch
        {
            "none" => ModerationActionKind.None,
            "delete" => ModerationActionKind.Delete,
            "timeout" => ModerationActionKind.Timeout,
            "ban" => ModerationActionKind.Ban,
            _ => throw new InvalidDataException($"Unsupported moderation action '{value}'."),
        };
    }

    private static string ToDatabaseAction(ModerationActionKind action)
    {
        return action switch
        {
            ModerationActionKind.None => "none",
            ModerationActionKind.Delete => "delete",
            ModerationActionKind.Timeout => "timeout",
            ModerationActionKind.Ban => "ban",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
    }

    private static PolicySetCode ParsePolicySet(string value)
    {
        return value switch
        {
            "general" => PolicySetCode.General,
            "backseatgaming" => PolicySetCode.Backseatgaming,
            _ => throw new InvalidDataException($"Unsupported policy set '{value}'."),
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

    private static CoreCaseStatus ParseStatus(string value)
    {
        return value switch
        {
            "pending" => CoreCaseStatus.Pending,
            "executing" => CoreCaseStatus.Executing,
            "executed" => CoreCaseStatus.Executed,
            "discarded" => CoreCaseStatus.Discarded,
            "allowlisted" => CoreCaseStatus.Allowlisted,
            "stale_policy" => CoreCaseStatus.StalePolicy,
            "integrity_blocked" or "failed" => CoreCaseStatus.IntegrityBlocked,
            "externally_completed" or "externally_deleted" => CoreCaseStatus.ExternallyCompleted,
            "outcome_unknown" => CoreCaseStatus.OutcomeUnknown,
            _ => throw new InvalidDataException($"Unsupported moderation status '{value}'."),
        };
    }

    private static string ToDatabaseStatus(CoreCaseStatus status)
    {
        return status switch
        {
            CoreCaseStatus.Pending => "pending",
            CoreCaseStatus.Executing => "executing",
            CoreCaseStatus.Executed => "executed",
            CoreCaseStatus.Discarded => "discarded",
            CoreCaseStatus.Allowlisted => "allowlisted",
            CoreCaseStatus.StalePolicy => "stale_policy",
            CoreCaseStatus.IntegrityBlocked => "integrity_blocked",
            CoreCaseStatus.ExternallyCompleted => "externally_completed",
            CoreCaseStatus.OutcomeUnknown => "outcome_unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }

    private static bool IsTerminal(CoreCaseStatus status)
    {
        return status is CoreCaseStatus.Executed
            or CoreCaseStatus.Discarded
            or CoreCaseStatus.Allowlisted
            or CoreCaseStatus.IntegrityBlocked
            or CoreCaseStatus.ExternallyCompleted
            or CoreCaseStatus.OutcomeUnknown;
    }
}
