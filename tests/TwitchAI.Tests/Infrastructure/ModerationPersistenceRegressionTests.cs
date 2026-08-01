using System.Net;
using System.Text.Json;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Pipeline;
using TwitchAI.Infrastructure.Twitch;
using CoreChatTextNormalizer = TwitchAI.Core.Services.ChatTextNormalizer;
using CoreEffectivePolicySnapshot = TwitchAI.Core.Models.EffectivePolicySnapshot;
using CoreModerationCaseStatus = TwitchAI.Core.Models.ModerationCaseStatus;

namespace TwitchAI.Tests.Infrastructure;

public sealed class ModerationPersistenceRegressionTests
{
    [Fact]
    public async Task TimeoutNotFoundIsFailedAndNeverCreatesPositiveLearning()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await CreateFixtureAsync(
            database,
            "timeout-404-channel",
            "toxic phrase");
        var caseId = await fixture.Repository.AddAsync(
            fixture.Message,
            Decision(
                ModerationActionKind.Timeout,
                timeoutSeconds: 300,
                ["toxic phrase"]),
            fixture.Policy.RevisionFingerprint);
        var helix = new ConfigurableHelixClient
        {
            TimeoutFailure = new TwitchHelixException(
                "User not found",
                HttpStatusCode.NotFound,
                """{"status":404}"""),
        };
        var executor = CreateExecutor(database, fixture, helix);

        var result = await executor.ExecuteAsync(
            new ModerationExecutionRequest(
                caseId,
                ExpectedRowVersion: 1,
                ModerationActionKind.Timeout,
                TimeoutSeconds: 300,
                "confirmed timeout",
                PolicySetCode.General,
                ["toxic phrase"],
                RuleCode: ModerationRuleCodes.GeneralPhrase));

        Assert.Equal(ModerationExecutionOutcome.Failed, result.Outcome);
        Assert.True(result.HumanApproved);
        Assert.False(result.TwitchExecuted);
        Assert.False(result.LearningAccepted);
        Assert.Equal(1, helix.TimeoutCalls);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.status, a.status, a.http_status, a.error_code,
                   f.actual_action, f.twitch_executed,
                   f.learning_accepted, f.eligible_for_learning,
                   (SELECT COUNT(*) FROM phrase_feedback
                    WHERE case_id = c.case_id AND is_positive = 1)
            FROM moderation_cases c
            INNER JOIN action_attempts a ON a.case_id = c.case_id
            INNER JOIN moderation_feedback f ON f.case_id = c.case_id
            WHERE c.case_id = @case_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("failed", reader.GetString(0));
        Assert.Equal("failed", reader.GetString(1));
        Assert.Equal(404, reader.GetInt32(2));
        Assert.Equal("twitch_http_404", reader.GetString(3));
        Assert.Equal("none", reader.GetString(4));
        Assert.False(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
        Assert.Equal(0, reader.GetInt32(8));
    }

    [Fact]
    public async Task IncompatiblePolicyReanalysisReusesFeedbackSlotAndCanExecute()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await CreateFixtureAsync(
            database,
            "stale-reanalysis-channel",
            "reanalyze this");
        var incompatibleDecision = new ValidatedModerationDecision(
            "m1",
            ModerationActionKind.Delete,
            TimeoutSeconds: null,
            PolicySetCode.Backseatgaming,
            "backseatgaming is inactive in Just Chatting",
            TriggerPhrases: [],
            Confidence: 0.9,
            RuleCode: ModerationRuleCodes.Backseatgaming);
        var caseId = await fixture.Repository.AddAsync(
            fixture.Message,
            incompatibleDecision,
            fixture.Policy.RevisionFingerprint);
        var helix = new ConfigurableHelixClient();
        var executor = CreateExecutor(database, fixture, helix);

        var staleResult = await executor.ExecuteAsync(
            new ModerationExecutionRequest(
                caseId,
                ExpectedRowVersion: 1,
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                string.Empty,
                PolicySetCode.Backseatgaming,
                [],
                RuleCode: ModerationRuleCodes.Backseatgaming));

        Assert.Equal(ModerationExecutionOutcome.StalePolicy, staleResult.Outcome);
        var staleCase = await fixture.Repository.GetAsync(caseId);
        Assert.NotNull(staleCase);
        Assert.Equal(CoreModerationCaseStatus.StalePolicy, staleCase.Status);

