using Microsoft.Data.Sqlite;
using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Pipeline;
using CoreEffectivePolicySnapshot = TwitchAI.Core.Models.EffectivePolicySnapshot;

namespace TwitchAI.Tests.Infrastructure;

public sealed class LearningDatasetServiceTests
{
    [Fact]
    public async Task Query_ProjectsOnlyConfirmedEligibleLiveAndManualLearning()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertLiveAsync(
            database,
            caseId: 101,
            text: "Bestätigter Verstoß",
            decision: "executed",
            action: "timeout",
            ruleCode: ModerationRuleCodes.OtherAbuse,
            eligible: true,
            accepted: true,
            occurredUtc: "2026-07-30T10:00:00.0000000+00:00");
        await InsertLiveAsync(
            database,
            caseId: 102,
            text: "Erlaubte Nachfrage",
            decision: "discarded",
            action: "none",
            ruleCode: ModerationRuleCodes.Spam,
            eligible: true,
            accepted: true,
            occurredUtc: "2026-07-30T10:01:00.0000000+00:00");
        await InsertLiveAsync(
            database,
            caseId: 103,
            text: "Nur abgebrochen",
            decision: "discarded",
            action: "none",
            ruleCode: ModerationRuleCodes.OtherAbuse,
            eligible: false,
            accepted: false,
            occurredUtc: "2026-07-30T10:02:00.0000000+00:00");
        await InsertManualAsync(
            database,
            manualId: 201,
            text: "Manuell bestätigt",
            finalDecision: "confirmed_violation",
            action: "timeout",
            policySet: "backseatgaming",
            ruleCode: ModerationRuleCodes.Backseatgaming,
            occurredUtc: "2026-07-30T10:03:00.0000000+00:00");
        await InsertManualAsync(
            database,
            manualId: 202,
            text: "Manuell erlaubt",
            finalDecision: "marked_allowed",
            action: "none",
            policySet: "general",
            ruleCode: ModerationRuleCodes.OtherAbuse,
            occurredUtc: "2026-07-30T10:04:00.0000000+00:00");

        var service = CreateService(database);
        var all = await service.QueryAsync(Query());
        var generalModerated = await service.QueryAsync(
            Query(LearningDecision.Moderate, PolicySetCode.General));
        var allowed = await service.QueryAsync(Query(LearningDecision.Allow));

