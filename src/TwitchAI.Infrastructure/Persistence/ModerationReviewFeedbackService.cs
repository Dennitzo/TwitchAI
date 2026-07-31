using Microsoft.Data.Sqlite;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using CoreModerationCaseStatus = TwitchAI.Core.Models.ModerationCaseStatus;

namespace TwitchAI.Infrastructure.Persistence;

public interface IModerationReviewFeedbackService
{
    ValueTask<ModerationExecutionResult> DiscardAsync(
        long caseId,
        long expectedRowVersion,
        string discardReason,
        PolicySetCode? feedbackPolicySet,
        string? feedbackRuleCode,
        IReadOnlyList<string> feedbackTriggerPhrases,
        bool learnAsAllowed = true,
        CancellationToken cancellationToken = default);

    ValueTask<ModerationExecutionResult> CompleteUnavailableAsync(
        ModerationExecutionRequest request,
        bool learn,
        CancellationToken cancellationToken = default);
}

public sealed class SqliteModerationReviewFeedbackService
    : IModerationReviewFeedbackService
{
    private readonly IModerationCaseRepository _caseRepository;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly IChatTextNormalizer _normalizer;
    private readonly ILearningContextRevision _learningContextRevision;

    public SqliteModerationReviewFeedbackService(
        IModerationCaseRepository caseRepository,
        ISqliteConnectionFactory connectionFactory,
        IChatTextNormalizer normalizer,
        ISqliteWriteCoordinator? writeCoordinator = null,
        ILearningContextRevision? learningContextRevision = null)
    {
        _caseRepository = caseRepository;
        _connectionFactory = connectionFactory;
        _normalizer = normalizer;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
        _learningContextRevision = learningContextRevision
            ?? LearningContextRevision.ProcessWide;
    }

    public async ValueTask<ModerationExecutionResult> DiscardAsync(
        long caseId,
        long expectedRowVersion,
        string discardReason,
        PolicySetCode? feedbackPolicySet,
        string? feedbackRuleCode,
        IReadOnlyList<string> feedbackTriggerPhrases,
        bool learnAsAllowed = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(discardReason);
        ArgumentNullException.ThrowIfNull(feedbackTriggerPhrases);
        discardReason = discardReason.Trim();
        feedbackRuleCode = string.IsNullOrWhiteSpace(feedbackRuleCode)
            ? null
            : feedbackRuleCode.Trim();
        if (learnAsAllowed
            && (feedbackPolicySet is null || feedbackRuleCode is null))
        {
            throw new ArgumentException(
                "Learning feedback requires a violation category.");
        }
        if (discardReason.Length > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(discardReason),
                discardReason.Length,
                "Discard reasons may contain at most 500 characters.");
        }

        var moderationCase = await _caseRepository.GetAsync(caseId, cancellationToken)
            .ConfigureAwait(false);
        if (moderationCase is null)
        {
            return Rejected("case_not_found", "Der Moderationsfall wurde nicht gefunden.");
        }

        if (moderationCase.Status != CoreModerationCaseStatus.Pending ||
            moderationCase.RowVersion != expectedRowVersion)
        {
            return Rejected(
                "case_changed",
                "Der Moderationsfall wurde bereits geändert oder verarbeitet.");
        }

        var proposed = NormalizeDistinct(moderationCase.TriggerPhrases);
        var selected = learnAsAllowed
            && ModerationRuleCodes.AllowsTriggerLearning(feedbackRuleCode)
            ? NormalizeDistinct(feedbackTriggerPhrases)
            : [];
        var databasePolicySet = feedbackPolicySet is { } selectedPolicySet
            ? ToDatabasePolicySet(selectedPolicySet)
            : null;
        if (!selected.All(proposed.Contains))
        {
            return Rejected(
                "trigger_phrase_mismatch",
                "Mindestens eine Feedback-Phrase gehört nicht zum Moderationsfall.");
        }

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        if (learnAsAllowed
            && !await IsActiveRuleAsync(
                connection,
                transaction,
                databasePolicySet!,
                feedbackRuleCode!,
                moderationCase.Message.ChannelContext.HasKnownCategory
                    ? moderationCase.Message.ChannelContext.CategoryId
                    : null,
                cancellationToken).ConfigureAwait(false))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Rejected(
                "inactive_rule_code",
                "Die ausgewählte Regelkategorie ist nicht mehr aktiv.");
        }

        var decidedAt = DateTimeOffset.UtcNow;
        await using (var transition = connection.CreateCommand())
        {
            transition.Transaction = transaction;
            transition.CommandText =
                """
                UPDATE moderation_cases
                SET status = 'discarded',
                    row_version = row_version + 1,
                    resolved_utc = @resolved_utc,
                    policy_set = COALESCE(@policy_set, policy_set),
                    violation_rule_code = COALESCE(
                        @rule_code,
                        violation_rule_code)
                WHERE case_id = @case_id
                  AND row_version = @expected_version
                  AND status = 'pending';
                """;
            transition.Parameters.AddWithValue("@case_id", caseId);
            transition.Parameters.AddWithValue("@expected_version", expectedRowVersion);
            transition.Parameters.AddWithValue(
                "@policy_set",
                SqliteData.DbValue(databasePolicySet));
            transition.Parameters.AddWithValue(
                "@rule_code",
                SqliteData.DbValue(feedbackRuleCode));
            transition.Parameters.AddWithValue(
                "@resolved_utc",
                SqliteData.ToDatabaseTimestamp(decidedAt));
            if (await transition.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) != 1)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return Rejected(
                    "case_changed",
                    "Der Moderationsfall wurde parallel geändert.");
            }
        }

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
                    NULL, NULL, @discard_reason, @rule_code,
                    @eligible, @human_approved, 0,
                    @eligible, 1, @decided_utc);
                """;
            feedback.Parameters.AddWithValue("@case_id", caseId);
            feedback.Parameters.AddWithValue("@discard_reason", discardReason);
            feedback.Parameters.AddWithValue(
                "@rule_code",
                SqliteData.DbValue(feedbackRuleCode));
            feedback.Parameters.AddWithValue("@eligible", learnAsAllowed ? 1 : 0);
            feedback.Parameters.AddWithValue(
                "@human_approved",
                learnAsAllowed ? 1 : 0);
            feedback.Parameters.AddWithValue(
                "@decided_utc",
                SqliteData.ToDatabaseTimestamp(decidedAt));
            await feedback.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var ordered = learnAsAllowed
            ? selected.OrderBy(item => item, StringComparer.Ordinal).ToArray()
            : [];
        for (var index = 0; index < ordered.Length; index++)
        {
            var phrase = ordered[index];
            // An allowed review is negative feedback for moderation, not a new
            // forbidden phrase. Only an already configured/learned phrase may
            // receive a negative counter.
            var target = await FindPhraseTargetAsync(
                connection,
                transaction,
                databasePolicySet!,
                feedbackRuleCode!,
                phrase,
                cancellationToken).ConfigureAwait(false);
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
                    @category_id, @message_normalized, @eligible, @confirmed_utc);
                """;
            phraseFeedback.Parameters.AddWithValue("@case_id", caseId);
            phraseFeedback.Parameters.AddWithValue(
                "@source_event_id",
                $"discard:{caseId}:{index}");
            phraseFeedback.Parameters.AddWithValue(
                "@rule_code",
                feedbackRuleCode!);
            phraseFeedback.Parameters.AddWithValue("@trigger_phrase", phrase);
            phraseFeedback.Parameters.AddWithValue(
                "@policy_set",
                databasePolicySet!);
            phraseFeedback.Parameters.AddWithValue(
                "@category_id",
                SqliteData.DbValue(moderationCase.Message.ChannelContext.CategoryId));
            phraseFeedback.Parameters.AddWithValue(
                "@message_normalized",
                moderationCase.Message.Text.RuleNormalizedText);
            phraseFeedback.Parameters.AddWithValue(
                "@eligible",
                target.PhraseId.HasValue ? 1 : 0);
            phraseFeedback.Parameters.AddWithValue(
                "@confirmed_utc",
                SqliteData.ToDatabaseTimestamp(decidedAt));
            await phraseFeedback.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (target.PhraseId.HasValue)
            {
                await IncrementNegativeAsync(
                    connection,
                    transaction,
                    target.PhraseId.Value,
                    cancellationToken).ConfigureAwait(false);
                if (target.ScopeId.HasValue)
                {
                    await BumpPolicyRevisionAsync(
                        connection,
                        transaction,
                        target.ScopeId.Value,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        if (learnAsAllowed)
        {
            _learningContextRevision.Advance();
        }

        return new ModerationExecutionResult(
            ModerationExecutionOutcome.Succeeded,
            HumanApproved: learnAsAllowed,
            TwitchExecuted: false,
            LearningAccepted: learnAsAllowed,
            "discarded",
            !learnAsAllowed
                ? "Der Vorschlag wurde ohne Twitch-Aktion und ohne Lernsignal abgebrochen."
                : selected.Count > 0
                ? "Der Vorschlag wurde als erlaubtes Fallbeispiel gespeichert; neue Verbotsphrasen wurden nicht angelegt."
                : "Der Vorschlag wurde als negatives Fallbeispiel ohne Phrasen-Lernsignal gespeichert.");
    }

    private static async Task IncrementNegativeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long phraseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE phrase_rules
            SET negative_confirmations = negative_confirmations + 1,
                row_version = row_version + 1,
                updated_utc = @updated_utc
            WHERE phrase_id = @phrase_id;
            """;
        command.Parameters.AddWithValue("@phrase_id", phraseId);
        command.Parameters.AddWithValue(
            "@updated_utc",
            SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ModerationExecutionResult> CompleteUnavailableAsync(
        ModerationExecutionRequest request,
        bool learn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var moderationCase = await _caseRepository.GetAsync(request.CaseId, cancellationToken)
            .ConfigureAwait(false);
        if (moderationCase is null)
        {
            return Rejected("case_not_found", "Der Moderationsfall wurde nicht gefunden.");
        }

        if (moderationCase.Status != CoreModerationCaseStatus.ExternallyCompleted)
        {
            return Rejected(
                "case_not_unavailable",
                "Der Moderationsfall ist nicht als extern abgeschlossen markiert.");
        }

        var reasonMatches = request.Action == ModerationActionKind.Delete
            ? string.IsNullOrEmpty(request.Reason)
            : string.Equals(
                request.Reason,
                moderationCase.Reason,
                StringComparison.Ordinal);
        if (request.Action != moderationCase.ProposedAction ||
            request.TimeoutSeconds != moderationCase.ProposedTimeoutSeconds ||
            !reasonMatches ||
            request.PolicySet != moderationCase.PolicySet ||
            !string.Equals(
                request.RuleCode,
                moderationCase.RuleCode,
                StringComparison.Ordinal) ||
            !AcceptedPhrasesAreBoundToCase(moderationCase, request))
        {
            return Rejected(
                "review_mismatch",
                "Die Lernbestätigung gehört nicht zur gespeicherten, final geprüften Aktion.");
        }

        if (string.IsNullOrWhiteSpace(request.RuleCode))
        {
            return Rejected(
                "rule_code_missing",
                "Die gespeicherte Regelkategorie fehlt.");
        }

        var accepted = ModerationRuleCodes.AllowsTriggerLearning(request.RuleCode)
            ? NormalizeDistinct(request.AcceptedTriggerPhrases)
            : [];
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var existing = await ReadFeedbackStateAsync(
            connection,
            transaction,
            request.CaseId,
            cancellationToken).ConfigureAwait(false);
        if (existing.TwitchExecuted)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Rejected(
                "twitch_already_executed",
                "Der Fall wurde bereits als ausgeführte Twitch-Aktion protokolliert.");
        }

        if (existing.Exists && existing.ReviewCompleted)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ModerationExecutionResult(
                ModerationExecutionOutcome.AlreadyUnavailable,
                HumanApproved: true,
                TwitchExecuted: false,
                LearningAccepted: existing.LearningAccepted,
                "unavailable_review_already_saved",
                "Die Lernentscheidung wurde bereits gespeichert.");
        }

        await UpsertUnavailableFeedbackAsync(
            connection,
            transaction,
            moderationCase,
            request,
            learn,
            cancellationToken).ConfigureAwait(false);

        if (learn)
        {
            await WritePositiveUnavailablePhraseFeedbackAsync(
                connection,
                transaction,
                moderationCase,
                request,
                accepted,
                cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (learn)
        {
            _learningContextRevision.Advance();
        }

        return new ModerationExecutionResult(
            ModerationExecutionOutcome.AlreadyUnavailable,
            HumanApproved: true,
            TwitchExecuted: false,
            LearningAccepted: learn,
            learn ? "unavailable_learning_confirmed" : "unavailable_learning_declined",
            learn
                ? "Die externe Moderation wurde ohne Twitch-Aufruf als positives Feedback gespeichert."
                : "Die externe Moderation wurde ohne Lernen abgeschlossen.");
    }

    private bool AcceptedPhrasesAreBoundToCase(
        ModerationCase moderationCase,
        ModerationExecutionRequest request)
    {
        var proposed = NormalizeDistinct(moderationCase.TriggerPhrases);
        return NormalizeDistinct(request.AcceptedTriggerPhrases).All(proposed.Contains);
    }

    private static async Task WritePositiveUnavailablePhraseFeedbackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ModerationCase moderationCase,
        ModerationExecutionRequest request,
        HashSet<string> accepted,
        CancellationToken cancellationToken)
    {
        var ordered = accepted.OrderBy(item => item, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var phrase = ordered[index];
            var target = await EnsurePhraseTargetAsync(
                connection,
                transaction,
                ToDatabasePolicySet(request.PolicySet),
                request.RuleCode!,
                phrase,
                cancellationToken).ConfigureAwait(false);

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO phrase_feedback(
                    case_id, source_kind, source_event_id, label_action,
                    rule_code, trigger_phrase, policy_set, is_positive,
                    category_id, message_normalized, eligible, confirmed_utc)
                VALUES(
                    @case_id, 'external_delete', @source_event_id, @label_action,
                    @rule_code, @trigger_phrase, @policy_set, 1,
                    @category_id, @message_normalized, @eligible, @confirmed_utc);
                """;
            insert.Parameters.AddWithValue("@case_id", moderationCase.CaseId);
            insert.Parameters.AddWithValue(
                "@source_event_id",
                $"unavailable-review:{moderationCase.CaseId}:{index}");
            insert.Parameters.AddWithValue("@label_action", ToDatabaseAction(request.Action));
            insert.Parameters.AddWithValue(
                "@rule_code",
                request.RuleCode!);
            insert.Parameters.AddWithValue("@trigger_phrase", phrase);
            insert.Parameters.AddWithValue(
                "@policy_set",
                ToDatabasePolicySet(request.PolicySet));
            insert.Parameters.AddWithValue(
                "@category_id",
                SqliteData.DbValue(moderationCase.Message.ChannelContext.CategoryId));
            insert.Parameters.AddWithValue(
                "@message_normalized",
                moderationCase.Message.Text.RuleNormalizedText);
            insert.Parameters.AddWithValue(
                "@eligible",
                target.PhraseId.HasValue ? 1 : 0);
            insert.Parameters.AddWithValue(
                "@confirmed_utc",
                SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
            if (await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1 &&
                target.PhraseId.HasValue)
            {
                await IncrementPositiveAsync(
                    connection,
                    transaction,
                    target.PhraseId.Value,
                    cancellationToken).ConfigureAwait(false);
                if (target.ScopeId.HasValue)
                {
                    await BumpPolicyRevisionAsync(
                        connection,
                        transaction,
                        target.ScopeId.Value,
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static async Task UpsertUnavailableFeedbackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ModerationCase moderationCase,
        ModerationExecutionRequest request,
        bool learn,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO moderation_feedback(
                case_id, operator_decision, actual_action,
                actual_timeout_seconds, actual_reason, discard_reason, rule_code,
                eligible_for_learning, human_approved, twitch_executed,
                learning_accepted, review_completed, decided_utc)
            VALUES(
                @case_id, 'executed', @actual_action,
                @timeout_seconds, @reason, NULL, @rule_code,
                @learn, 1, 0,
                @learn, 1, @decided_utc)
            ON CONFLICT(case_id) DO UPDATE SET
                actual_action = excluded.actual_action,
                actual_timeout_seconds = excluded.actual_timeout_seconds,
                actual_reason = excluded.actual_reason,
                rule_code = excluded.rule_code,
                eligible_for_learning = excluded.eligible_for_learning,
                human_approved = 1,
                learning_accepted = excluded.learning_accepted,
                review_completed = 1,
                decided_utc = excluded.decided_utc
            WHERE moderation_feedback.twitch_executed = 0
              AND moderation_feedback.review_completed = 0;
            """;
        command.Parameters.AddWithValue("@case_id", moderationCase.CaseId);
        command.Parameters.AddWithValue(
            "@actual_action",
            ToDatabaseAction(request.Action));
        command.Parameters.AddWithValue(
            "@timeout_seconds",
            SqliteData.DbValue(request.TimeoutSeconds));
        command.Parameters.AddWithValue(
            "@reason",
            request.Action is ModerationActionKind.Timeout or ModerationActionKind.Ban
                ? request.Reason
                : DBNull.Value);
        command.Parameters.AddWithValue("@rule_code", request.RuleCode!);
        command.Parameters.AddWithValue("@learn", learn ? 1 : 0);
        command.Parameters.AddWithValue(
            "@decided_utc",
            SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<FeedbackState> ReadFeedbackStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long caseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT twitch_executed, learning_accepted, review_completed
            FROM moderation_feedback
            WHERE case_id = @case_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new FeedbackState(
                Exists: true,
                TwitchExecuted: reader.GetBoolean(0),
                LearningAccepted: reader.GetBoolean(1),
                ReviewCompleted: reader.GetBoolean(2))
            : new FeedbackState(false, false, false, false);
    }

    private static async Task<bool> IsActiveRuleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string policySet,
        string ruleCode,
        string? categoryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = @global_scope
                  AND s.set_code = @policy_set
                  AND r.rule_code = @rule_code
                  AND s.enabled = 1
                  AND s.archived_utc IS NULL
                  AND r.enabled = 1
                  AND r.archived_utc IS NULL
                  AND (
                      @policy_set <> 'backseatgaming'
                      OR (
                          @category_id IS NOT NULL
                          AND EXISTS (
                              SELECT 1
                              FROM twitch_categories known
                              WHERE known.category_id = @category_id
                          )
                          AND NOT EXISTS (
                              SELECT 1
                              FROM policy_category_exclusions e
                              WHERE e.scope_id = s.scope_id
                                AND e.category_id = @category_id
                                AND e.enabled = 1
                                AND e.archived_utc IS NULL
                          )
                      )
                  )
            );
            """;
        command.Parameters.AddWithValue(
            "@global_scope",
            GlobalPolicyStorage.BroadcasterId);
        command.Parameters.AddWithValue("@policy_set", policySet);
        command.Parameters.AddWithValue("@rule_code", ruleCode);
        command.Parameters.AddWithValue(
            "@category_id",
            SqliteData.DbValue(categoryId));
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<PhraseTarget> FindPhraseTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string policySet,
        string ruleCode,
        string phrase,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT p.phrase_id, r.rule_code, s.scope_id
            FROM phrase_rules p
            INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
            INNER JOIN policy_sets s ON s.scope_id = r.scope_id
            WHERE s.broadcaster_id = @global_scope
              AND s.set_code = @policy_set
              AND r.rule_code = @rule_code
              AND p.normalized_phrase = @phrase
              AND p.archived_utc IS NULL
              AND r.archived_utc IS NULL
            LIMIT 1;
            """;
        command.Parameters.AddWithValue(
            "@global_scope",
            GlobalPolicyStorage.BroadcasterId);
        command.Parameters.AddWithValue("@policy_set", policySet);
        command.Parameters.AddWithValue("@rule_code", ruleCode);
        command.Parameters.AddWithValue("@phrase", phrase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new PhraseTarget(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2))
            : new PhraseTarget(null, null, null);
    }

    private static async Task<PhraseTarget> EnsurePhraseTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string policySet,
        string ruleCode,
        string phrase,
        CancellationToken cancellationToken)
    {
        var existing = await FindPhraseTargetAsync(
            connection,
            transaction,
            policySet,
            ruleCode,
            phrase,
            cancellationToken).ConfigureAwait(false);
        if (existing.PhraseId.HasValue)
        {
            return existing;
        }

        long ruleId;
        long scopeId;
        await using (var findRule = connection.CreateCommand())
        {
            findRule.Transaction = transaction;
            findRule.CommandText =
                """
                SELECT r.rule_id, s.scope_id
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = @global_scope
                  AND s.set_code = @policy_set
                  AND r.rule_code = @rule_code
                  AND s.enabled = 1
                  AND s.archived_utc IS NULL
                  AND r.enabled = 1
                  AND r.archived_utc IS NULL
                LIMIT 1;
                """;
            findRule.Parameters.AddWithValue(
                "@global_scope",
                GlobalPolicyStorage.BroadcasterId);
            findRule.Parameters.AddWithValue("@policy_set", policySet);
            findRule.Parameters.AddWithValue("@rule_code", ruleCode);
            await using var reader = await findRule
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return existing;
            }

            ruleId = reader.GetInt64(0);
            scopeId = reader.GetInt64(1);
        }

        var now = SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO phrase_rules(
                    rule_id, phrase, normalized_phrase, enabled,
                    manual_override, positive_confirmations, negative_confirmations,
                    row_version, created_utc, updated_utc)
                VALUES(
                    @rule_id, @phrase, @phrase, 1,
                    'auto', 0, 0,
                    1, @now, @now);
                """;
            insert.Parameters.AddWithValue("@rule_id", ruleId);
            insert.Parameters.AddWithValue("@phrase", phrase);
            insert.Parameters.AddWithValue("@now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var readInserted = connection.CreateCommand();
        readInserted.Transaction = transaction;
        readInserted.CommandText =
            """
            SELECT phrase_id
            FROM phrase_rules
            WHERE rule_id = @rule_id
              AND normalized_phrase = @phrase
              AND archived_utc IS NULL;
            """;
        readInserted.Parameters.AddWithValue("@rule_id", ruleId);
        readInserted.Parameters.AddWithValue("@phrase", phrase);
        var value = await readInserted.ExecuteScalarAsync(cancellationToken)
            .ConfigureAwait(false);
        return value is null
            ? existing
            : new PhraseTarget(
                Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture),
                ruleCode,
                scopeId);
    }

    private static async Task IncrementPositiveAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long phraseId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE phrase_rules
            SET positive_confirmations = positive_confirmations + 1,
                row_version = row_version + 1,
                updated_utc = @updated_utc
            WHERE phrase_id = @phrase_id;
            """;
        command.Parameters.AddWithValue("@phrase_id", phraseId);
        command.Parameters.AddWithValue(
            "@updated_utc",
            SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task BumpPolicyRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE policy_sets
            SET revision = revision + 1,
                row_version = row_version + 1,
                updated_utc = @updated_utc
            WHERE scope_id = @scope_id;
            """;
        command.Parameters.AddWithValue("@scope_id", scopeId);
        command.Parameters.AddWithValue(
            "@updated_utc",
            SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private HashSet<string> NormalizeDistinct(IEnumerable<string> phrases)
    {
        return phrases
            .Select(_normalizer.Normalize)
            .Where(item => item.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string ToDatabaseAction(ModerationActionKind action)
    {
        return action switch
        {
            ModerationActionKind.Delete => "delete",
            ModerationActionKind.Timeout => "timeout",
            ModerationActionKind.Ban => "ban",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
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

    private static ModerationExecutionResult Rejected(string statusCode, string message)
    {
        return new ModerationExecutionResult(
            ModerationExecutionOutcome.Rejected,
            HumanApproved: false,
            TwitchExecuted: false,
            LearningAccepted: false,
            statusCode,
            message);
    }

    private readonly record struct FeedbackState(
        bool Exists,
        bool TwitchExecuted,
        bool LearningAccepted,
        bool ReviewCompleted);

    private readonly record struct PhraseTarget(
        long? PhraseId,
        string? RuleCode,
        long? ScopeId);
}