        var refreshedDecision = Decision(
            ModerationActionKind.Delete,
            timeoutSeconds: null,
            triggerPhrases: []) with
        {
            Reason = "fresh review",
        };
        var refreshedId = await fixture.Repository.AddAsync(
            fixture.Message,
            refreshedDecision,
            fixture.Policy.RevisionFingerprint,
            new ModerationCaseCreationMetadata(
                Guid.Parse("ed0f2286-d734-4ea6-85a6-a64f56942a13"),
                ["G.REANALYZED"],
                AiLatencyMilliseconds: 17));
        var refreshed = await fixture.Repository.GetAsync(refreshedId);
        Assert.NotNull(refreshed);
        Assert.Equal(caseId, refreshedId);
        Assert.Equal(CoreModerationCaseStatus.Pending, refreshed.Status);

        var executed = await executor.ExecuteAsync(
            new ModerationExecutionRequest(
                refreshedId,
                refreshed.RowVersion,
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                string.Empty,
                PolicySetCode.General,
                [],
                RuleCode: ModerationRuleCodes.GeneralPhrase));

        Assert.Equal(ModerationExecutionOutcome.Succeeded, executed.Outcome);
        Assert.True(executed.TwitchExecuted);
        Assert.True(executed.LearningAccepted);
        Assert.Equal(1, helix.DeleteCalls);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.status,
                   COUNT(f.feedback_id),
                   MAX(f.twitch_executed),
                   MAX(f.learning_accepted)
            FROM moderation_cases c
            LEFT JOIN moderation_feedback f ON f.case_id = c.case_id
            WHERE c.case_id = @case_id
            GROUP BY c.case_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("executed", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
    }

    [Fact]
    public async Task RetrievalSharesPositiveExecutedCaseAcrossChannels()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await CreateFixtureAsync(
            database,
            "retrieval-without-triggers-channel",
            "MESSAGE without a selected trigger");
        var caseId = await fixture.Repository.AddAsync(
            fixture.Message,
            Decision(
                ModerationActionKind.Delete,
                timeoutSeconds: null,
                triggerPhrases: []),
            fixture.Policy.RevisionFingerprint);
        var executor = CreateExecutor(
            database,
            fixture,
            new ConfigurableHelixClient());
        var phraseStateBefore = await ReadPhraseStateAsync(database);
        var policyStateBefore = await ReadPolicyStateAsync(database);
        var phraseFeedbackCountBefore = await ReadPhraseFeedbackCountAsync(database);

        var result = await executor.ExecuteAsync(
            new ModerationExecutionRequest(
                caseId,
                ExpectedRowVersion: 1,
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                string.Empty,
                PolicySetCode.General,
                [],
                RuleCode: ModerationRuleCodes.GeneralPhrase));
        Assert.Equal(ModerationExecutionOutcome.Succeeded, result.Outcome);

        var examples = await new SqliteRetrievalExampleStore(database.ConnectionFactory)
            .GetRelevantAsync(
                "different-connected-channel",
                fixture.Message.ChannelContext.CategoryId,
                fixture.Policy);