        Assert.Equal(4, all.TotalCount);
        Assert.Equal(1, generalModerated.TotalCount);
        Assert.Equal("T-101", Assert.Single(generalModerated.Rows).DisplayId);
        Assert.Equal(2, allowed.TotalCount);
        Assert.Contains(allowed.Rows, item => item.DisplayId == "T-102");
        Assert.Contains(
            allowed.Rows,
            item => item.DisplayId == "L-202" &&
                    item.Reason == "Menschlich als erlaubt markiert.");
        Assert.DoesNotContain(all.Rows, item => item.DisplayId == "T-103");
        Assert.Contains(
            all.Rows,
            item => item.DisplayId == "L-201" &&
                    item.PolicySet == PolicySetCode.Backseatgaming);
    }

    [Fact]
    public async Task Update_CreatesEffectiveOverrideWithoutChangingHistoricalRecord()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertManualAsync(
            database,
            manualId: 301,
            text: "Historischer Originaltext",
            finalDecision: "confirmed_violation",
            action: "timeout",
            policySet: "general",
            ruleCode: ModerationRuleCodes.OtherAbuse,
            occurredUtc: "2026-07-30T11:00:00.0000000+00:00");
        var revision = new LearningContextRevision();
        var service = CreateService(database, revision);
        var initial = Assert.Single((await service.QueryAsync(Query())).Rows);
        Assert.Equal(0, initial.RowVersion);

        var updated = await service.UpdateAsync(
            initial.Source,
            initial.RowVersion,
            new LearningDatasetDraft(
                "DAS ist leichter Spam",
                LearningDecision.Moderate,
                PolicySetCode.General,
                ModerationRuleCodes.Spam,
                ModerationActionKind.Timeout,
                120,
                "Leichter Spam."));

        Assert.Equal(1, updated.RowVersion);
        Assert.Equal("das ist leichter spam", updated.LowerText);
        Assert.Equal(2, revision.Current);
        var found = await service.QueryAsync(Query(search: "LEICHTER SPAM"));
        Assert.Equal("G_SPAM", Assert.Single(found.Rows).RuleCode);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT message_text_original FROM manual_learning_examples WHERE manual_example_id = 301;";
        Assert.Equal("Historischer Originaltext", await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Remove_PersistsTombstoneAndExcludesExampleFromRetrieval()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertLiveAsync(
            database,
            caseId: 401,
            text: "Dieses Beispiel soll verschwinden",
            decision: "executed",
            action: "timeout",
            ruleCode: ModerationRuleCodes.OtherAbuse,
            eligible: true,
            accepted: true,
            occurredUtc: "2026-07-30T12:00:00.0000000+00:00");
        var revision = new LearningContextRevision();
        var service = CreateService(database, revision);
        var row = Assert.Single((await service.QueryAsync(Query())).Rows);

        await service.RemoveAsync(row.Source, row.RowVersion);

        Assert.Empty((await service.QueryAsync(Query())).Rows);
        Assert.Equal(2, revision.Current);
        var retrieval = await new SqliteRetrievalExampleStore(database.ConnectionFactory)
            .GetRelevantAsync(
                "any-channel",
                categoryId: null,
                CreatePolicy(includeBackseatgaming: false),
                ["dieses beispiel soll verschwinden"]);
        Assert.DoesNotContain(
            retrieval,
            item => item.LowerText == "dieses beispiel soll verschwinden");
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM moderation_cases WHERE case_id = 401;";
        Assert.Equal(1L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task UniversalDelete_ManualSourceRemovesTombstoneBeforeIdReuse()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertManualAsync(
            database,
            manualId: 450,
            text: "Altes manuelles Beispiel",
            finalDecision: "marked_allowed",
            action: "none",
            policySet: "general",
            ruleCode: ModerationRuleCodes.OtherAbuse,
            occurredUtc: "2026-07-30T12:30:00.0000000+00:00");
        var revision = new LearningContextRevision();
        var dataset = CreateService(database, revision);
        var oldRow = Assert.Single((await dataset.QueryAsync(Query())).Rows);
        await dataset.RemoveAsync(oldRow.Source, oldRow.RowVersion);
        var editor = new UniversalDatabaseEditor(
            database.ConnectionFactory,
            new DatabaseCatalog(),
            [],
            learningContextRevision: revision);

        var deleted = await editor.DeleteRowsAsync(
            "manual_learning_examples",
            "manual_example_id",
            [450L]);

        Assert.Equal(2, deleted);
        Assert.Equal(3, revision.Current);
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM learning_example_overrides
                WHERE source_kind = 'manual_test'
                  AND source_id = 450;
                """;
            Assert.Equal(0L, await command.ExecuteScalarAsync());
        }

        await InsertManualAsync(
            database,
            manualId: 450,
            text: "Neue Quelle mit wiederverwendeter ID",
            finalDecision: "confirmed_violation",
            action: "timeout",
            policySet: "general",
            ruleCode: ModerationRuleCodes.OtherAbuse,
            occurredUtc: "2026-07-30T12:31:00.0000000+00:00");
        var replacement = Assert.Single((await dataset.QueryAsync(Query())).Rows);
        Assert.Equal("Neue Quelle mit wiederverwendeter ID", replacement.OriginalText);
        Assert.Equal(LearningDecision.Moderate, replacement.Decision);
        Assert.Equal(0, replacement.RowVersion);
    }

    [Fact]
    public async Task Update_RejectsStaleRowVersion()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertManualAsync(
            database,
            manualId: 501,
            text: "Nebenläufiges Beispiel",
            finalDecision: "marked_allowed",
            action: "none",
            policySet: "general",
            ruleCode: ModerationRuleCodes.OtherAbuse,
            occurredUtc: "2026-07-30T13:00:00.0000000+00:00");
        var service = CreateService(database);
        var row = Assert.Single((await service.QueryAsync(Query())).Rows);
        var draft = new LearningDatasetDraft(
            row.OriginalText,
            LearningDecision.Allow,
            row.PolicySet,
            row.RuleCode,
            ModerationActionKind.None,
            null,
            "Erlaubtes Beispiel.");
        await service.UpdateAsync(row.Source, row.RowVersion, draft);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await service.UpdateAsync(row.Source, row.RowVersion, draft));

        Assert.Contains("concurrently", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retrieval_RanksSimilarTextBalancesLabelsAndKeepsNewestConflict()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertManualAsync(
            database,
            601,
            "kann mir bitte jemand einen sub schenken",
            "marked_allowed",
            "none",
            "general",
            ModerationRuleCodes.Begging,
            "2026-07-30T14:00:00.0000000+00:00");
        await InsertManualAsync(
            database,
            602,
            "ein völlig anderes erlaubtes beispiel",
            "marked_allowed",
            "none",
            "general",
            ModerationRuleCodes.OtherAbuse,
            "2026-07-30T14:01:00.0000000+00:00");
        await InsertManualAsync(
            database,
            603,
            "du bist wirklich dumm",
            "confirmed_violation",
            "timeout",
            "general",
            "G_INSULT",
            "2026-07-30T14:02:00.0000000+00:00");
        await InsertLiveAsync(
            database,
            604,
            "kann mir bitte jemand einen sub schenken",
            "executed",
            "timeout",
            ModerationRuleCodes.Begging,
            eligible: true,
            accepted: true,
            occurredUtc: "2026-07-30T14:03:00.0000000+00:00");
        await InsertManualAsync(
            database,
            605,
            "du musst jetzt nach links gehen",
            "confirmed_violation",
            "timeout",
            "backseatgaming",
            ModerationRuleCodes.Backseatgaming,
            "2026-07-30T14:04:00.0000000+00:00");

        var examples = await new SqliteRetrievalExampleStore(database.ConnectionFactory)
            .GetRelevantAsync(
                "channel",
                categoryId: null,
                CreatePolicy(includeBackseatgaming: false),
                ["kann mir jemand einen sub schenken"]);

        Assert.Equal(
            "kann mir bitte jemand einen sub schenken",
            examples[0].LowerText);
        Assert.True(examples[0].IsPositive);
        Assert.Equal(
            1,
            examples.Count(item =>
                item.LowerText == "kann mir bitte jemand einen sub schenken"));
        Assert.Contains(examples.Take(3), item => item.IsPositive);
        Assert.Contains(examples.Take(3), item => !item.IsPositive);
        Assert.DoesNotContain(
            examples,
            item => item.RuleCode == ModerationRuleCodes.Backseatgaming);

        var withBackseatgaming = await new SqliteRetrievalExampleStore(
                database.ConnectionFactory)
            .GetRelevantAsync(
                "another-channel",
                categoryId: "any-known-game",
                CreatePolicy(includeBackseatgaming: true),
                ["wohin muss ich jetzt gehen"]);
        Assert.Contains(
            withBackseatgaming,
            item => item.RuleCode == ModerationRuleCodes.Backseatgaming);
    }

    [Fact]
    public async Task Retrieval_NewerDecisionOutsideActivePolicyBlocksOlderConflict()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string text = "du musst jetzt nach links gehen";
        await InsertLiveAsync(
            database,
            caseId: 701,
            text,
            decision: "executed",
            action: "timeout",
            ruleCode: ModerationRuleCodes.OtherAbuse,
            eligible: true,
            accepted: true,
            occurredUtc: "2026-07-30T15:00:00.0000000+00:00");
        await InsertManualAsync(
            database,
            manualId: 702,
            text,
            finalDecision: "marked_allowed",
            action: "none",
            policySet: "backseatgaming",
            ruleCode: ModerationRuleCodes.Backseatgaming,
            occurredUtc: "2026-07-30T15:01:00.0000000+00:00");

        var examples = await new SqliteRetrievalExampleStore(database.ConnectionFactory)
            .GetRelevantAsync(
                "channel",
                categoryId: null,
                CreatePolicy(includeBackseatgaming: false),
                [text]);

        Assert.DoesNotContain(
            examples,
            item => item.LowerText == text);
    }

    [Fact]
    public async Task Retrieval_AppliesPolicyFilterBeforeCandidateLimit()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string relevantText = "altes aber relevantes general beispiel";
        await InsertManualAsync(
            database,
            manualId: 801,
            relevantText,
            finalDecision: "confirmed_violation",
            action: "timeout",
            policySet: "general",
            ruleCode: ModerationRuleCodes.OtherAbuse,
            occurredUtc: "2026-07-30T14:00:00.0000000+00:00");
        await InsertManyBackseatExamplesAsync(database, count: 1_000);

        var examples = await new SqliteRetrievalExampleStore(database.ConnectionFactory)
            .GetRelevantAsync(
                "channel",
                categoryId: null,
                CreatePolicy(includeBackseatgaming: false),
                [relevantText]);

        Assert.Contains(
            examples,
            item => item.LowerText == relevantText);
        Assert.DoesNotContain(
            examples,
            item => item.PolicySet == PolicySetCode.Backseatgaming);
    }

    [Fact]
    public async Task Retrieval_MarksOnlyIdenticalHumanDecisionAsExact()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertManualAsync(
            database,
            manualId: 901,
            "ich liebe dich",
            finalDecision: "confirmed_violation",
            action: "timeout",
            policySet: "general",
            ruleCode: ModerationRuleCodes.PersonalAttack,
            occurredUtc: "2026-07-30T16:00:00.0000000+00:00");
        await InsertManualAsync(
            database,
            manualId: 902,
            "ich liebe dich wirklich",
            finalDecision: "confirmed_violation",
            action: "timeout",
            policySet: "general",
            ruleCode: ModerationRuleCodes.PersonalAttack,
            occurredUtc: "2026-07-30T16:01:00.0000000+00:00");
        var policy = new CoreEffectivePolicySnapshot(
            [new PolicySetRevision(PolicySetCode.General, 1)],
            "human-priority")
        {
            ActiveRules =
            [
                Rule(ModerationRuleCodes.PersonalAttack, PolicySetCode.General),
            ],
        };

        var examples = await new SqliteRetrievalExampleStore(database.ConnectionFactory)
            .GetRelevantAsync(
                "channel",
                categoryId: null,
                policy,
                ["ich liebe dich"]);

        var identical = Assert.Single(
            examples,
            item => item.LowerText == "ich liebe dich");
        var similar = Assert.Single(
            examples,
            item => item.LowerText == "ich liebe dich wirklich");
        Assert.True(identical.IsExactHumanDecision);
        Assert.False(similar.IsExactHumanDecision);
    }

    private static SqliteLearningDatasetService CreateService(
        TestDatabase database,
        LearningContextRevision? revision = null) =>
        new(
            database.ConnectionFactory,
            revision ?? new LearningContextRevision(),
            new SqliteWriteCoordinator());

    private static LearningDatasetQuery Query(
        LearningDecision? decision = null,
        PolicySetCode? policySet = null,
        string? search = null) =>
        new(
            decision,
            policySet,
            search,
            SortColumn: "OccurredAtUtc",
            Descending: true,
            Offset: 0,
            PageSize: 100);

    private static CoreEffectivePolicySnapshot CreatePolicy(bool includeBackseatgaming)
    {
        var sets = new List<PolicySetRevision>
        {
            new(PolicySetCode.General, 1),
        };
        var rules = new List<ModerationRuleDefinition>
        {
            Rule(ModerationRuleCodes.OtherAbuse, PolicySetCode.General),
            Rule(ModerationRuleCodes.Spam, PolicySetCode.General),
            Rule(ModerationRuleCodes.Begging, PolicySetCode.General),
            Rule("G_INSULT", PolicySetCode.General),
        };
        if (includeBackseatgaming)
        {
            sets.Add(new PolicySetRevision(PolicySetCode.Backseatgaming, 1));
            rules.Add(Rule(
                ModerationRuleCodes.Backseatgaming,
                PolicySetCode.Backseatgaming));
        }

        return new CoreEffectivePolicySnapshot(sets, "test")
        {
            ActiveRules = rules,
        };
    }

    private static ModerationRuleDefinition Rule(
        string ruleCode,
        PolicySetCode policySet) =>
        new(
            ruleCode,
            policySet,
            ruleCode,
            ruleCode,
            ModerationActionKind.Timeout,
            600,
            ruleCode);

    private static async Task InsertLiveAsync(
        TestDatabase database,
        long caseId,
        string text,
        string decision,
        string action,
        string ruleCode,
        bool eligible,
        bool accepted,
        string occurredUtc)
    {
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
                @message_id, @event_id, 'channel', @user_id,
                @user_id, @user_id,
                @text, @lower_text, @lower_text, 1,
                @original_hash, @normalized_hash,
                @fingerprint, 1, 1, @case_id,
                '{}', @occurred_utc);

            INSERT INTO moderation_cases(
                case_id, twitch_message_id, broadcaster_id, chatter_user_id,
                chatter_name_snapshot, message_text_original,
                message_text_normalized, effective_policy_hash,
                model_id, envelope_json, proposed_action,
                proposed_timeout_seconds, violation_rule_code, reason,
                policy_set, confidence, status, created_utc)
            VALUES(
                @case_id, @message_id, 'channel', @user_id,
                @user_id, @text, @lower_text, 'policy',
                'model', '{}', 'timeout', 600, @rule_code,
                'Historischer Grund.', 'general', 0.8,
                CASE @decision WHEN 'executed' THEN 'executed' ELSE 'discarded' END,
                @occurred_utc);

            INSERT INTO moderation_feedback(
                case_id, operator_decision, actual_action,
                actual_timeout_seconds, actual_reason, discard_reason,
                rule_code, eligible_for_learning, human_approved,
                twitch_executed, learning_accepted, review_completed,
                decided_utc)
            VALUES(
                @case_id, @decision, @action,
                CASE @action WHEN 'timeout' THEN 600 ELSE NULL END,
                CASE @decision WHEN 'executed' THEN 'Bestätigter Grund.' ELSE NULL END,
                CASE @decision WHEN 'discarded' THEN 'Als erlaubt bestätigt.' ELSE NULL END,
                @rule_code, @eligible, @accepted,
                CASE @decision WHEN 'executed' THEN 1 ELSE 0 END,
                @accepted, 1, @occurred_utc);
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        command.Parameters.AddWithValue("@message_id", $"message-{caseId}");
        command.Parameters.AddWithValue("@event_id", $"event-{caseId}");
        command.Parameters.AddWithValue("@user_id", $"user-{caseId}");
        command.Parameters.AddWithValue("@text", text);
        command.Parameters.AddWithValue("@lower_text", text.ToLowerInvariant());
        command.Parameters.AddWithValue("@original_hash", $"original-{caseId}");
        command.Parameters.AddWithValue("@normalized_hash", $"normalized-{caseId}");
        command.Parameters.AddWithValue("@fingerprint", $"fingerprint-{caseId}");
        command.Parameters.AddWithValue("@decision", decision);
        command.Parameters.AddWithValue("@action", action);
        command.Parameters.AddWithValue("@rule_code", ruleCode);
        command.Parameters.AddWithValue("@eligible", eligible ? 1 : 0);
        command.Parameters.AddWithValue("@accepted", accepted ? 1 : 0);
        command.Parameters.AddWithValue("@occurred_utc", occurredUtc);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertManualAsync(
        TestDatabase database,
        long manualId,
        string text,
        string finalDecision,
        string action,
        string policySet,
        string ruleCode,
        string occurredUtc)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO manual_learning_examples(
                manual_example_id, test_id, message_text_original, lower_text,
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
                @manual_id, @test_id, @text, @lower_text,
                CASE @final_decision WHEN 'confirmed_violation' THEN 'moderate' ELSE 'allow' END,
                @action,
                CASE @action WHEN 'timeout' THEN 600 ELSE NULL END,
                @policy_set, @rule_code, 'AI-Grund.',
                '[]', 0.8, '[]', @final_decision,
                @action,
                CASE @action WHEN 'timeout' THEN 600 ELSE NULL END,
                @policy_set, @rule_code, 'Menschlicher Grund.',
                '[]', 'policy', 'model',
                10, 5, 15, 20, 1, 1, @occurred_utc, @occurred_utc);
            """;
        command.Parameters.AddWithValue("@manual_id", manualId);
        command.Parameters.AddWithValue("@test_id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("@text", text);
        command.Parameters.AddWithValue("@lower_text", text.ToLowerInvariant());
        command.Parameters.AddWithValue("@final_decision", finalDecision);
        command.Parameters.AddWithValue("@action", action);
        command.Parameters.AddWithValue("@policy_set", policySet);
        command.Parameters.AddWithValue("@rule_code", ruleCode);
        command.Parameters.AddWithValue("@occurred_utc", occurredUtc);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertManyBackseatExamplesAsync(
        TestDatabase database,
        int count)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH RECURSIVE sequence(value) AS (
                SELECT 1
                UNION ALL
                SELECT value + 1
                FROM sequence
                WHERE value < @count
            )
            INSERT INTO manual_learning_examples(
                manual_example_id, test_id, message_text_original, lower_text,
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
            SELECT
                10000 + value,
                'bulk-backseat-' || value,
                'irrelevantes backseat beispiel ' || value,
                'irrelevantes backseat beispiel ' || value,
                'moderate', 'timeout', 600,
                'backseatgaming', 'BACKSEATGAMING', 'AI-Grund.',
                '[]', 0.8, '[]', 'confirmed_violation',
                'timeout', 600,
                'backseatgaming', 'BACKSEATGAMING', 'Menschlicher Grund.',
                '[]', 'policy', 'model',
                10, 5, 15, 20, 1, 1,
                strftime(
                    '%Y-%m-%dT%H:%M:%fZ',
                    '2026-07-30T15:00:00Z',
                    '+' || value || ' seconds'),
                strftime(
                    '%Y-%m-%dT%H:%M:%fZ',
                    '2026-07-30T15:00:00Z',
                    '+' || value || ' seconds')
            FROM sequence;
            """;
        command.Parameters.AddWithValue("@count", count);
        await command.ExecuteNonQueryAsync();
    }
}
