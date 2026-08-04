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
        var isAutoModFallbackLearning = learnAsAllowed
            && moderationCase.Message.IsAutoModHeld
            && feedbackPolicySet == PolicySetCode.General
            && string.Equals(
                feedbackRuleCode,
                ModerationRuleCodes.AutoModHeld,
                StringComparison.Ordinal);
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
            && !isAutoModFallbackLearning
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
                : "Der Vorschlag wurde ausschließlich als erlaubtes Learning-Beispiel gespeichert.");
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

}
