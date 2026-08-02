using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Twitch;
using CoreChatTextNormalizer = TwitchAI.Core.Services.ChatTextNormalizer;
using CoreModerationCaseStatus = TwitchAI.Core.Models.ModerationCaseStatus;

namespace TwitchAI.Tests.Infrastructure;

public sealed class TwitchModerationExecutorTests
{
    [Theory]
    [InlineData("delete")]
    [InlineData("timeout")]
    [InlineData("ban")]
    public async Task ExecuteAsync_ReadOnlyLearnsWithoutCallingTwitch(string actionName)
    {
        await using var database = await TestDatabase.CreateAsync();
        const string channelId = "read-only-channel";
        var infrastructureNormalizer =
            new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer();
        var policyStore = new SqlitePolicyStore(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var persistedPolicy = await policyStore.GetEffectivePolicyAsync(
            channelId,
            "509658");
        var context = new ChannelContextSnapshot(
            channelId,
            "509658",
            "Just Chatting",
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: true);
        var corePolicy = new EffectivePolicyResolver().Resolve(
            context,
            persistedPolicy.GeneralRevision,
            backseatgamingRevision: 1);
        var identity = ChatMessageIdentityV1.Create(
            $"event-read-only-{actionName}",
            $"message-read-only-{actionName}",
            channelId,
            "viewer-id",
            "viewer",
            "Viewer",
            "verbotene testnachricht",
            DateTimeOffset.UtcNow);
        var snapshot = new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(identity.OriginalText),
            context,
            IngressSequence: 1);
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var action = actionName switch
        {
            "delete" => ModerationActionKind.Delete,
            "timeout" => ModerationActionKind.Timeout,
            "ban" => ModerationActionKind.Ban,
            _ => throw new InvalidOperationException(),
        };
        int? timeoutSeconds = action == ModerationActionKind.Timeout ? 600 : null;
        var reason = action == ModerationActionKind.Delete
            ? string.Empty
            : "Testverstoß gegen die Chatregeln.";
        var caseId = await repository.AddAsync(
            snapshot,
            new ValidatedModerationDecision(
                "temporary-read-only",
                action,
                timeoutSeconds,
                PolicySetCode.General,
                reason,
                ["verbotene testnachricht"],
                Confidence: 0.9,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            corePolicy.RevisionFingerprint);
        var helix = new RecordingHelixClient();
        var learningRevision = new LearningContextRevision();
        var executor = new TwitchModerationExecutor(
            repository,
            new SqliteMessageAvailabilityStore(
                database.ConnectionFactory,
                infrastructureNormalizer),
            new FixedChannelContextService(context),
            new EffectivePolicyResolver(),
            policyStore,
            database.ConnectionFactory,
            infrastructureNormalizer,
            helix,
            new InfrastructureOptions
            {
                DatabasePath = database.Path,
                TwitchApi = new TwitchApiOptions
                {
                    BroadcasterId = channelId,
                },
            },
            runtimeMode: new TwitchRuntimeModeSettings(isReadOnly: true),
            learningContextRevision: learningRevision);
        var phraseStateBefore = await ReadPhraseStateAsync(database);
        var policyStateBefore = await ReadPolicyStateAsync(database);
        var phraseFeedbackCountBefore = await ReadPhraseFeedbackCountAsync(database);

        var result = await executor.ExecuteAsync(
            new ModerationExecutionRequest(
                caseId,
                ExpectedRowVersion: 1,
                action,
                timeoutSeconds,
                reason,
                PolicySetCode.General,
                ["verbotene testnachricht"],
                RuleCode: ModerationRuleCodes.GeneralPhrase));

        Assert.Equal(ModerationExecutionOutcome.Succeeded, result.Outcome);
        Assert.True(result.HumanApproved);
        Assert.False(result.TwitchExecuted);
        Assert.True(result.LearningAccepted);
        Assert.Equal("read_only_learning_saved", result.StatusCode);
        Assert.Equal(2, learningRevision.Current);
        Assert.Null(helix.Delete);
        Assert.Null(helix.Timeout);
        Assert.Null(helix.Ban);
        Assert.Equal(0, helix.GetBannedUserCalls);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.status, f.actual_action, f.actual_timeout_seconds,
                   f.actual_reason, f.twitch_executed, f.learning_accepted,
                   f.eligible_for_learning,
                   (SELECT COUNT(*) FROM action_attempts WHERE case_id = @case_id),
                   (SELECT COUNT(*) FROM phrase_feedback WHERE case_id = @case_id)
            FROM moderation_cases c
            INNER JOIN moderation_feedback f ON f.case_id = c.case_id
            WHERE c.case_id = @case_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("executed", reader.GetString(0));
        Assert.Equal(actionName, reader.GetString(1));
        Assert.Equal(
            timeoutSeconds,
            reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2));
        Assert.Equal(reason.Length == 0 ? null : reason, reader.IsDBNull(3) ? null : reader.GetString(3));
        Assert.False(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.True(reader.GetBoolean(6));
        Assert.Equal(0, reader.GetInt32(7));
        Assert.Equal(0, reader.GetInt32(8));
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(
            phraseFeedbackCountBefore,
            await ReadPhraseFeedbackCountAsync(database));
    }

    [Fact]
    public async Task ExecuteAsync_ReadOnlyLearningKeepsPolicyStableForAnotherPendingProposal()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string channelId = "read-only-concurrent-channel";
        var infrastructureNormalizer =
            new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer();
        var policyStore = new SqlitePolicyStore(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var persistedPolicy = await policyStore.GetEffectivePolicyAsync(
            channelId,
            "509658");
        var context = new ChannelContextSnapshot(
            channelId,
            "509658",
            "Just Chatting",
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: true);
        var corePolicy = new EffectivePolicyResolver().Resolve(
            context,
            persistedPolicy.GeneralRevision,
            backseatgamingRevision: 1);
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory,
            infrastructureNormalizer);

        ChatMessageSnapshot CreateMessage(string suffix, string text)
        {
            var identity = ChatMessageIdentityV1.Create(
                $"event-{suffix}",
                $"message-{suffix}",
                channelId,
                $"viewer-{suffix}",
                $"viewer_{suffix}",
                $"Viewer {suffix}",
                text,
                DateTimeOffset.UtcNow);
            return new ChatMessageSnapshot(
                identity,
                new CoreChatTextNormalizer().Process(identity.OriginalText),
                context,
                IngressSequence: suffix == "first" ? 1 : 2);
        }

        var firstTrigger = "erste verbotene triggerphrase";
        var secondTrigger = "zweite verbotene triggerphrase";
        var firstCaseId = await repository.AddAsync(
            CreateMessage("first", firstTrigger),
            new ValidatedModerationDecision(
                "temporary-first",
                ModerationActionKind.Timeout,
                TimeoutSeconds: 600,
                PolicySetCode.General,
                "Erster Vorschlag.",
                [firstTrigger],
                Confidence: 0.9,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            corePolicy.RevisionFingerprint);
        var secondCaseId = await repository.AddAsync(
            CreateMessage("second", secondTrigger),
            new ValidatedModerationDecision(
                "temporary-second",
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                PolicySetCode.General,
                string.Empty,
                [secondTrigger],
                Confidence: 0.9,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            corePolicy.RevisionFingerprint);

        var helix = new RecordingHelixClient();
        var executor = new TwitchModerationExecutor(
            repository,
            new SqliteMessageAvailabilityStore(
                database.ConnectionFactory,
                infrastructureNormalizer),
            new FixedChannelContextService(context),
            new EffectivePolicyResolver(),
            policyStore,
            database.ConnectionFactory,
            infrastructureNormalizer,
            helix,
            new InfrastructureOptions
            {
                DatabasePath = database.Path,
                TwitchApi = new TwitchApiOptions
                {
                    BroadcasterId = channelId,
                },
            },
            runtimeMode: new TwitchRuntimeModeSettings(isReadOnly: true));
        var phraseStateBefore = await ReadPhraseStateAsync(database);
        var policyStateBefore = await ReadPolicyStateAsync(database);
        var phraseFeedbackCountBefore = await ReadPhraseFeedbackCountAsync(database);

        var firstResult = await executor.ExecuteAsync(
            new ModerationExecutionRequest(
                firstCaseId,
                ExpectedRowVersion: 1,
                ModerationActionKind.Timeout,
                TimeoutSeconds: 600,
                "Erster Vorschlag.",
                PolicySetCode.General,
                [firstTrigger],
                RuleCode: ModerationRuleCodes.GeneralPhrase));

        Assert.Equal(ModerationExecutionOutcome.Succeeded, firstResult.Outcome);
        var policyAfterLearning = await policyStore.GetEffectivePolicyAsync(
            channelId,
            "509658");
        Assert.Equal(
            persistedPolicy.GeneralRevision,
            policyAfterLearning.GeneralRevision);
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(
            phraseFeedbackCountBefore,
            await ReadPhraseFeedbackCountAsync(database));

        var secondResult = await executor.ExecuteAsync(
            new ModerationExecutionRequest(
                secondCaseId,
                ExpectedRowVersion: 1,
                ModerationActionKind.Timeout,
                TimeoutSeconds: 1_200,
                "Manuell bearbeiteter, weiterhin gültiger Grund.",
                PolicySetCode.General,
                [secondTrigger],
                RuleCode: ModerationRuleCodes.GeneralPhrase));

        Assert.Equal(ModerationExecutionOutcome.Succeeded, secondResult.Outcome);
        Assert.True(secondResult.LearningAccepted);
        Assert.False(secondResult.TwitchExecuted);
        Assert.Null(helix.Delete);
        Assert.Null(helix.Timeout);
        Assert.Null(helix.Ban);
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(
            phraseFeedbackCountBefore,
            await ReadPhraseFeedbackCountAsync(database));
    }

    [Fact]
    public async Task ExecuteAsync_PersistsEditedFeedbackWithoutMutatingPolicyPhrases()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string channelId = "channel-1";
        var infrastructureNormalizer = new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer();
        var policyStore = new SqlitePolicyStore(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var persistedPolicy = await policyStore.GetEffectivePolicyAsync(channelId, "32982");
        var context = new ChannelContextSnapshot(
            channelId,
            "32982",
            "Grand Theft Auto V",
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: true);
        var corePolicy = new EffectivePolicyResolver().Resolve(
            context,
            persistedPolicy.GeneralRevision,
            Math.Max(1, persistedPolicy.CategoryRevision));
        var identity = ChatMessageIdentityV1.Create(
            "event-1",
            "message-1",
            channelId,
            "user-1",
            "viewer",
            "Viewer",
            "spoiler",
            DateTimeOffset.UtcNow);
        var message = new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(identity.OriginalText),
            context,
            IngressSequence: 1);
        var caseRepository = new CoreModerationCaseRepository(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var caseId = await caseRepository.AddAsync(
            message,
            new ValidatedModerationDecision(
                "temporary-1",
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                PolicySetCode.General,
                "original reason",
                ["spoiler", "other"],
                Confidence: 0.9,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            corePolicy.RevisionFingerprint);
        var helix = new RecordingHelixClient();
        var learningRevision = new LearningContextRevision();
        var executor = new TwitchModerationExecutor(
            caseRepository,
            new SqliteMessageAvailabilityStore(
                database.ConnectionFactory,
                infrastructureNormalizer),
            new FixedChannelContextService(context),
            new EffectivePolicyResolver(),
            policyStore,
            database.ConnectionFactory,
            infrastructureNormalizer,
            helix,
            new InfrastructureOptions
            {
                DatabasePath = database.Path,
                TwitchApi = new TwitchApiOptions
                {
                    BroadcasterId = channelId,
                    ModeratorUserId = "moderator-1",
                },
            },
            learningContextRevision: learningRevision);
        var phraseStateBefore = await ReadPhraseStateAsync(database);
        var policyStateBefore = await ReadPolicyStateAsync(database);
        var phraseFeedbackCountBefore = await ReadPhraseFeedbackCountAsync(database);

        var result = await executor.ExecuteAsync(
            new ModerationExecutionRequest(
                caseId,
                ExpectedRowVersion: 1,
                ModerationActionKind.Timeout,
                TimeoutSeconds: 42,
                "edited reason",
                PolicySetCode.Backseatgaming,
                ["spoiler"],
                RuleCode: ModerationRuleCodes.Backseatgaming));

        Assert.Equal(ModerationExecutionOutcome.Succeeded, result.Outcome);
        Assert.True(result.TwitchExecuted);
        Assert.Equal(2, learningRevision.Current);
        Assert.Equal(0, helix.GetBannedUserCalls);
        Assert.Equal(
            (channelId, "moderator-1", "user-1", 42, "edited reason"),
            helix.Timeout);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT a.action, a.timeout_seconds, a.reason,
                   f.actual_action, f.actual_timeout_seconds, f.actual_reason,
                   c.status, a.rule_code, f.rule_code, c.violation_rule_code
            FROM action_attempts a
            INNER JOIN moderation_feedback f ON f.case_id = a.case_id
            INNER JOIN moderation_cases c ON c.case_id = a.case_id
            WHERE a.case_id = @case_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("timeout", reader.GetString(0));
        Assert.Equal(42, reader.GetInt32(1));
        Assert.Equal("edited reason", reader.GetString(2));
        Assert.Equal("timeout", reader.GetString(3));
        Assert.Equal(42, reader.GetInt32(4));
        Assert.Equal("edited reason", reader.GetString(5));
        Assert.Equal("executed", reader.GetString(6));
        Assert.Equal(ModerationRuleCodes.Backseatgaming, reader.GetString(7));
        Assert.Equal(ModerationRuleCodes.Backseatgaming, reader.GetString(8));
        Assert.Equal(ModerationRuleCodes.Backseatgaming, reader.GetString(9));

        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(
            phraseFeedbackCountBefore,
            await ReadPhraseFeedbackCountAsync(database));
    }

    [Fact]
    public async Task ExecuteAsync_BanPersistsReasonAndRecordsUserClear()
    {
        await using var database = await TestDatabase.CreateAsync();
        var helix = new RecordingHelixClient();
        var fixture = await CreateBanFixtureAsync(database, helix);

        var result = await fixture.Executor.ExecuteAsync(
            CreateBanRequest(fixture.CaseId));

        Assert.Equal(ModerationExecutionOutcome.Succeeded, result.Outcome);
        Assert.True(result.TwitchExecuted);
        Assert.Equal(0, helix.GetBannedUserCalls);
        Assert.Equal(
            (
                "channel-ban",
                "moderator-1",
                "user-ban",
                "Schwere gezielte Beleidigung."),
            helix.Ban);
        Assert.Null(helix.Timeout);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT a.action, a.timeout_seconds, a.reason,
                   f.actual_action, f.actual_timeout_seconds, f.actual_reason,
                   w.availability, w.eventsub_message_id
            FROM action_attempts a
            INNER JOIN moderation_feedback f ON f.case_id = a.case_id
            INNER JOIN message_clear_watermarks w
                ON w.broadcaster_id = a.broadcaster_id
               AND w.target_user_id = a.target_user_id
               AND w.clear_kind = 'user'
            WHERE a.case_id = @case_id;
            """;
        command.Parameters.AddWithValue("@case_id", fixture.CaseId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("ban", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal("Schwere gezielte Beleidigung.", reader.GetString(2));
        Assert.Equal("ban", reader.GetString(3));
        Assert.True(reader.IsDBNull(4));
        Assert.Equal("Schwere gezielte Beleidigung.", reader.GetString(5));
        Assert.Equal("cleared_for_user", reader.GetString(6));
        Assert.StartsWith("local-ban:", reader.GetString(7), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_BanEscalatesActiveTimeoutWhileMessageRemainsAvailable()
    {
        await using var database = await TestDatabase.CreateAsync();
        var helix = new RecordingHelixClient
        {
            ExistingBan = new TwitchBannedUserInfo(
                "user-ban",
                "viewer",
                "Viewer",
                DateTimeOffset.UtcNow.AddMinutes(5)),
        };
        var fixture = await CreateBanFixtureAsync(
            database,
            helix,
            moderatorUserId: "channel-ban");

        var result = await fixture.Executor.ExecuteAsync(
            CreateBanRequest(fixture.CaseId));

        Assert.Equal(ModerationExecutionOutcome.Succeeded, result.Outcome);
        Assert.True(result.TwitchExecuted);
        Assert.NotNull(helix.Ban);
        Assert.Null(helix.Timeout);
    }

    [Fact]
    public async Task ExecuteAsync_UserClearBlocksBanDespiteActiveTemporaryTimeout()
    {
        await using var database = await TestDatabase.CreateAsync();
        var helix = new RecordingHelixClient
        {
            ExistingBan = new TwitchBannedUserInfo(
                "user-ban",
                "viewer",
                "Viewer",
                DateTimeOffset.UtcNow.AddMinutes(5)),
        };
        var fixture = await CreateBanFixtureAsync(
            database,
            helix,
            moderatorUserId: "channel-ban");
        var availabilityStore = new SqliteMessageAvailabilityStore(
            database.ConnectionFactory,
            new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer());
        await availabilityStore.RecordUserClearAsync(
            new TwitchUserMessagesClearedEvent(
                "external-timeout-clear",
                "channel-ban",
                DateTimeOffset.UtcNow.AddSeconds(1),
                "user-ban",
                "viewer",
                "Viewer"));

        var result = await fixture.Executor.ExecuteAsync(
            CreateBanRequest(fixture.CaseId));

        Assert.Equal(ModerationExecutionOutcome.AlreadyUnavailable, result.Outcome);
        Assert.False(result.TwitchExecuted);
        Assert.Null(helix.Ban);
        Assert.Null(helix.Timeout);
    }

    [Fact]
    public async Task ExecuteAsync_PermanentBanPreventsDuplicateBanRequest()
    {
        await using var database = await TestDatabase.CreateAsync();
        var helix = new RecordingHelixClient
        {
            ExistingBan = new TwitchBannedUserInfo(
                "user-ban",
                "viewer",
                "Viewer",
                ExpiresAt: null),
        };
        var fixture = await CreateBanFixtureAsync(
            database,
            helix,
            moderatorUserId: "channel-ban");

        var result = await fixture.Executor.ExecuteAsync(
            CreateBanRequest(fixture.CaseId));

        Assert.Equal(ModerationExecutionOutcome.AlreadyUnavailable, result.Outcome);
        Assert.False(result.TwitchExecuted);
        Assert.Null(helix.Ban);
        Assert.Null(helix.Timeout);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM action_attempts WHERE case_id = @case_id;";
        command.Parameters.AddWithValue("@case_id", fixture.CaseId);
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ExecuteAsync_BanWithTimeoutDurationIsRejectedBeforeTwitchCall()
    {
        await using var database = await TestDatabase.CreateAsync();
        var helix = new RecordingHelixClient();
        var fixture = await CreateBanFixtureAsync(database, helix);
        var invalidRequest = CreateBanRequest(fixture.CaseId) with
        {
            TimeoutSeconds = 600,
        };

        var result = await fixture.Executor.ExecuteAsync(invalidRequest);

        Assert.Equal(ModerationExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal("invalid_request", result.StatusCode);
        Assert.False(result.TwitchExecuted);
        Assert.Null(helix.Ban);
        Assert.Null(helix.Timeout);
    }

    [Fact]
    public async Task ExecuteAsync_CaseFromDisconnectedChannelIsBlocked()
    {
        await using var database = await TestDatabase.CreateAsync();
        var helix = new RecordingHelixClient();
        var fixture = await CreateBanFixtureAsync(database, helix);
        fixture.Options.SetBroadcasterIds(["different-channel"]);

        var result = await fixture.Executor.ExecuteAsync(
            CreateBanRequest(fixture.CaseId));

        Assert.Equal(ModerationExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal("channel_not_connected", result.StatusCode);
        Assert.False(result.TwitchExecuted);
        Assert.Null(helix.Ban);
    }

    [Theory]
    [InlineData("moderator")]
    [InlineData("lead_moderator")]
    [InlineData("vip")]
    [InlineData("bot-badge")]
    public async Task ExecuteAsync_AutomaticRoleWhitelistBlocksExistingCase(
        string badgeSetId)
    {
        await using var database = await TestDatabase.CreateAsync();
        var helix = new RecordingHelixClient();
        var fixture = await CreateBanFixtureAsync(
            database,
            helix,
            badgeSetId: badgeSetId);

        var result = await fixture.Executor.ExecuteAsync(
            CreateBanRequest(fixture.CaseId));

        Assert.Equal(
            ModerationExecutionOutcome.IntegrityBlocked,
            result.Outcome);
        Assert.Equal("sender_role_whitelisted", result.StatusCode);
        Assert.False(result.TwitchExecuted);
        Assert.Null(helix.Delete);
        Assert.Null(helix.Timeout);
        Assert.Null(helix.Ban);
    }

    [Fact]
    public async Task ExecuteAsync_WhitelistedLoginBlocksTwitchAction()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string channelId = "channel-login-whitelist";
        var infrastructureNormalizer =
            new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer();
        var policyStore = new SqlitePolicyStore(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var persistedPolicy = await policyStore.GetEffectivePolicyAsync(
            channelId,
            "509658");
        var context = new ChannelContextSnapshot(
            channelId,
            "509658",
            "Just Chatting",
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: true);
        var corePolicy = new EffectivePolicyResolver().Resolve(
            context,
            persistedPolicy.GeneralRevision,
            backseatgamingRevision: 1);
        var identity = ChatMessageIdentityV1.Create(
            "event-login-whitelist",
            "message-login-whitelist",
            channelId,
            "user-login-whitelist",
            "TargetLogin",
            "Different Display Name",
            "ordinary message",
            DateTimeOffset.UtcNow);
        var snapshot = new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(identity.OriginalText),
            context,
            IngressSequence: 1);
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var caseId = await repository.AddAsync(
            snapshot,
            new ValidatedModerationDecision(
                "temporary-login-whitelist",
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                PolicySetCode.General,
                "original reason",
                ["ordinary"],
                Confidence: 0.9,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            corePolicy.RevisionFingerprint);
        var helix = new RecordingHelixClient();
        var whitelistSource = new FixedWhitelistPhraseSource(
            exactMessages: new HashSet<string>(StringComparer.Ordinal),
            lowerUserLogins: new HashSet<string>(
                ["targetlogin"],
                StringComparer.Ordinal));
        var executor = new TwitchModerationExecutor(
            repository,
            new SqliteMessageAvailabilityStore(
                database.ConnectionFactory,
                infrastructureNormalizer),
            new FixedChannelContextService(context),
            new EffectivePolicyResolver(),
            policyStore,
            database.ConnectionFactory,
            infrastructureNormalizer,
            helix,
            new InfrastructureOptions
            {
                DatabasePath = database.Path,
                TwitchApi = new TwitchApiOptions
                {
                    BroadcasterId = channelId,
                    ModeratorUserId = "moderator-1",
                },
            },
            whitelistFilter: new WhitelistFilterAgent(whitelistSource));

        var result = await executor.ExecuteAsync(
            new ModerationExecutionRequest(
                caseId,
                ExpectedRowVersion: 1,
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                string.Empty,
                PolicySetCode.General,
                ["ordinary"],
                RuleCode: ModerationRuleCodes.GeneralPhrase));

        Assert.Equal(
            ModerationExecutionOutcome.IntegrityBlocked,
            result.Outcome);
        Assert.Equal("message_now_whitelisted", result.StatusCode);
        Assert.False(result.TwitchExecuted);
        Assert.Null(helix.Timeout);
        Assert.Equal(
            (
                WhitelistEntryKind.UserLogin,
                "targetlogin"),
            whitelistSource.RecordedHit);
        Assert.Equal(
            CoreModerationCaseStatus.IntegrityBlocked,
            (await repository.GetAsync(caseId))?.Status);
    }

    [Fact]
    public async Task CompleteUnavailableAsync_PersistsDeclinedReviewCompletionAcrossCalls()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string channelId = "channel-review";
        var infrastructureNormalizer = new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer();
        var policyStore = new SqlitePolicyStore(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var persistedPolicy = await policyStore.GetEffectivePolicyAsync(channelId, "509658");
        var context = new ChannelContextSnapshot(
            channelId,
            "509658",
            "Just Chatting",
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: true);
        var corePolicy = new EffectivePolicyResolver().Resolve(
            context,
            persistedPolicy.GeneralRevision,
            backseatgamingRevision: 1);
        var identity = ChatMessageIdentityV1.Create(
            "event-review",
            "message-review",
            channelId,
            "user-review",
            "viewer",
            "Viewer",
            "spoiler",
            DateTimeOffset.UtcNow);
        var moderationCase = new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(identity.OriginalText),
            context,
            IngressSequence: 1);
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var caseId = await repository.AddAsync(
            moderationCase,
            new ValidatedModerationDecision(
                "temporary-review",
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                PolicySetCode.General,
                "review reason",
                ["spoiler"],
                Confidence: 0.9,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            corePolicy.RevisionFingerprint);
        Assert.True(await repository.TryTransitionAsync(
            caseId,
            expectedRowVersion: 1,
            CoreModerationCaseStatus.Pending,
            CoreModerationCaseStatus.ExternallyCompleted));

        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO moderation_feedback(
                    case_id, operator_decision, actual_action,
                    eligible_for_learning, human_approved, twitch_executed,
                    learning_accepted, decided_utc)
                VALUES(
                    @case_id, 'executed', 'none',
                    0, 1, 0,
                    0, @decided_utc);
                """;
            command.Parameters.AddWithValue("@case_id", caseId);
            command.Parameters.AddWithValue("@decided_utc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var learningRevision = new LearningContextRevision();
        var service = new SqliteModerationReviewFeedbackService(
            repository,
            database.ConnectionFactory,
            infrastructureNormalizer,
            learningContextRevision: learningRevision);
        var request = new ModerationExecutionRequest(
            caseId,
            ExpectedRowVersion: 1,
            ModerationActionKind.Delete,
            TimeoutSeconds: null,
            string.Empty,
            PolicySetCode.General,
            ["spoiler"],
            RuleCode: ModerationRuleCodes.GeneralPhrase);

        var first = await service.CompleteUnavailableAsync(request, learn: false);
        var second = await service.CompleteUnavailableAsync(request, learn: false);

        Assert.Equal(ModerationExecutionOutcome.AlreadyUnavailable, first.Outcome);
        Assert.False(first.LearningAccepted);
        Assert.Equal("unavailable_review_already_saved", second.StatusCode);
        Assert.Equal(1, learningRevision.Current);
        await using var verify = await database.OpenConnectionAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText =
            "SELECT review_completed FROM moderation_feedback WHERE case_id = @case_id;";
        verifyCommand.Parameters.AddWithValue("@case_id", caseId);
        Assert.Equal(1L, await verifyCommand.ExecuteScalarAsync());
    }

    [Fact]
    public async Task ExternalDeleteLearningPersistsReviewWithoutMutatingPolicyPhrases()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string channelId = "channel-external-delete";
        var infrastructureNormalizer = new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer();
        var policyStore = new SqlitePolicyStore(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var persistedPolicy = await policyStore.GetEffectivePolicyAsync(channelId, "509658");
        var context = new ChannelContextSnapshot(
            channelId,
            "509658",
            "Just Chatting",
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: true);
        var corePolicy = new EffectivePolicyResolver().Resolve(
            context,
            persistedPolicy.GeneralRevision,
            Math.Max(1, persistedPolicy.CategoryRevision));
        var identity = ChatMessageIdentityV1.Create(
            "event-external",
            "message-external",
            channelId,
            "user-external",
            "viewer",
            "Viewer",
            "spoiler",
            DateTimeOffset.UtcNow);
        var snapshot = new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(identity.OriginalText),
            context,
            IngressSequence: 1);
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var caseId = await repository.AddAsync(
            snapshot,
            new ValidatedModerationDecision(
                "temporary-external",
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                PolicySetCode.General,
                "review reason",
                ["spoiler"],
                Confidence: 0.9,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            corePolicy.RevisionFingerprint);
        var availability = new SqliteMessageAvailabilityStore(
            database.ConnectionFactory,
            infrastructureNormalizer);

        await availability.RecordDeletedAsync(
            new TwitchMessageDeletedEvent(
                "delete-event",
                channelId,
                DateTimeOffset.UtcNow,
                identity.TwitchMessageId,
                identity.TwitchUserId,
                identity.Login,
                identity.DisplayName));

        var stillPending = await repository.GetAsync(caseId);
        Assert.NotNull(stillPending);
        Assert.Equal(CoreModerationCaseStatus.Pending, stillPending.Status);
        Assert.Equal(1, stillPending.RowVersion);

        var helix = new RecordingHelixClient();
        var executor = new TwitchModerationExecutor(
            repository,
            availability,
            new FixedChannelContextService(context),
            new EffectivePolicyResolver(),
            policyStore,
            database.ConnectionFactory,
            infrastructureNormalizer,
            helix,
            new InfrastructureOptions
            {
                DatabasePath = database.Path,
                TwitchApi = new TwitchApiOptions
                {
                    BroadcasterId = channelId,
                    ModeratorUserId = "moderator-1",
                },
            });
        var request = new ModerationExecutionRequest(
            caseId,
            ExpectedRowVersion: 1,
            ModerationActionKind.Timeout,
            TimeoutSeconds: 90,
            "edited unavailable reason",
            PolicySetCode.General,
            ["spoiler"],
            RuleCode: ModerationRuleCodes.GeneralPhrase);
        var phraseStateBefore = await ReadPhraseStateAsync(database);
        var policyStateBefore = await ReadPolicyStateAsync(database);
        var phraseFeedbackCountBefore = await ReadPhraseFeedbackCountAsync(database);

        var result = await executor.ExecuteAsync(request);

        Assert.Equal(ModerationExecutionOutcome.AlreadyUnavailable, result.Outcome);
        Assert.False(result.TwitchExecuted);
        Assert.Null(helix.Timeout);
        var externallyCompleted = await repository.GetAsync(caseId);
        Assert.NotNull(externallyCompleted);
        Assert.Equal(
            CoreModerationCaseStatus.ExternallyCompleted,
            externallyCompleted.Status);
        Assert.Equal(ModerationActionKind.Timeout, externallyCompleted.ProposedAction);
        Assert.Equal(90, externallyCompleted.ProposedTimeoutSeconds);
        Assert.Equal("edited unavailable reason", externallyCompleted.Reason);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT review_completed FROM moderation_feedback WHERE case_id = @case_id;";
        command.Parameters.AddWithValue("@case_id", caseId);
        Assert.Equal(0L, await command.ExecuteScalarAsync());

        var learningRevision = new LearningContextRevision();
        var reviewService = new SqliteModerationReviewFeedbackService(
            repository,
            database.ConnectionFactory,
            infrastructureNormalizer,
            learningContextRevision: learningRevision);
        var reviewResult = await reviewService.CompleteUnavailableAsync(
            request,
            learn: true);
        Assert.True(reviewResult.LearningAccepted);
        Assert.Equal(2, learningRevision.Current);

        await using var learnedConnection = await database.OpenConnectionAsync();
        await using var learnedCommand = learnedConnection.CreateCommand();
        learnedCommand.CommandText =
            """
            SELECT f.review_completed, f.actual_action, f.actual_timeout_seconds,
                   f.learning_accepted,
                   (SELECT COUNT(*)
                    FROM phrase_feedback
                    WHERE case_id = @case_id)
            FROM moderation_feedback f
            WHERE f.case_id = @case_id;
            """;
        learnedCommand.Parameters.AddWithValue("@case_id", caseId);
        await using var learnedReader = await learnedCommand.ExecuteReaderAsync();
        Assert.True(await learnedReader.ReadAsync());
        Assert.True(learnedReader.GetBoolean(0));
        Assert.Equal("timeout", learnedReader.GetString(1));
        Assert.Equal(90, learnedReader.GetInt32(2));
        Assert.True(learnedReader.GetBoolean(3));
        Assert.Equal(0, learnedReader.GetInt32(4));
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(
            phraseFeedbackCountBefore,
            await ReadPhraseFeedbackCountAsync(database));
    }

    [Fact]
    public async Task DiscardAsync_PersistsAllowedDatasetWithoutMutatingPolicyPhrases()
    {
        await using var database = await TestDatabase.CreateAsync();
        const string channelId = "channel-discard";
        var infrastructureNormalizer = new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer();
        var policyStore = new SqlitePolicyStore(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var storedPolicy = await policyStore.GetEffectivePolicyAsync(channelId, "509658");
        var context = new ChannelContextSnapshot(
            channelId,
            "509658",
            "Just Chatting",
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: true);
        var policy = new EffectivePolicyResolver().Resolve(
            context,
            storedPolicy.GeneralRevision,
            backseatgamingRevision: 1);
        var identity = ChatMessageIdentityV1.Create(
            "event-discard",
            "message-discard",
            channelId,
            "user-discard",
            "viewer",
            "Viewer",
            "spoiler secret",
            DateTimeOffset.UtcNow);
        var snapshot = new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(identity.OriginalText),
            context,
            IngressSequence: 1);
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var caseId = await repository.AddAsync(
            snapshot,
            new ValidatedModerationDecision(
                "temporary-discard",
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                PolicySetCode.General,
                "proposal",
                ["spoiler", "secret"],
                Confidence: 0.8,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            policy.RevisionFingerprint);
        var learningRevision = new LearningContextRevision();
        var service = new SqliteModerationReviewFeedbackService(
            repository,
            database.ConnectionFactory,
            infrastructureNormalizer,
            learningContextRevision: learningRevision);
        var phraseStateBefore = await ReadPhraseStateAsync(database);
        var policyStateBefore = await ReadPolicyStateAsync(database);
        var phraseFeedbackCountBefore = await ReadPhraseFeedbackCountAsync(database);

        var result = await service.DiscardAsync(
            caseId,
            expectedRowVersion: 1,
            discardReason: "Kein Verstoß",
            feedbackPolicySet: PolicySetCode.General,
            feedbackRuleCode: ModerationRuleCodes.GeneralPhrase,
            feedbackTriggerPhrases: ["spoiler"]);

        Assert.Equal(ModerationExecutionOutcome.Succeeded, result.Outcome);
        Assert.True(result.LearningAccepted);
        Assert.Equal(2, learningRevision.Current);
        var discarded = await repository.GetAsync(caseId);
        Assert.NotNull(discarded);
        Assert.Equal(CoreModerationCaseStatus.Discarded, discarded.Status);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT f.review_completed, f.discard_reason,
                   f.learning_accepted, COUNT(p.feedback_id)
            FROM moderation_feedback f
            LEFT JOIN phrase_feedback p ON p.case_id = f.case_id
            WHERE f.case_id = @case_id
            GROUP BY f.feedback_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal("Kein Verstoß", reader.GetString(1));
        Assert.True(reader.GetBoolean(2));
        Assert.Equal(0, reader.GetInt32(3));
        Assert.Equal(phraseStateBefore, await ReadPhraseStateAsync(database));
        Assert.Equal(policyStateBefore, await ReadPolicyStateAsync(database));
        Assert.Equal(
            phraseFeedbackCountBefore,
            await ReadPhraseFeedbackCountAsync(database));
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

    private sealed class FixedWhitelistPhraseSource(
        IReadOnlySet<string> exactMessages,
        IReadOnlySet<string> lowerUserLogins)
        : IWhitelistPhraseSource
    {
        public (WhitelistEntryKind Kind, string Value)? RecordedHit { get; private set; }

        public ValueTask<ActiveWhitelistSnapshot> GetActiveEntriesAsync(
            string channelId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ActiveWhitelistSnapshot(
                    exactMessages,
                    lowerUserLogins));
        }

        public ValueTask RecordHitAsync(
            string channelId,
            WhitelistEntryKind kind,
            string normalizedValue,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordedHit = (kind, normalizedValue);
            return ValueTask.CompletedTask;
        }
    }

    private static async Task<BanFixture> CreateBanFixtureAsync(
        TestDatabase database,
        RecordingHelixClient helix,
        string moderatorUserId = "moderator-1",
        string? badgeSetId = null)
    {
        const string channelId = "channel-ban";
        var infrastructureNormalizer =
            new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer();
        var policyStore = new SqlitePolicyStore(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var persistedPolicy = await policyStore.GetEffectivePolicyAsync(
            channelId,
            "509658");
        var context = new ChannelContextSnapshot(
            channelId,
            "509658",
            "Just Chatting",
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: true);
        var corePolicy = new EffectivePolicyResolver().Resolve(
            context,
            persistedPolicy.GeneralRevision,
            backseatgamingRevision: 1);
        var identity = ChatMessageIdentityV1.Create(
            "event-ban",
            "message-ban",
            channelId,
            "user-ban",
            "viewer",
            "Viewer",
            "schwere beleidigung",
            DateTimeOffset.UtcNow,
            badges: badgeSetId is null
                ? null
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [badgeSetId] = "1",
                });
        var snapshot = new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(identity.OriginalText),
            context,
            IngressSequence: 1);
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var caseId = await repository.AddAsync(
            snapshot,
            new ValidatedModerationDecision(
                "temporary-ban",
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                PolicySetCode.General,
                "Ursprünglicher Vorschlag.",
                ["schwere beleidigung"],
                Confidence: 0.99,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            corePolicy.RevisionFingerprint);
        var twitchOptions = new TwitchApiOptions
        {
            BroadcasterId = channelId,
            ModeratorUserId = moderatorUserId,
        };
        var executor = new TwitchModerationExecutor(
            repository,
            new SqliteMessageAvailabilityStore(
                database.ConnectionFactory,
                infrastructureNormalizer),
            new FixedChannelContextService(context),
            new EffectivePolicyResolver(),
            policyStore,
            database.ConnectionFactory,
            infrastructureNormalizer,
            helix,
            new InfrastructureOptions
            {
                DatabasePath = database.Path,
                TwitchApi = twitchOptions,
            });
        return new BanFixture(caseId, executor, twitchOptions);
    }

    private static ModerationExecutionRequest CreateBanRequest(long caseId) =>
        new(
            caseId,
            ExpectedRowVersion: 1,
            ModerationActionKind.Ban,
            TimeoutSeconds: null,
            "Schwere gezielte Beleidigung.",
            PolicySetCode.General,
            ["schwere beleidigung"],
            RuleCode: ModerationRuleCodes.GeneralPhrase);

    private sealed record BanFixture(
        long CaseId,
        TwitchModerationExecutor Executor,
        TwitchApiOptions Options);

    private sealed class RecordingHelixClient : ITwitchHelixClient
    {
        public (string BroadcasterId, string ModeratorId, string MessageId)?
            Delete { get; private set; }

        public (string BroadcasterId, string ModeratorId, string UserId, int Seconds, string Reason)?
            Timeout { get; private set; }

        public (string BroadcasterId, string ModeratorId, string UserId, string Reason)?
            Ban { get; private set; }

        public TwitchBannedUserInfo? ExistingBan { get; init; }

        public int GetBannedUserCalls { get; private set; }

        public Task<TwitchUserInfo> GetCurrentUserAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new TwitchUserInfo("moderator-1", "moderator", "Moderator"));
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
            GetBannedUserCalls++;
            return Task.FromResult(ExistingBan);
        }

        public Task DeleteMessageAsync(
            string broadcasterId,
            string moderatorId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            Delete = (broadcasterId, moderatorId, messageId);
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
            Timeout = (broadcasterId, moderatorId, userId, durationSeconds, reason);
            return Task.CompletedTask;
        }

        public Task BanUserAsync(
            string broadcasterId,
            string moderatorId,
            string userId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            Ban = (broadcasterId, moderatorId, userId, reason);
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
                "Moderation tests must not send chat messages.");
        }

        public Task CreateEventSubSubscriptionAsync(
            TwitchEventSubSubscriptionRequest subscription,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
