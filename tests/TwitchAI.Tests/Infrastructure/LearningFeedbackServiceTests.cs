using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Pipeline;
using CoreEffectivePolicySnapshot = TwitchAI.Core.Models.EffectivePolicySnapshot;

namespace TwitchAI.Tests.Infrastructure;

public sealed class LearningFeedbackServiceTests
{
    [Theory]
    [InlineData(ModerationRuleCodes.OtherAbuse, PolicySetCode.General)]
    [InlineData(ModerationRuleCodes.Backseatgaming, PolicySetCode.Backseatgaming)]
    public async Task ConfirmedViolationStoresOnlyManualExampleWithoutChangingPolicyPhrases(
        string ruleCode,
        PolicySetCode policySet)
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await CreateFixtureAsync(
            database,
            "eindeutiges lernsignal",
            ruleCode,
            policySet);
        var phraseStateBefore = await ReadPhraseStateAsync(database);
        var policyStateBefore = await ReadPolicyStateAsync(database);
        var feedbackCountBefore = await ReadCountAsync(database, "phrase_feedback");

        var saved = await fixture.Service.SaveAsync(
            fixture.Review,
            LearningFeedbackDecision.ConfirmedViolation);

        Assert.True(saved.Saved);
        Assert.NotNull(saved.ManualExampleId);
        Assert.Equal(2, fixture.LearningRevision.Current);
        var duplicate = await fixture.Service.SaveAsync(
            fixture.Review,
            LearningFeedbackDecision.ConfirmedViolation);
        Assert.Equal(saved.ManualExampleId, duplicate.ManualExampleId);
        Assert.Equal(2, fixture.LearningRevision.Current);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                example.final_decision,
                example.actual_action,
                example.actual_timeout_seconds,
                example.actual_rule_code,
                (SELECT COUNT(*) FROM moderation_cases),
                (SELECT COUNT(*) FROM action_attempts),
                (SELECT COUNT(*) FROM message_availability_events),
                actual_trigger_phrases_json
            FROM manual_learning_examples example
            WHERE example.manual_example_id = @manual_example_id
            ;
            """;
        command.Parameters.AddWithValue(
            "@manual_example_id",
            saved.ManualExampleId!.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("confirmed_violation", reader.GetString(0));
        Assert.Equal("timeout", reader.GetString(1));
        Assert.Equal(600, reader.GetInt32(2));
        Assert.Equal(ruleCode, reader.GetString(3));
        Assert.Equal(0, reader.GetInt32(4));
        Assert.Equal(0, reader.GetInt32(5));
        Assert.Equal(0, reader.GetInt32(6));
        Assert.Equal("""["eindeutiges lernsignal"]""", reader.GetString(7));
        Assert.Equal(feedbackCountBefore, await ReadCountAsync(database, "phrase_feedback"));
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
    }

    [Fact]
    public async Task MarkedAllowedStoresNegativeContextButDoesNotCreateForbiddenPhrase()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string trigger = "erlaubtes neues signal";
        var fixture = await CreateFixtureAsync(database, trigger);
        var phraseStateBefore = await ReadPhraseStateAsync(database);
        var policyStateBefore = await ReadPolicyStateAsync(database);
        var feedbackCountBefore = await ReadCountAsync(database, "phrase_feedback");

        var saved = await fixture.Service.SaveAsync(
            fixture.Review,
            LearningFeedbackDecision.MarkedAllowed);

        Assert.True(saved.Saved);
        Assert.Equal(2, fixture.LearningRevision.Current);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                example.final_decision,
                example.actual_action,
                (SELECT COUNT(*)
                 FROM phrase_rules phrase
                 INNER JOIN blacklist_rules rule
                    ON rule.rule_id = phrase.rule_id
                 WHERE rule.rule_code = @rule_code
                   AND phrase.normalized_phrase = @trigger)
            FROM manual_learning_examples example
            WHERE example.manual_example_id = @manual_example_id;
            """;
        command.Parameters.AddWithValue(
            "@manual_example_id",
            saved.ManualExampleId!.Value);
        command.Parameters.AddWithValue(
            "@rule_code",
            ModerationRuleCodes.OtherAbuse);
        command.Parameters.AddWithValue("@trigger", trigger);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("marked_allowed", reader.GetString(0));
        Assert.Equal("none", reader.GetString(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(feedbackCountBefore, await ReadCountAsync(database, "phrase_feedback"));
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
    }

    [Fact]
    public async Task AbortedTestWritesNothing()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await CreateFixtureAsync(database, "nicht speichern");

        var result = await fixture.Service.SaveAsync(
            fixture.Review,
            LearningFeedbackDecision.Aborted);

        Assert.False(result.Saved);
        Assert.Null(result.ManualExampleId);
        Assert.Equal(1, fixture.LearningRevision.Current);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM manual_learning_examples;";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RetrievalInterleavesManualAndTwitchExamplesAndRespectsActivePolicy()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await CreateFixtureAsync(database, "manuelles lernsignal");
        await fixture.Service.SaveAsync(
            fixture.Review,
            LearningFeedbackDecision.ConfirmedViolation);
        await InsertTwitchRetrievalExampleAsync(database);

        var current = await fixture.PolicyStore.GetLearningPolicyAsync();
        var corePolicy = MapPolicy(current);
        var examples = await new SqliteRetrievalExampleStore(database.ConnectionFactory)
            .GetRelevantForLearningTestAsync(corePolicy);

        Assert.True(examples.Count >= 2);
        Assert.Equal("nachricht mit manuelles lernsignal", examples[0].LowerText);
        Assert.Equal("twitch beispiel", examples[1].LowerText);
        Assert.True(examples[0].IsPositive);
        Assert.True(examples[1].IsPositive);
    }

