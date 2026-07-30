using System.Text.Json;
using Microsoft.Data.Sqlite;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Infrastructure.Persistence;

public sealed class SqliteLearningFeedbackService : ILearningFeedbackService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly IPolicyStore _policyStore;
    private readonly IChatTextNormalizer _normalizer;

    public SqliteLearningFeedbackService(
        ISqliteConnectionFactory connectionFactory,
        IPolicyStore policyStore,
        IChatTextNormalizer normalizer,
        IEnumerable<IPolicyCacheInvalidator>? cacheInvalidators = null,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _policyStore = policyStore;
        _normalizer = normalizer;
        // Kept as an optional constructor dependency for binary/source compatibility
        // with existing composition roots. Manual learning examples deliberately do
        // not invalidate policy caches because they are not policy configuration.
        _ = cacheInvalidators;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async ValueTask<LearningFeedbackResult> SaveAsync(
        LearningReviewItem review,
        LearningFeedbackDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (decision == LearningFeedbackDecision.Aborted)
        {
            return new LearningFeedbackResult(
                ManualExampleId: null,
                Saved: false,
                decision,
                "Der Lerntest wurde ohne Lernsignal abgebrochen.");
        }

        ValidateReview(review, decision);
        var currentPolicy = await _policyStore
            .GetLearningPolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(
                LearningPolicyRevisionFingerprint.Create(currentPolicy),
                review.TestResult.PolicyRevisionFingerprint))
        {
            throw new InvalidOperationException(
                "Die Regeln wurden seit dem Lerntest geändert. Bitte den Test erneut ausführen.");
        }

        var expectedPolicySet = review.PolicySet == PolicySetCode.General
            ? "general"
            : "backseatgaming";
        var activeRule = currentPolicy.Rules.FirstOrDefault(rule =>
            StringComparer.Ordinal.Equals(rule.RuleCode, review.RuleCode));
        if (activeRule is null ||
            !StringComparer.Ordinal.Equals(
                expectedPolicySet,
                PolicySetForRule(activeRule.RuleCode)))
        {
            throw new InvalidOperationException(
                "Die ausgewählte Verstoßkategorie ist nicht mehr im gewählten Regelset aktiv.");
        }

        var normalizedTriggers = NormalizeTriggers(review);
        var now = DateTimeOffset.UtcNow;
        long manualExampleId;

        await using (var writeLease = await _writeCoordinator
                         .AcquireAsync(cancellationToken)
                         .ConfigureAwait(false))
        await using (var connection = await _connectionFactory
                         .OpenConnectionAsync(cancellationToken)
                         .ConfigureAwait(false))
        await using (var transaction = connection.BeginTransaction())
        {
            var existingId = await FindExistingAsync(
                    connection,
                    transaction,
                    review.TestResult.TestId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existingId.HasValue)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new LearningFeedbackResult(
                    existingId,
                    Saved: true,
                    decision,
                    "Diese Lernentscheidung wurde bereits gespeichert.");
            }

            manualExampleId = await InsertExampleAsync(
                    connection,
                    transaction,
                    review,
                    decision,
                    normalizedTriggers,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new LearningFeedbackResult(
            manualExampleId,
            Saved: true,
            decision,
            decision == LearningFeedbackDecision.ConfirmedViolation
                ? "Der Lerntest wurde als positives Lernbeispiel gespeichert."
                : "Der Lerntest wurde als erlaubtes Lernbeispiel gespeichert.");
    }

    private static void ValidateReview(
        LearningReviewItem review,
        LearningFeedbackDecision decision)
    {
        var result = review.TestResult;
        if (result.TestId == Guid.Empty)
        {
            throw new ArgumentException("Der Lerntest besitzt keine gültige ID.", nameof(review));
        }

        if (string.IsNullOrEmpty(result.OriginalText) ||
            result.OriginalText.Length > 500 ||
            string.IsNullOrEmpty(result.LowerText) ||
            !StringComparer.Ordinal.Equals(
                result.OriginalText.ToLowerInvariant(),
                result.LowerText))
        {
            throw new ArgumentException(
                "Original- und Lowercase-Text des Lerntests stimmen nicht überein.",
                nameof(review));
        }

        if (string.IsNullOrWhiteSpace(review.RuleCode) ||
            !result.ActiveRules.Any(rule =>
                StringComparer.Ordinal.Equals(rule.RuleCode, review.RuleCode) &&
                rule.PolicySet == review.PolicySet))
        {
            throw new ArgumentException(
                "Die gewählte Verstoßkategorie gehört nicht zum getesteten Regelstand.",
                nameof(review));
        }

        if (review.Reason.Length > ModerationReasonNormalizer.MaximumLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(review),
                review.Reason.Length,
                "Der Moderationsgrund darf höchstens 120 Zeichen enthalten.");
        }

        if (decision == LearningFeedbackDecision.ConfirmedViolation)
        {
            if (review.Action == ModerationActionKind.None ||
                !ModerationActionRules.IsValid(review.Action, review.TimeoutSeconds))
            {
                throw new ArgumentException(
                    "Ein bestätigter Verstoß benötigt eine gültige Moderationsaktion.",
                    nameof(review));
            }
        }
    }

    private string[] NormalizeTriggers(LearningReviewItem review)
    {
        if (!ModerationRuleCodes.AllowsTriggerLearning(review.RuleCode))
        {
            return [];
        }

        var proposed = review.TestResult.ProposedTriggerPhrases
            .Select(_normalizer.Normalize)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var selected = review.SelectedTriggerPhrases
            .Select(_normalizer.Normalize)
            .Where(value => value.Length is >= 2 and <= 500)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (selected.Any(value =>
                !proposed.Contains(value) ||
                !review.TestResult.LowerText.Contains(
                    value,
                    StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Mindestens eine ausgewählte Triggerphrase gehört nicht zum Lerntest.",
                nameof(review));
        }

        return selected;
    }

    private static async Task<long?> FindExistingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid testId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT manual_example_id
            FROM manual_learning_examples
            WHERE test_id = @test_id;
            """;
        command.Parameters.AddWithValue("@test_id", testId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull
            ? null
            : Convert.ToInt64(
                value,
                System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> InsertExampleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LearningReviewItem review,
        LearningFeedbackDecision decision,
        IReadOnlyList<string> normalizedTriggers,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var result = review.TestResult;
        var isAllowed = decision == LearningFeedbackDecision.MarkedAllowed;
        var actualAction = isAllowed ? ModerationActionKind.None : review.Action;
        var timeoutSeconds = isAllowed ? null : review.TimeoutSeconds;
        var timestamp = SqliteData.ToDatabaseTimestamp(now);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO manual_learning_examples(
                test_id, message_text_original, lower_text,
                ai_verdict, ai_action, ai_timeout_seconds,
                ai_policy_set, ai_rule_code, ai_reason,
                ai_trigger_phrases_json, confidence,
                local_match_codes_json, final_decision,
                actual_action, actual_timeout_seconds,
                actual_policy_set, actual_rule_code, actual_reason,
                actual_trigger_phrases_json,
                policy_revision_fingerprint, model_id,
                prompt_tokens, completion_tokens, total_tokens,
                ai_latency_ms, eligible_for_learning,
                row_version, created_utc, updated_utc)
            VALUES(
                @test_id, @original_text, @lower_text,
                @ai_verdict, @ai_action, @ai_timeout_seconds,
                @ai_policy_set, @ai_rule_code, @ai_reason,
                @ai_triggers_json, @confidence,
                @local_match_codes_json, @final_decision,
                @actual_action, @actual_timeout_seconds,
                @actual_policy_set, @actual_rule_code, @actual_reason,
                @actual_triggers_json,
                @policy_fingerprint, @model_id,
                @prompt_tokens, @completion_tokens, @total_tokens,
                @latency_ms, 1, 1, @created_utc, @updated_utc)
            RETURNING manual_example_id;
            """;
        command.Parameters.AddWithValue("@test_id", result.TestId.ToString("D"));
        command.Parameters.AddWithValue("@original_text", result.OriginalText);
        command.Parameters.AddWithValue("@lower_text", result.LowerText);
        command.Parameters.AddWithValue(
            "@ai_verdict",
            result.Verdict == LearningVerdict.Allow ? "allow" : "moderate");
        command.Parameters.AddWithValue(
            "@ai_action",
            ToDatabaseAction(result.ProposedAction));
        command.Parameters.AddWithValue(
            "@ai_timeout_seconds",
            SqliteData.DbValue(result.ProposedTimeoutSeconds));
        command.Parameters.AddWithValue(
            "@ai_policy_set",
            ToDatabasePolicySet(result.ProposedPolicySet));
        command.Parameters.AddWithValue("@ai_rule_code", result.ProposedRuleCode);
        command.Parameters.AddWithValue("@ai_reason", result.ProposedReason);
        command.Parameters.AddWithValue(
            "@ai_triggers_json",
            JsonSerializer.Serialize(result.ProposedTriggerPhrases, JsonOptions));
        command.Parameters.AddWithValue("@confidence", result.Confidence);
        command.Parameters.AddWithValue(
            "@local_match_codes_json",
            JsonSerializer.Serialize(result.LocalMatchCodes, JsonOptions));
        command.Parameters.AddWithValue(
            "@final_decision",
            isAllowed ? "marked_allowed" : "confirmed_violation");
        command.Parameters.AddWithValue(
            "@actual_action",
            ToDatabaseAction(actualAction));
        command.Parameters.AddWithValue(
            "@actual_timeout_seconds",
            SqliteData.DbValue(timeoutSeconds));
        command.Parameters.AddWithValue(
            "@actual_policy_set",
            ToDatabasePolicySet(review.PolicySet));
        command.Parameters.AddWithValue("@actual_rule_code", review.RuleCode);
        command.Parameters.AddWithValue("@actual_reason", review.Reason);
        command.Parameters.AddWithValue(
            "@actual_triggers_json",
            JsonSerializer.Serialize(normalizedTriggers, JsonOptions));
        command.Parameters.AddWithValue(
            "@policy_fingerprint",
            result.PolicyRevisionFingerprint);
        command.Parameters.AddWithValue("@model_id", result.ModelId);
        command.Parameters.AddWithValue(
            "@prompt_tokens",
            SqliteData.DbValue(result.PromptTokens));
        command.Parameters.AddWithValue(
            "@completion_tokens",
            SqliteData.DbValue(result.CompletionTokens));
        command.Parameters.AddWithValue(
            "@total_tokens",
            SqliteData.DbValue(result.TotalTokens));
        command.Parameters.AddWithValue("@latency_ms", result.AiLatencyMilliseconds);
        command.Parameters.AddWithValue("@created_utc", timestamp);
        command.Parameters.AddWithValue("@updated_utc", timestamp);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(
            value,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string PolicySetForRule(string ruleCode) =>
        StringComparer.Ordinal.Equals(
            ruleCode,
            ModerationRuleCodes.Backseatgaming)
            ? "backseatgaming"
            : "general";

    private static string ToDatabasePolicySet(PolicySetCode policySet) =>
        policySet switch
        {
            PolicySetCode.General => "general",
            PolicySetCode.Backseatgaming => "backseatgaming",
            _ => throw new ArgumentOutOfRangeException(nameof(policySet), policySet, null),
        };

    private static string ToDatabaseAction(ModerationActionKind action) =>
        action switch
        {
            ModerationActionKind.None => "none",
            ModerationActionKind.Delete => "delete",
            ModerationActionKind.Timeout => "timeout",
            ModerationActionKind.Ban => "ban",
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
        };
}