        var example = Assert.Single(examples);
        Assert.True(example.IsPositive);
        Assert.Equal(ModerationActionKind.Delete, example.Action);
        Assert.Null(example.TimeoutSeconds);
        Assert.Equal(PolicySetCode.General, example.PolicySet);
        Assert.Equal(
            fixture.Message.Text.LowerText,
            example.LowerText);
        Assert.Equal(string.Empty, example.Reason);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM phrase_feedback WHERE case_id = @case_id;";
        command.Parameters.AddWithValue("@case_id", caseId);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(
            phraseFeedbackCountBefore,
            await ReadPhraseFeedbackCountAsync(database));
    }

    [Fact]
    public async Task CaseRepositoryRoundTripsBatchMatchesAndAiLatencyMetadata()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await CreateFixtureAsync(
            database,
            "case-metadata-channel",
            "metadata message");
        var batchId = Guid.Parse("20a0e08d-3b7e-4ed6-ae54-d8e4667c7f07");
        var caseId = await fixture.Repository.AddAsync(
            fixture.Message,
            Decision(
                ModerationActionKind.Delete,
                timeoutSeconds: null,
                triggerPhrases: ["metadata"]),
            fixture.Policy.RevisionFingerprint,
            new ModerationCaseCreationMetadata(
                batchId,
                ["G.SPAM", "B.BACKSEAT", "G.SPAM"],
                AiLatencyMilliseconds: 314));

        var moderationCase = await fixture.Repository.GetAsync(caseId);

        Assert.NotNull(moderationCase);
        Assert.Equal(batchId, moderationCase.BatchId);
        Assert.Equal(
            ["G.SPAM", "B.BACKSEAT"],
            moderationCase.LocalMatchCodes);
        Assert.Equal(314, moderationCase.AiLatencyMilliseconds);
        Assert.Equal(ModerationRuleCodes.GeneralPhrase, moderationCase.RuleCode);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT batch_id, local_match_codes_json, ai_latency_ms,
                   violation_rule_code
            FROM moderation_cases
            WHERE case_id = @case_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(batchId.ToString("D"), reader.GetString(0));
        var storedMatchCodes =
            JsonSerializer.Deserialize<string[]>(reader.GetString(1));
        Assert.NotNull(storedMatchCodes);
        Assert.Equal(
            ["G.SPAM", "B.BACKSEAT"],
            storedMatchCodes);
        Assert.Equal(314, reader.GetInt64(2));
        Assert.Equal(ModerationRuleCodes.GeneralPhrase, reader.GetString(3));
    }

    [Fact]
    public async Task ExecutedFeedback_DoesNotRestoreArchivedPhraseTombstone()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string trigger = "archivierter trigger";
        var fixture = await CreateFixtureAsync(
            database,
            "archived-trigger-channel",
            $"Nachricht mit {trigger}");
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO phrase_rules(
                    rule_id, phrase, normalized_phrase, enabled,
                    manual_override, positive_confirmations, negative_confirmations,
                    row_version, created_utc, updated_utc, archived_utc)
                SELECT r.rule_id, @phrase, @phrase, 0,
                       'inactive', 0, 0,
                       1, @now, @now, @now
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = @global_scope
                  AND r.rule_code = @rule_code;
                """;
            command.Parameters.AddWithValue("@phrase", trigger);
            command.Parameters.AddWithValue(
                "@now",
                DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue(
                "@global_scope",
                GlobalPolicyStorage.BroadcasterId);
            command.Parameters.AddWithValue(
                "@rule_code",
                ModerationRuleCodes.GeneralPhrase);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var phraseStateBefore = await ReadPhraseStateAsync(database);
        var policyStateBefore = await ReadPolicyStateAsync(database);
        var phraseFeedbackCountBefore = await ReadPhraseFeedbackCountAsync(database);

        var caseId = await fixture.Repository.AddAsync(
            fixture.Message,
            Decision(
                ModerationActionKind.Delete,
                timeoutSeconds: null,
                triggerPhrases: [trigger]),
            fixture.Policy.RevisionFingerprint);
        var result = await CreateExecutor(
                database,
                fixture,
                new ConfigurableHelixClient())
            .ExecuteAsync(
                new ModerationExecutionRequest(
                    caseId,
                    ExpectedRowVersion: 1,
                    ModerationActionKind.Delete,
                    TimeoutSeconds: null,
                    string.Empty,
                    PolicySetCode.General,
                    [trigger],
                    RuleCode: ModerationRuleCodes.GeneralPhrase));

        Assert.Equal(ModerationExecutionOutcome.Succeeded, result.Outcome);
        await using var verifyConnection = await database.OpenConnectionAsync();
        await using var verify = verifyConnection.CreateCommand();
        verify.CommandText =
            """
            SELECT COUNT(*),
                   MAX(CASE WHEN p.archived_utc IS NOT NULL THEN 1 ELSE 0 END),
                   SUM(p.positive_confirmations),
                   (SELECT COUNT(*)
                    FROM phrase_feedback
                    WHERE case_id = @case_id AND trigger_phrase = @phrase),
                   (SELECT rule_code
                    FROM moderation_feedback
                    WHERE case_id = @case_id),
                   (SELECT rule_code
                    FROM action_attempts
                    WHERE case_id = @case_id)
            FROM phrase_rules p
            INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
            WHERE r.rule_code = @rule_code
              AND p.normalized_phrase = @phrase;
            """;
        verify.Parameters.AddWithValue("@case_id", caseId);
        verify.Parameters.AddWithValue("@phrase", trigger);
        verify.Parameters.AddWithValue(
            "@rule_code",
            ModerationRuleCodes.GeneralPhrase);
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal(ModerationRuleCodes.GeneralPhrase, reader.GetString(4));
        Assert.Equal(ModerationRuleCodes.GeneralPhrase, reader.GetString(5));
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(
            phraseFeedbackCountBefore,
            await ReadPhraseFeedbackCountAsync(database));
    }

    [Fact]
    public async Task PersonalDataRule_NeverLearnsAcceptedTriggerPhrase()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string trigger = "user@example.com";
        var fixture = await CreateFixtureAsync(
            database,
            "personal-data-channel",
            $"Kontakt: {trigger}");
        var caseId = await fixture.Repository.AddAsync(
            fixture.Message,
            new ValidatedModerationDecision(
                "m1",
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                PolicySetCode.General,
                "Veröffentlichung persönlicher Daten.",
                [trigger],
                Confidence: 0.98,
                RuleCode: ModerationRuleCodes.PersonalData),
            fixture.Policy.RevisionFingerprint);

        var result = await CreateExecutor(
                database,
                fixture,
                new ConfigurableHelixClient())
            .ExecuteAsync(
                new ModerationExecutionRequest(
                    caseId,
                    ExpectedRowVersion: 1,
                    ModerationActionKind.Delete,
                    TimeoutSeconds: null,
                    string.Empty,
                    PolicySetCode.General,
                    [trigger],
                    RuleCode: ModerationRuleCodes.PersonalData));

        Assert.Equal(ModerationExecutionOutcome.Succeeded, result.Outcome);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM phrase_feedback WHERE case_id = @case_id),
                (SELECT COUNT(*)
                 FROM phrase_rules p
                 INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
                 WHERE r.rule_code = @rule_code
                   AND p.normalized_phrase = @phrase),
                (SELECT rule_code
                 FROM moderation_feedback
                 WHERE case_id = @case_id);
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        command.Parameters.AddWithValue("@rule_code", ModerationRuleCodes.PersonalData);
        command.Parameters.AddWithValue("@phrase", trigger);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(0, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal(ModerationRuleCodes.PersonalData, reader.GetString(2));
    }

    [Fact]
    public async Task InactiveEventSubBarrier_RejectsWithoutCompletingPendingCase()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await CreateFixtureAsync(
            database,
            "inactive-eventsub-channel",
            "message under review");
        var caseId = await fixture.Repository.AddAsync(
            fixture.Message,
            Decision(ModerationActionKind.Delete, null, []),
            fixture.Policy.RevisionFingerprint);
        var helix = new ConfigurableHelixClient();
        var executor = CreateExecutor(
            database,
            fixture,
            helix,
            new ThrowingEventSubBarrier());

        var result = await executor.ExecuteAsync(
            new ModerationExecutionRequest(
                caseId,
                ExpectedRowVersion: 1,
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                string.Empty,
                PolicySetCode.General,
                [],
                RuleCode: ModerationRuleCodes.GeneralPhrase));

        Assert.Equal(ModerationExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal("eventsub_barrier_failed", result.StatusCode);
        var pending = await fixture.Repository.GetAsync(caseId);
        Assert.NotNull(pending);
        Assert.Equal(CoreModerationCaseStatus.Pending, pending.Status);
        Assert.Equal(1, pending.RowVersion);
        Assert.Equal(0, helix.DeleteCalls);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM moderation_feedback WHERE case_id = @case_id;";
        command.Parameters.AddWithValue("@case_id", caseId);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task RestartGap_MakesOldPendingCaseReviewableWithoutTwitchAction()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await CreateFixtureAsync(
            database,
            "restart-gap-channel",
            "old message under review");
        var caseId = await fixture.Repository.AddAsync(
            fixture.Message,
            Decision(ModerationActionKind.Delete, null, []),
            fixture.Policy.RevisionFingerprint);
        await fixture.Availability.RecordOfflineGapForPendingCasesAsync(
            DateTimeOffset.UtcNow.AddSeconds(1));
        var assessment = await fixture.Availability.AssessAsync(
            fixture.Message.Identity.ChannelId,
            fixture.Message.Identity.TwitchMessageId,
            fixture.Message.Identity.TwitchUserId,
            fixture.Message.Identity.ReceivedAtUtc);
        Assert.Equal(MessageAvailability.UnknownAfterEventGap, assessment.Availability);

        var barrier = new ThrowingEventSubBarrier();
        var helix = new ConfigurableHelixClient();
        var executor = CreateExecutor(database, fixture, helix, barrier);
        var result = await executor.ExecuteAsync(
            new ModerationExecutionRequest(
                caseId,
                ExpectedRowVersion: 1,
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                string.Empty,
                PolicySetCode.General,
                [],
                RuleCode: ModerationRuleCodes.GeneralPhrase));

        Assert.Equal(ModerationExecutionOutcome.AlreadyUnavailable, result.Outcome);
        Assert.Equal(0, barrier.CallCount);
        Assert.Equal(0, helix.DeleteCalls);
        var completed = await fixture.Repository.GetAsync(caseId);
        Assert.NotNull(completed);
        Assert.Equal(CoreModerationCaseStatus.ExternallyCompleted, completed.Status);
    }

    [Fact]
    public async Task PreviousSessionCleaner_RemovesPendingProposalWithoutLearning()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await CreateFixtureAsync(
            database,
            "previous-session-channel",
            "old proposal");
        var caseId = await fixture.Repository.AddAsync(
            fixture.Message,
            Decision(ModerationActionKind.Delete, null, ["old proposal"]),
            fixture.Policy.RevisionFingerprint);
        var cleaner = new PreviousSessionReviewCleaner(database.ConnectionFactory);

        var closed = await cleaner.CloseAsync(DateTimeOffset.UtcNow);

        Assert.Equal(1, closed);
        var discarded = await fixture.Repository.GetAsync(caseId);
        Assert.NotNull(discarded);
        Assert.Equal(CoreModerationCaseStatus.Discarded, discarded.Status);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT eligible_for_learning, human_approved, twitch_executed,
                   learning_accepted, review_completed, discard_reason,
                   (SELECT COUNT(*) FROM phrase_feedback WHERE case_id = @case_id)
            FROM moderation_feedback
            WHERE case_id = @case_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.False(reader.GetBoolean(3));
        Assert.True(reader.GetBoolean(4));
        Assert.Equal("Beim App-Neustart automatisch verworfen.", reader.GetString(5));
        Assert.Equal(0, reader.GetInt32(6));
    }

    [Fact]
    public async Task DiscardWithoutLearning_ClosesTestProposalWithoutFeedbackSignal()
    {
        await using var database = await TestDatabase.CreateAsync();
        var fixture = await CreateFixtureAsync(
            database,
            "test-abort-channel",
            "test trigger");
        var caseId = await fixture.Repository.AddAsync(
            fixture.Message,
            Decision(ModerationActionKind.Delete, null, ["test trigger"]),
            fixture.Policy.RevisionFingerprint);
        var service = new SqliteModerationReviewFeedbackService(
            fixture.Repository,
            database.ConnectionFactory,
            fixture.InfrastructureNormalizer);

        var result = await service.DiscardAsync(
            caseId,
            expectedRowVersion: 1,
            discardReason: "Testvorschlag abgebrochen",
            feedbackPolicySet: null,
            feedbackRuleCode: null,
            feedbackTriggerPhrases: ["test trigger"],
            learnAsAllowed: false);

        Assert.Equal(ModerationExecutionOutcome.Succeeded, result.Outcome);
        Assert.False(result.HumanApproved);
        Assert.False(result.LearningAccepted);
        var discarded = await fixture.Repository.GetAsync(caseId);
        Assert.NotNull(discarded);
        Assert.Equal(CoreModerationCaseStatus.Discarded, discarded.Status);
        Assert.Equal(ModerationRuleCodes.GeneralPhrase, discarded.RuleCode);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT eligible_for_learning, learning_accepted, human_approved,
                   operator_decision, actual_action,
                   actual_timeout_seconds, actual_reason, rule_code,
                   (SELECT COUNT(*) FROM phrase_feedback WHERE case_id = @case_id)
            FROM moderation_feedback
            WHERE case_id = @case_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.False(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
        Assert.Equal("discarded", reader.GetString(3));
        Assert.Equal("none", reader.GetString(4));
        Assert.True(reader.IsDBNull(5));
        Assert.True(reader.IsDBNull(6));
        Assert.True(reader.IsDBNull(7));
        Assert.Equal(0, reader.GetInt32(8));
    }

    [Fact]
    public async Task AllowedAndExternalConfirmedLearningRemainDatasetOnly()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string channelId = "discard-policy-channel";
        const string triggerPhrase = "hidden route";
        var infrastructureNormalizer =
            new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer();
        var policyStore = new SqlitePolicyStore(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var storedPolicy = await policyStore.GetEffectivePolicyAsync(channelId, "32982");
        var context = new ChannelContextSnapshot(
            channelId,
            "32982",
            "Grand Theft Auto V",
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: true);
        var policy = new EffectivePolicyResolver().Resolve(
            context,
            storedPolicy.GeneralRevision,
            Math.Max(1, storedPolicy.CategoryRevision));
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var learningRevision = new LearningContextRevision();
        var service = new SqliteModerationReviewFeedbackService(
            repository,
            database.ConnectionFactory,
            infrastructureNormalizer,
            learningContextRevision: learningRevision);
        var phraseStateBefore = await ReadPhraseStateAsync(database);
        var policyStateBefore = await ReadPolicyStateAsync(database);
        var phraseFeedbackCountBefore = await ReadPhraseFeedbackCountAsync(database);

        var discardedCaseId = await repository.AddAsync(
            CreateMessage(
                context,
                eventMessageId: "event-negative",
                twitchMessageId: "message-negative",
                originalText: "Try the hidden route"),
            new ValidatedModerationDecision(
                "m1",
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                PolicySetCode.General,
                "AI classified this as general",
                [triggerPhrase],
                Confidence: 0.8,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            policy.RevisionFingerprint);

        var discarded = await service.DiscardAsync(
            discardedCaseId,
            expectedRowVersion: 1,
            discardReason: "Backseat-Hinweis verworfen",
            feedbackPolicySet: PolicySetCode.Backseatgaming,
            feedbackRuleCode: ModerationRuleCodes.Backseatgaming,
            feedbackTriggerPhrases: [triggerPhrase]);

        Assert.Equal(ModerationExecutionOutcome.Succeeded, discarded.Outcome);
        Assert.True(discarded.LearningAccepted);
        Assert.Equal(2, learningRevision.Current);
        Assert.Null(
            await ReadPhraseAsync(
                database,
                "backseatgaming",
                triggerPhrase));
        Assert.Null(
            await ReadPhraseAsync(
                database,
                "general",
                triggerPhrase));

        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT c.policy_set, f.actual_action, f.rule_code,
                       f.learning_accepted,
                       (SELECT COUNT(*)
                        FROM phrase_feedback
                        WHERE case_id = @case_id)
                FROM moderation_feedback f
                INNER JOIN moderation_cases c ON c.case_id = f.case_id
                WHERE f.case_id = @case_id;
                """;
            command.Parameters.AddWithValue("@case_id", discardedCaseId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("backseatgaming", reader.GetString(0));
            Assert.Equal("none", reader.GetString(1));
            Assert.Equal(GlobalPolicyStorage.BackseatgamingRuleCode, reader.GetString(2));
            Assert.True(reader.GetBoolean(3));
            Assert.Equal(0, reader.GetInt32(4));
            Assert.False(await reader.ReadAsync());
        }
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(
            phraseFeedbackCountBefore,
            await ReadPhraseFeedbackCountAsync(database));

        var retrievalPolicy = policy with
        {
            ActiveRules =
            [
                new ModerationRuleDefinition(
                    ModerationRuleCodes.Backseatgaming,
                    PolicySetCode.Backseatgaming,
                    "Backseatgaming",
                    "Backseatgaming erkennen.",
                    ModerationActionKind.Timeout,
                    600,
                    "Unaufgefordertes Backseatgaming."),
            ],
        };
        var allowedExamples = await new SqliteRetrievalExampleStore(
                database.ConnectionFactory)
            .GetRelevantAsync(channelId, context.CategoryId, retrievalPolicy);
        Assert.Contains(
            allowedExamples,
            example =>
                example.Action == ModerationActionKind.None &&
                !example.IsPositive &&
                example.PolicySet == PolicySetCode.Backseatgaming &&
                example.LowerText == "try the hidden route");

        var confirmedCaseId = await repository.AddAsync(
            CreateMessage(
                context,
                eventMessageId: "event-positive",
                twitchMessageId: "message-positive",
                originalText: "Use the hidden route"),
            new ValidatedModerationDecision(
                "m2",
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                PolicySetCode.Backseatgaming,
                "Confirmed backseat hint",
                [triggerPhrase],
                Confidence: 0.9,
                RuleCode: ModerationRuleCodes.Backseatgaming),
            policy.RevisionFingerprint);
        Assert.True(
            await repository.TryTransitionAsync(
                confirmedCaseId,
                expectedRowVersion: 1,
                CoreModerationCaseStatus.Pending,
                CoreModerationCaseStatus.ExternallyCompleted));

        var confirmed = await service.CompleteUnavailableAsync(
            new ModerationExecutionRequest(
                confirmedCaseId,
                ExpectedRowVersion: 2,
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                string.Empty,
                PolicySetCode.Backseatgaming,
                [triggerPhrase],
                RuleCode: ModerationRuleCodes.Backseatgaming),
            learn: true);

        Assert.Equal(ModerationExecutionOutcome.AlreadyUnavailable, confirmed.Outcome);
        Assert.True(confirmed.LearningAccepted);
        Assert.Equal(3, learningRevision.Current);
        Assert.Null(
            await ReadPhraseAsync(
                database,
                "backseatgaming",
                triggerPhrase));
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT review_completed, learning_accepted,
                       actual_action, rule_code,
                       (SELECT COUNT(*)
                        FROM phrase_feedback
                        WHERE case_id = @case_id)
                FROM moderation_feedback
                WHERE case_id = @case_id;
                """;
            command.Parameters.AddWithValue("@case_id", confirmedCaseId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.Equal("delete", reader.GetString(2));
            Assert.Equal(ModerationRuleCodes.Backseatgaming, reader.GetString(3));
            Assert.Equal(0, reader.GetInt32(4));
        }
        var learnedExamples = await new SqliteRetrievalExampleStore(
                database.ConnectionFactory)
            .GetRelevantAsync(channelId, context.CategoryId, retrievalPolicy);
        Assert.Contains(
            learnedExamples,
            example =>
                example.IsPositive &&
                example.PolicySet == PolicySetCode.Backseatgaming &&
                example.LowerText == "use the hidden route");
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(
            phraseFeedbackCountBefore,
            await ReadPhraseFeedbackCountAsync(database));
    }

    private static async Task<Fixture> CreateFixtureAsync(
        TestDatabase database,
        string channelId,
        string originalText)
    {
        var infrastructureNormalizer =
            new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer();
        var policyStore = new SqlitePolicyStore(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var storedPolicy = await policyStore.GetEffectivePolicyAsync(
            channelId,
            EffectivePolicyResolver.BootstrapJustChattingCategoryId);
        var context = new ChannelContextSnapshot(
            channelId,
            EffectivePolicyResolver.BootstrapJustChattingCategoryId,
            "Just Chatting",
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: true);
        var resolvedPolicy = new EffectivePolicyResolver().Resolve(
            context,
            storedPolicy.GeneralRevision,
            backseatgamingRevision: 1);
        var policy = resolvedPolicy with
        {
            ActiveRules = storedPolicy.Rules
                .Select(rule => new ModerationRuleDefinition(
                    rule.RuleCode,
                    string.Equals(
                        rule.RuleCode,
                        ModerationRuleCodes.Backseatgaming,
                        StringComparison.Ordinal)
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
                    ModerationRuleCodes.AllowsTriggerLearning(rule.RuleCode)))
                .Where(rule => resolvedPolicy.Contains(rule.PolicySet))
                .ToArray(),
        };
        var identity = ChatMessageIdentityV1.Create(
            $"event-{channelId}",
            $"message-{channelId}",
            channelId,
            $"user-{channelId}",
            "viewer",
            "Viewer",
            originalText,
            DateTimeOffset.UtcNow);
        var message = new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(originalText),
            context,
            IngressSequence: 1);
        return new Fixture(
            infrastructureNormalizer,
            policyStore,
            context,
            policy,
            message,
            new CoreModerationCaseRepository(
                database.ConnectionFactory,
                infrastructureNormalizer),
            new SqliteMessageAvailabilityStore(
                database.ConnectionFactory,
                infrastructureNormalizer));
    }

    private static TwitchModerationExecutor CreateExecutor(
        TestDatabase database,
        Fixture fixture,
        ITwitchHelixClient helix,
        IEventSubProcessingBarrier? eventSubBarrier = null)
    {
        return new TwitchModerationExecutor(
            fixture.Repository,
            fixture.Availability,
            new FixedChannelContextService(fixture.Context),
            new EffectivePolicyResolver(),
            fixture.PolicyStore,
            database.ConnectionFactory,
            fixture.InfrastructureNormalizer,
            helix,
            new InfrastructureOptions
            {
                DatabasePath = database.Path,
                TwitchApi = new TwitchApiOptions
                {
                    ModeratorUserId = "moderator-1",
                },
            },
            eventSubBarrier);
    }

    private static ValidatedModerationDecision Decision(
        ModerationActionKind action,
        int? timeoutSeconds,
        IReadOnlyList<string> triggerPhrases)
    {
        return new ValidatedModerationDecision(
            "m1",
            action,
            timeoutSeconds,
            PolicySetCode.General,
            "model reason",
            triggerPhrases,
            Confidence: 0.9,
            RuleCode: ModerationRuleCodes.GeneralPhrase);
    }

    private static ChatMessageSnapshot CreateMessage(
        ChannelContextSnapshot context,
        string eventMessageId,
        string twitchMessageId,
        string originalText)
    {
        var identity = ChatMessageIdentityV1.Create(
            eventMessageId,
            twitchMessageId,
            context.ChannelId,
            $"user-{twitchMessageId}",
            "viewer",
            "Viewer",
            originalText,
            DateTimeOffset.UtcNow);
        return new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(originalText),
            context,
            IngressSequence: 1);
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
        var state = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            state.Add(reader.GetString(0));
        }

        return state;
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
        var state = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            state.Add(reader.GetString(0));
        }

        return state;
    }

    private static async Task<long> ReadPhraseFeedbackCountAsync(
        TestDatabase database)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM phrase_feedback;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<PersistedPhrase?> ReadPhraseAsync(
        TestDatabase database,
        string policySet,
        string normalizedPhrase)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT p.phrase_id, r.rule_code, p.normalized_phrase, p.manual_override,
                   p.positive_confirmations, p.negative_confirmations,
                   p.archived_utc
            FROM phrase_rules p
            INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
            INNER JOIN policy_sets s ON s.scope_id = r.scope_id
            WHERE s.broadcaster_id = @global_scope
              AND s.set_code = @policy_set
              AND p.normalized_phrase = @normalized_phrase
              AND p.archived_utc IS NULL;
            """;
        command.Parameters.AddWithValue(
            "@global_scope",
            GlobalPolicyStorage.BroadcasterId);
        command.Parameters.AddWithValue("@policy_set", policySet);
        command.Parameters.AddWithValue("@normalized_phrase", normalizedPhrase);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new PersistedPhrase(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6))
            : null;
    }

    private sealed record Fixture(
        TwitchAI.Infrastructure.Persistence.ChatTextNormalizer InfrastructureNormalizer,
        SqlitePolicyStore PolicyStore,
        ChannelContextSnapshot Context,
        CoreEffectivePolicySnapshot Policy,
        ChatMessageSnapshot Message,
        CoreModerationCaseRepository Repository,
        SqliteMessageAvailabilityStore Availability);

    private sealed record PersistedPhrase(
        long PhraseId,
        string RuleCode,
        string RuleNormalizedPhrase,
        string ManualOverride,
        int PositiveConfirmations,
        int NegativeConfirmations,
        string? ArchivedUtc);

    private sealed class ThrowingEventSubBarrier : IEventSubProcessingBarrier
    {
        public int CallCount { get; private set; }

        public Task DrainAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("EventSub ist nicht aktiv.");
        }
    }

    private sealed class FixedChannelContextService(ChannelContextSnapshot context)
        : IChannelContextService
    {
        public ValueTask<ChannelContextSnapshot> GetCurrentAsync(
            string channelId,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(context.ChannelId, channelId);
            return ValueTask.FromResult(context);
        }
    }

    private sealed class ConfigurableHelixClient : ITwitchHelixClient
    {
        public TwitchHelixException? TimeoutFailure { get; init; }

        public int DeleteCalls { get; private set; }

        public int TimeoutCalls { get; private set; }

        public Task<TwitchUserInfo> GetCurrentUserAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new TwitchUserInfo("moderator-1", "moderator", "Moderator"));
        }

        public Task<TwitchChannelInfo?> GetChannelAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TwitchChannelInfo?>(null);
        }

        public Task<IReadOnlyList<TwitchGameInfo>> GetGamesAsync(
            IReadOnlyCollection<string> categoryIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TwitchGameInfo>>([]);
        }

        public Task<TwitchGameInfo?> GetGameByNameAsync(
            string categoryName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TwitchGameInfo?>(null);
        }

        public Task<IReadOnlyList<TwitchModeratedChannel>> GetModeratedChannelsAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TwitchModeratedChannel>>([]);
        }

        public Task<TwitchBannedUserInfo?> GetBannedUserAsync(
            string broadcasterId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TwitchBannedUserInfo?>(null);
        }

        public Task DeleteMessageAsync(
            string broadcasterId,
            string moderatorId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            return Task.CompletedTask;
        }

        public Task TimeoutUserAsync(
            string broadcasterId,
            string moderatorId,
            string userId,
            int durationSeconds,
            string reason,
            CancellationToken cancellationToken = default)
        {
            TimeoutCalls++;
            return TimeoutFailure is null
                ? Task.CompletedTask
                : Task.FromException(TimeoutFailure);
        }

        public Task BanUserAsync(
            string broadcasterId,
            string moderatorId,
            string userId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<TwitchSendChatMessageResult> SendChatMessageAsync(
            string broadcasterId,
            string senderId,
            string message,
            string? replyParentMessageId = null,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "Moderation persistence tests must not send chat messages.");
        }

        public Task CreateEventSubSubscriptionAsync(
            TwitchEventSubSubscriptionRequest subscription,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