    [Fact]
    public async Task UniversalEditAndDeleteAffectOnlyManualExamples()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string trigger = "editierbares lernsignal";
        var fixture = await CreateFixtureAsync(database, trigger);
        var saved = await fixture.Service.SaveAsync(
            fixture.Review,
            LearningFeedbackDecision.ConfirmedViolation);
        var phraseStateBefore = await ReadPhraseStateAsync(database);
        var policyStateBefore = await ReadPolicyStateAsync(database);
        var feedbackCountBefore = await ReadCountAsync(database, "phrase_feedback");
        var editorLearningRevision = new LearningContextRevision();
        var editor = new UniversalDatabaseEditor(
            database.ConnectionFactory,
            new DatabaseCatalog(),
            [fixture.PolicyStore],
            learningContextRevision: editorLearningRevision);

        await editor.SaveRowAsync(
            "manual_learning_examples",
            "manual_example_id",
            saved.ManualExampleId!.Value,
            new Dictionary<string, object?>
            {
                ["final_decision"] = "marked_allowed",
                ["actual_action"] = "none",
                ["actual_timeout_seconds"] = null,
            });

        Assert.Equal(2, editorLearningRevision.Current);

        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(feedbackCountBefore, await ReadCountAsync(database, "phrase_feedback"));

        var deleted = await editor.DeleteRowsAsync(
            "manual_learning_examples",
            "manual_example_id",
            [saved.ManualExampleId.Value]);

        Assert.Equal(1, deleted);
        Assert.Equal(3, editorLearningRevision.Current);
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(feedbackCountBefore, await ReadCountAsync(database, "phrase_feedback"));
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM manual_learning_examples;";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    private static async Task<Fixture> CreateFixtureAsync(
        TestDatabase database,
        string trigger,
        string ruleCode = ModerationRuleCodes.OtherAbuse,
        PolicySetCode policySet = PolicySetCode.General)
    {
        var normalizer = new ChatTextNormalizer();
        var policyStore = new SqlitePolicyStore(
            database.ConnectionFactory,
            normalizer);
        var policy = await policyStore.GetLearningPolicyAsync();
        var rule = policy.Rules.Single(item => item.RuleCode == ruleCode);
        var coreRule = ToCoreRule(rule);
        var original = $"Nachricht mit {trigger}";
        var result = new LearningTestResult(
            Guid.NewGuid(),
            original,
            original.ToLowerInvariant(),
            LearningVerdict.Moderate,
            ModerationActionKind.Timeout,
            ProposedTimeoutSeconds: 600,
            policySet,
            coreRule.RuleCode,
            coreRule.PublicReason,
            [trigger],
            Confidence: 0.91,
            LocalMatchCodes: [],
            AiLatencyMilliseconds: 25,
            LearningPolicyRevisionFingerprint.Create(policy),
            [coreRule],
            ModelId: "test-model",
            PromptTokens: 10,
            CompletionTokens: 5,
            TotalTokens: 15,
            AnalyzedAtUtc: DateTimeOffset.UtcNow,
            UsedRuleFallback: false);
        var review = new LearningReviewItem(
            result,
            ModerationActionKind.Timeout,
            TimeoutSeconds: 600,
            policySet,
            coreRule.RuleCode,
            coreRule.PublicReason,
            [trigger]);
        var learningRevision = new LearningContextRevision();
        var service = new SqliteLearningFeedbackService(
            database.ConnectionFactory,
            policyStore,
            normalizer,
            [policyStore],
            learningContextRevision: learningRevision);
        return new Fixture(policyStore, service, review, learningRevision);
    }

    private static async Task InsertTwitchRetrievalExampleAsync(
        TestDatabase database)
    {
        const string timestamp = "2026-07-29T10:00:00.0000000+00:00";
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO chat_message_snapshots(
                twitch_message_id, eventsub_notification_id,
                broadcaster_id, chatter_user_id,
                chatter_login_original, chatter_name_original,
                message_text_original, lower_text,
                message_text_normalized, normalization_version,
                original_sha256, normalized_sha256,
                identity_fingerprint, channel_context_revision,
                category_is_verified, ingress_sequence,
                badges_json, received_utc)
            VALUES(
                'retrieval-message', 'retrieval-event',
                'channel', 'user', 'User', 'User',
                'Twitch Beispiel', 'twitch beispiel',
                'twitch beispiel', 1, 'original', 'normalized',
                'fingerprint', 1, 1, 1, '{}', @timestamp);

            INSERT INTO moderation_cases(
                twitch_message_id, broadcaster_id, chatter_user_id,
                chatter_name_snapshot, message_text_original,
                message_text_normalized, effective_policy_hash,
                model_id, envelope_json, proposed_action,
                violation_rule_code, reason, policy_set,
                confidence, status, created_utc)
            VALUES(
                'retrieval-message', 'channel', 'user',
                'User', 'Twitch Beispiel', 'twitch beispiel',
                'policy', 'model', '{}', 'delete',
                'G_OTHER_ABUSE', '', 'general',
                0.8, 'executed', @timestamp);

            INSERT INTO moderation_feedback(
                case_id, operator_decision, actual_action,
                actual_reason, rule_code, eligible_for_learning,
                human_approved, twitch_executed, learning_accepted,
                review_completed, decided_utc)
            SELECT
                case_id, 'executed', 'delete',
                '', 'G_OTHER_ABUSE', 1,
                1, 1, 1, 1, @timestamp
            FROM moderation_cases
            WHERE twitch_message_id = 'retrieval-message';
            """;
        command.Parameters.AddWithValue("@timestamp", timestamp);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadPhraseStateAsync(
        TestDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                phrase.phrase_id || '|' ||
                phrase.rule_id || '|' ||
                phrase.phrase || '|' ||
                phrase.normalized_phrase || '|' ||
                phrase.enabled || '|' ||
                phrase.manual_override || '|' ||
                phrase.positive_confirmations || '|' ||
                phrase.negative_confirmations || '|' ||
                phrase.row_version
            FROM phrase_rules phrase
            ORDER BY phrase.phrase_id;
            """;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<IReadOnlyList<string>> ReadPolicyStateAsync(
        TestDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT scope_id || '|' || revision || '|' || row_version
            FROM policy_sets
            ORDER BY scope_id;
            """;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<long> ReadCountAsync(
        TestDatabase database,
        string tableName)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM \"{tableName}\";";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static CoreEffectivePolicySnapshot MapPolicy(
        TwitchAI.Infrastructure.Persistence.EffectivePolicySnapshot policy) =>
        new(
            [
                new(PolicySetCode.General, policy.GeneralRevision),
                new(PolicySetCode.Backseatgaming, policy.CategoryRevision),
            ],
            policy.PolicyHash)
        {
            ActiveRules = policy.Rules.Select(ToCoreRule).ToArray(),
        };

    private static ModerationRuleDefinition ToCoreRule(BlacklistRuleRecord rule) =>
        new(
            rule.RuleCode,
            rule.RuleCode == ModerationRuleCodes.Backseatgaming
                ? PolicySetCode.Backseatgaming
                : PolicySetCode.General,
            rule.Title,
            rule.PromptInstruction,
            rule.DefaultAction == ModerationRuleAction.Timeout
                ? ModerationActionKind.Timeout
                : ModerationActionKind.Delete,
            rule.DefaultTimeoutSeconds,
            rule.PublicReason,
            rule.Priority,
            ModerationRuleCodes.AllowsTriggerLearning(rule.RuleCode));

    private sealed record Fixture(
        SqlitePolicyStore PolicyStore,
        SqliteLearningFeedbackService Service,
        LearningReviewItem Review,
        LearningContextRevision LearningRevision);
}
