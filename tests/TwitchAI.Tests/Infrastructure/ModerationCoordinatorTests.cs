using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Pipeline;
using TwitchAI.Infrastructure.Twitch;
using CoreChatTextNormalizer = TwitchAI.Core.Services.ChatTextNormalizer;
using CoreEffectivePolicy = TwitchAI.Core.Models.EffectivePolicySnapshot;
using CoreModerationCaseStatus = TwitchAI.Core.Models.ModerationCaseStatus;
using StoredEffectivePolicy = TwitchAI.Infrastructure.Persistence.EffectivePolicySnapshot;

namespace TwitchAI.Tests.Infrastructure;

public sealed class ModerationCoordinatorTests
{
    private const string ChannelId = "channel-1";
    private static readonly DateTimeOffset Baseline =
        new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ApplyHumanPriorityDecisions_PreservesCompatibleAiEvidenceForExactLearning()
    {
        const string text = "du idiot";
        var rule = PersonalAttackRule();
        var batch = HumanPriorityBatch(text, rule, localMatches: []);
        var current = new ValidatedModerationDecision(
            "m1",
            ModerationActionKind.Timeout,
            600,
            PolicySetCode.General,
            "Die Nachricht hat den Nutzer direkt als Idioten bezeichnet.",
            [text],
            0.94,
            rule.RuleCode)
        {
            EvidenceKind = "direct_insult",
        };
        var result = ModerationCoordinator.ApplyHumanPriorityDecisions(
            batch,
            ValidReport(batch.BatchId, current),
            [new RetrievalExample(
                text,
                ModerationActionKind.Timeout,
                600,
                PolicySetCode.General,
                "Direkte Beleidigung eines Nutzers oder Streamers.",
                IsPositive: true,
                rule.RuleCode)
            {
                IsExactHumanDecision = true,
            }]);

        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal("direct_insult", decision.EvidenceKind);
        Assert.Equal(
            "Die Nachricht hat den Nutzer direkt als Idioten bezeichnet.",
            decision.Reason);
    }

    [Fact]
    public void ApplyHumanPriorityDecisions_PreservesPersonalDataTypeWithAiEvidence()
    {
        const string text = "deine adresse ist beispielstrasse 1";
        var rule = new ModerationRuleDefinition(
            ModerationRuleCodes.PersonalData,
            PolicySetCode.General,
            "Persönliche Daten / Doxxing",
            ModerationRuleCodes.PersonalDataPromptInstruction,
            ModerationActionKind.Timeout,
            86_400,
            "Veröffentlichung persönlicher Daten.",
            Priority: 10);
        var batch = HumanPriorityBatch(text, rule, localMatches: []);
        var current = new ValidatedModerationDecision(
            "m1",
            ModerationActionKind.Timeout,
            86_400,
            PolicySetCode.General,
            "Veröffentlichung persönlicher Daten.",
            [],
            0.98,
            rule.RuleCode)
        {
            EvidenceKind = "disclosure",
            SensitiveDataType = "address",
        };
        var result = ModerationCoordinator.ApplyHumanPriorityDecisions(
            batch,
            ValidReport(batch.BatchId, current),
            [new RetrievalExample(
                text,
                ModerationActionKind.Timeout,
                86_400,
                PolicySetCode.General,
                "Veröffentlichung persönlicher Daten.",
                IsPositive: true,
                rule.RuleCode)
            {
                IsExactHumanDecision = true,
            }]);

        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal("disclosure", decision.EvidenceKind);
        Assert.Equal("address", decision.SensitiveDataType);
    }

    [Fact]
    public void ApplyHumanPriorityDecisions_ExplicitPhraseEvidenceOverridesAiSubtype()
    {
        const string text = "ich liebe dich";
        var rule = PersonalAttackRule();
        var match = new BlacklistMatch(
            PhraseId: 173,
            RuleCode: rule.RuleCode,
            PolicySet: rule.PolicySet,
            MatchedPhrase: text);
        var batch = HumanPriorityBatch(text, rule, [match]);
        var current = new ValidatedModerationDecision(
            "m1",
            ModerationActionKind.Timeout,
            600,
            PolicySetCode.General,
            "Unangebrachte persönliche Annäherung.",
            [text],
            0.9,
            rule.RuleCode)
        {
            EvidenceKind = "direct_insult",
        };

        var result = ModerationCoordinator.ApplyHumanPriorityDecisions(
            batch,
            ValidReport(batch.BatchId, current),
            retrievalExamples: []);

        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal("unwanted_intimate_approach", decision.EvidenceKind);
    }

    [Fact]
    public void ApplyHumanPriorityDecisions_DoesNotCreateGenericModerationWithoutAiReason()
    {
        const string text = "ich liebe dich";
        var rule = PersonalAttackRule();
        var match = new BlacklistMatch(
            PhraseId: 173,
            RuleCode: rule.RuleCode,
            PolicySet: rule.PolicySet,
            MatchedPhrase: text);
        var batch = HumanPriorityBatch(text, rule, [match]);
        var current = new ValidatedModerationDecision(
            "m1",
            ModerationActionKind.None,
            null,
            null,
            string.Empty,
            [],
            0.9);

        var result = ModerationCoordinator.ApplyHumanPriorityDecisions(
            batch,
            ValidReport(batch.BatchId, current),
            retrievalExamples: []);

        var decision = Assert.Single(result.Envelope!.Decisions);
        Assert.Equal(ModerationActionKind.None, decision.Action);
        Assert.Empty(decision.Reason);
        Assert.Null(decision.RuleCode);
    }

    [Fact]
    public async Task StartAsync_RejectsMissingTwitchSessionBeforePipelineWork()
    {
        var readiness = new TwitchConnectionReadiness();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            connectionReadiness: readiness);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Coordinator.StartAsync());

        Assert.Contains(
            "Twitch-Anmeldung",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(harness.Coordinator.IsRunning);
        Assert.Equal(0, harness.PolicyStore.GetCallCount);
    }

    [Fact]
    public async Task StartAsync_PublishesStartedOnlyAfterEventSubIsReady()
    {
        var readiness = new TwitchConnectionReadiness();
        readiness.SetSessionReady(true, "Twitch verbunden.");
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            connectionReadiness: readiness);
        var startedEvent = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.Started);

        var start = harness.Coordinator.StartAsync();
        await harness.EventSub.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.False(start.IsCompleted);
        Assert.False(startedEvent.Task.IsCompleted);

        readiness.SetEventSubReady("EventSub aktiv.");
        await start.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(harness.Coordinator.IsRunning);
        Assert.True((await startedEvent.Task.WaitAsync(TimeSpan.FromSeconds(1))).Kind
            == ModerationPipelineEventKind.Started);
    }

    [Fact]
    public async Task ExactKekwWhitelistHitNeverCallsLocalModel()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set("kekw"),
            modelResponse: ValidEnvelope);
        var skipped = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.WhitelistSkipped);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(MessageEvent(1, "KEKW"));

        var pipelineEvent = await skipped.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("whitelist_match", pipelineEvent.StatusCode);
        Assert.Equal("KEKW", pipelineEvent.ChatMessage?.Text.OriginalText);
        Assert.Equal("kekw", pipelineEvent.ChatMessage?.Text.LowerText);
        Assert.Equal(
            WhitelistEntryKind.ExactMessage,
            harness.Whitelist.LastRecordedKind);
        Assert.Equal(1, harness.Whitelist.RecordedHitCount);
        Assert.Equal(0, harness.LocalModel.CallCount);
        Assert.Equal(0, harness.Repair.CallCount);
        Assert.Equal(0, harness.Cases.AddCallCount);
    }

    [Fact]
    public async Task StandaloneTwitchClipBypassesWhitelistDatabaseAndLocalModel()
    {
        var batcher = new RecordingModerationBatcher();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            batcher: batcher);
        var skipped = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.WhitelistSkipped);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(
            MessageEvent(
                1,
                "https://www.twitch.tv/any_channel/clip/FiveWordsForClipSlug"));

        var pipelineEvent = await skipped.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("whitelist_match", pipelineEvent.StatusCode);
        Assert.Equal(0, harness.Whitelist.GetCallCount);
        Assert.Equal(0, harness.Whitelist.RecordedHitCount);
        Assert.Equal(0, batcher.EnqueueCallCount);
        Assert.Equal(0, batcher.WaitingMessageCount);
        Assert.Null(batcher.NextDeadlineUtc);
        Assert.Equal(0, harness.LocalModel.CallCount);
        Assert.Equal(0, harness.Repair.CallCount);
        Assert.Equal(0, harness.Cases.AddCallCount);
    }

    [Fact]
    public async Task WhitelistedTwitchLoginNeverCallsLocalModel()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            whitelistedLogins: Set("targetlogin"));
        var skipped = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.WhitelistSkipped);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(
            MessageEvent(
                1,
                "ordinary message",
                login: "TargetLogin",
                displayName: "Different Display Name"));

        var pipelineEvent = await skipped.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("whitelist_match", pipelineEvent.StatusCode);
        Assert.Equal(
            "TargetLogin",
            pipelineEvent.ChatMessage?.Identity.Login);
        Assert.Equal(
            WhitelistEntryKind.UserLogin,
            harness.Whitelist.LastRecordedKind);
        Assert.Equal("targetlogin", harness.Whitelist.LastRecordedValue);
        Assert.Equal(1, harness.Whitelist.RecordedHitCount);
        Assert.Equal(0, harness.LocalModel.CallCount);
        Assert.Equal(0, harness.Repair.CallCount);
        Assert.Equal(0, harness.Cases.AddCallCount);
    }

    [Fact]
    public async Task TwitchRoleMessagesBypassWhitelistAndLocalModel()
    {
        var batcher = new RecordingModerationBatcher();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            batcher: batcher);
        var skipped = CompletionAfter(
            harness.Coordinator,
            item =>
                item.Kind == ModerationPipelineEventKind.WhitelistSkipped
                && item.StatusCode == "privileged_sender",
            count: 8);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(
            MessageEvent(
                1,
                "Broadcaster message",
                userId: ChannelId));
        harness.EventSub.Publish(
            MessageEvent(
                2,
                "Configured moderator message",
                userId: "moderator-1"));
        harness.EventSub.Publish(
            MessageEvent(
                3,
                "Badged moderator message",
                userId: "other-moderator",
                badges: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["MoDeRaToR"] = "1",
                }));
        harness.EventSub.Publish(
            MessageEvent(
                4,
                "Shared channel broadcaster message",
                userId: "source-channel",
                sharedChatSourceChannelId: "source-channel"));
        harness.EventSub.Publish(
            MessageEvent(
                5,
                "Badged broadcaster message",
                userId: "other-broadcaster",
                badges: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BrOaDcAsTeR"] = "1",
                }));
        harness.EventSub.Publish(
            MessageEvent(
                6,
                "Lead moderator bot message",
                userId: "lead-moderator-bot",
                badges: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["LeAd_MoDeRaToR"] = "1",
                }));
        harness.EventSub.Publish(
            MessageEvent(
                7,
                "Official chat bot message",
                userId: "official-chat-bot",
                badges: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["BoT-BaDgE"] = "1",
                }));
        harness.EventSub.Publish(
            MessageEvent(
                8,
                "VIP message",
                userId: "channel-vip",
                badges: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ViP"] = "1",
                }));

        await skipped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromMilliseconds(150));

        Assert.Equal(0, harness.Whitelist.GetCallCount);
        Assert.Equal(0, harness.Whitelist.RecordedHitCount);
        Assert.Equal(1, harness.PolicyStore.GetCallCount);
        Assert.Equal(0, batcher.EnqueueCallCount);
        Assert.Equal(0, batcher.WaitingMessageCount);
        Assert.Null(batcher.NextDeadlineUtc);
        Assert.Equal(0, harness.LocalModel.CallCount);
        Assert.Equal(0, harness.Repair.CallCount);
        Assert.Equal(0, harness.Cases.AddCallCount);
        Assert.DoesNotContain(
            harness.AiRunActivities,
            item => item.State == LocalAiRunState.Queued);
    }

    [Fact]
    public async Task BotLikeLoginWithoutBadgeStillUsesNormalWhitelistPipeline()
    {
        var batcher = new RecordingModerationBatcher();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            batcher: batcher);
        var queued = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.ChatQueued);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(
            MessageEvent(
                1,
                "Ordinary message",
                userId: "unmarked-bot-account",
                login: "ExampleBot"));

        await queued.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, harness.Whitelist.GetCallCount);
        Assert.Equal(1, batcher.EnqueueCallCount);
        Assert.Equal(1, batcher.WaitingMessageCount);
    }

    [Fact]
    public async Task DelayedEventSubTimestampsDoNotMakeTwoMessagesImmediatelyDue()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope);
        var queued = CompletionAfter(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.ChatQueued,
            count: 2);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(MessageEvent(1, "First delayed message"));
        harness.EventSub.Publish(MessageEvent(2, "Second delayed message"));

        await queued.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromMilliseconds(350));

        Assert.Equal(0, harness.LocalModel.CallCount);
        Assert.DoesNotContain(
            harness.AiRunActivities,
            item => item.State == LocalAiRunState.Queued);
    }

    [Fact]
    public async Task IdenticalQueuedLowerTextIsSkippedBeforeLocalModel()
    {
        var batcher = new RecordingModerationBatcher();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            batcher: batcher);
        var events = new ConcurrentQueue<ModerationPipelineEventArgs>();
        harness.Coordinator.EventReceived += (_, args) => events.Enqueue(args);
        var skipped = CompletionFor(
            harness.Coordinator,
            item => item.Kind ==
                ModerationPipelineEventKind.QueueDuplicateSkipped);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(
            MessageEvent(1, "Gemeinsam SPAMMEN", userId: "user-one"));
        harness.EventSub.Publish(
            MessageEvent(2, "gemeinsam spammen", userId: "user-two"));

        var pipelineEvent = await skipped.Task.WaitAsync(
            TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        Assert.Equal(
            "duplicate_lower_text_queued",
            pipelineEvent.StatusCode);
        Assert.Equal(
            "message-2",
            pipelineEvent.ChatMessage?.Identity.TwitchMessageId);
        Assert.Equal(2, batcher.EnqueueCallCount);
        Assert.Equal(1, batcher.WaitingMessageCount);
        Assert.Single(
            events,
            item => item.Kind == ModerationPipelineEventKind.ChatQueued);
        Assert.Equal(0, harness.LocalModel.CallCount);
        Assert.DoesNotContain(
            harness.AiRunActivities,
            item => item.State == LocalAiRunState.Queued);
    }

    [Fact]
    public async Task IdenticalCategoryReconciliationDoesNotSealTwoMessageBatch()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope);
        var queued = CompletionAfter(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.ChatQueued,
            count: 2);
        var reconciled = CompletionFor(
            harness.Coordinator,
            item => item.StatusCode == "category_reconciled");

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(MessageEvent(1, "First message"));
        harness.EventSub.Publish(MessageEvent(2, "Second message"));
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var sameCategory = Context() with
        {
            ObservedAtUtc = Baseline.AddMinutes(5),
            Revision = 999,
        };
        harness.EventSub.Publish(
            new TwitchChatEvent(
                TwitchChatEventKind.ChannelUpdated,
                "category-reconciliation",
                Baseline.AddMinutes(5),
                Message: null,
                ChannelId: ChannelId,
                TargetMessageId: null,
                TargetUserId: null,
                ChannelContext: sameCategory));

        await reconciled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        Assert.Equal(0, harness.LocalModel.CallCount);
        Assert.DoesNotContain(
            harness.AiRunActivities,
            item => item.State == LocalAiRunState.Queued);
    }

    [Fact]
    public async Task MessageFromSecondChannelUsesItsOwnCategoryContext()
    {
        const string secondChannelId = "channel-2";
        var secondContext = new ChannelContextSnapshot(
            secondChannelId,
            "game-2",
            "Second Game",
            Baseline,
            Revision: 7,
            IsVerified: true);
        var batcher = new RecordingModerationBatcher();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            batcher: batcher,
            channelContexts: [Context(), secondContext]);
        var queued = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.ChatQueued);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(
            MessageEvent(1, "Message in second channel", channelId: secondChannelId));

        var pipelineEvent = await queued.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(secondChannelId, pipelineEvent.ChatMessage?.Identity.ChannelId);
        Assert.Equal(secondContext, pipelineEvent.ChatMessage?.ChannelContext);
        Assert.Equal(1, harness.Whitelist.GetCallCount);
        Assert.Equal(1, batcher.EnqueueCallCount);
    }

    [Fact]
    public async Task StopAsync_DiscardsQueuedMessagesFromAllChannels()
    {
        const string secondChannelId = "channel-2";
        var batcher = new ModerationBatcher();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            batcher: batcher,
            channelContexts:
            [
                Context(),
                new ChannelContextSnapshot(
                    secondChannelId,
                    "game-2",
                    "Second Game",
                    Baseline,
                    Revision: 1,
                    IsVerified: true),
            ]);
        var queued = CompletionAfter(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.ChatQueued,
            count: 2);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(MessageEvent(1, "first channel"));
        harness.EventSub.Publish(
            MessageEvent(2, "second channel", channelId: secondChannelId));
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, batcher.WaitingMessageCount);

        await harness.Coordinator.StopAsync();

        Assert.Equal(0, batcher.WaitingMessageCount);
        Assert.Null(batcher.NextDeadlineUtc);
        Assert.False(batcher.TryDequeue(out _));
    }

    [Fact]
    public async Task StopAsync_PublishesSupersededForSealedWaitingBatch()
    {
        var inferenceStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            asyncModelResponse: async (_, cancellationToken) =>
            {
                inferenceStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable test path.");
            });
        var queued = CompletionAfter(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.ChatQueued,
            count: 40);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, startIndex: 1, count: 20);
        await inferenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        PublishMessages(harness.EventSub, startIndex: 21, count: 20);
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        await harness.Coordinator.StopAsync();

        Assert.Contains(
            harness.AiRunActivities,
            activity => activity.State == LocalAiRunState.Superseded
                && activity.ErrorCode == "analysis_stopped");
    }

    [Fact]
    public async Task TwentiethMessageSealsBatchAndCreatesValidatedCase()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope);
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);

        var pipelineEvent = await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("moderation_case_created", pipelineEvent.StatusCode);
        Assert.Equal("message-1", pipelineEvent.ChatMessage?.Identity.TwitchMessageId);
        Assert.Contains(
            pipelineEvent.ActiveRules,
            rule => string.Equals(
                rule.RuleCode,
                ModerationRuleCodes.GeneralPhrase,
                StringComparison.Ordinal));
        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(0, harness.Repair.CallCount);
        Assert.Equal(1, harness.Cases.AddCallCount);
        Assert.Equal(
            ModerationRuleCodes.GeneralPhrase,
            (await harness.Cases.GetAsync(101))?.RuleCode);
        var queuedRun = Assert.Single(
            harness.AiRunActivities,
            item => item.State == LocalAiRunState.Queued);
        Assert.Equal(20, queuedRun.MessageCount);
        Assert.Equal("openai/gpt-oss-20b", queuedRun.ModelId);
        var request = Assert.Single(harness.LocalModel.Requests);
        Assert.Equal(20, request.Messages.Count);
        Assert.All(
            request.Messages,
            message => Assert.Equal(
                message.LowerText.ToLowerInvariant(),
                message.LowerText));
    }

    [Fact]
    public async Task ActiveDuplicateIsStillAnalyzedButDoesNotPublishSecondCase()
    {
        var oneMessageBatcher = new ModerationBatcher(
            new ModerationBatcherOptions(
                MaximumMessages: 1,
                MaximumCollectionDelay: TimeSpan.FromSeconds(10),
                MaximumMessageCharacters: 6_000,
                MaximumQueuedMessages: 500));
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            batcher: oneMessageBatcher);
        var firstCreated = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(
            MessageEvent(1, "Bitte   aufhören", userId: "same-user"));
        var first = await firstCreated.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var suppressed = CompletionFor(
            harness.Coordinator,
            item =>
                item.Kind == ModerationPipelineEventKind.DuplicateProposalSuppressed);
        harness.EventSub.Publish(
            MessageEvent(2, "BITTE AUFHÖREN", userId: "same-user"));
        var duplicate = await suppressed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("active_duplicate_proposal", duplicate.StatusCode);
        Assert.Equal(first.CaseId, duplicate.CaseId);
        Assert.Equal("message-2", duplicate.ChatMessage?.Identity.TwitchMessageId);
        Assert.Equal(2, harness.LocalModel.CallCount);
        Assert.Equal(1, harness.Cases.AddCallCount);
    }

    [Fact]
    public async Task InvalidEnvelopeGetsExactlyOneRepairAndCreatesNoCase()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: _ => "{}",
            repairResponse: "{}");
        var rejected = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.BatchRejected);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);

        var pipelineEvent = await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("invalid_ai_envelope", pipelineEvent.StatusCode);
        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(1, harness.Repair.CallCount);
        Assert.Equal(0, harness.Cases.AddCallCount);
    }

    [Fact]
    public async Task V6SingleInvalidDecisionRepairsOnlyThatMessageAndKeepsValidDecisions()
    {
        var validationAttempts = new RecordingValidationAttemptStore();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: PartiallyInvalidV6Envelope,
            repairResponseFactory: V6NoActionEnvelope,
            useCompactContractV6: true,
            validationAttemptStore: validationAttempts);
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);

        var pipelineEvent = await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("message-1", pipelineEvent.ChatMessage?.Identity.TwitchMessageId);
        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(1, harness.Repair.CallCount);
        Assert.Equal(1, harness.Cases.AddCallCount);
        var repairEnvelope = Assert.Single(harness.Repair.Requests);
        var originalEnvelope = Assert.Single(harness.LocalModel.Requests);
        Assert.Equal(
            originalEnvelope.Messages[^1].MessageId,
            Assert.Single(repairEnvelope.Messages).MessageId);
        var attempts = validationAttempts.Attempts.ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(AiValidationAttemptKind.Initial, attempts[0].AttemptKind);
        Assert.Equal(AiValidationOutcome.Rejected, attempts[0].Outcome);
        Assert.Equal(["rule_inactive"], attempts[0].ValidationErrorCodes);
        Assert.Equal(1, attempts[0].AffectedMessageCount);
        Assert.Equal(AiValidationAttemptKind.Repair, attempts[1].AttemptKind);
        Assert.Equal(AiValidationOutcome.Repaired, attempts[1].Outcome);
        Assert.Equal(1, attempts[1].MessageCount);
        Assert.Equal(1, attempts[1].AffectedMessageCount);
        Assert.Equal(attempts[0].CorrelationId, attempts[1].ParentCorrelationId);
        Assert.True(
            attempts[1].PromptCharacters.Messages
            < attempts[0].PromptCharacters.Messages);
    }

    [Fact]
    public async Task V7KeyedTwentyMessageResponseNeedsNoRepair()
    {
        var validationAttempts = new RecordingValidationAttemptStore();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: V7NoActionEnvelope,
            useCompactContractV6: true,
            validationAttemptStore: validationAttempts);
        var allowed = CompletionAfter(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.Allowed,
            count: 20);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);

        await allowed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(0, harness.Repair.CallCount);
        Assert.Equal(0, harness.Cases.AddCallCount);
        var attempt = Assert.Single(validationAttempts.Attempts);
        Assert.Equal(AiValidationAttemptKind.Initial, attempt.AttemptKind);
        Assert.Equal(AiValidationOutcome.Valid, attempt.Outcome);
        Assert.Equal(ModerationPromptCompiler.ResponseEnvelopeVersion, attempt.SchemaVersion);
    }

    [Fact]
    public async Task V7SingleInvalidDecisionRepairsOnlyThatMessage()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: PartiallyInvalidV7Envelope,
            repairResponseFactory: V7NoActionEnvelope,
            useCompactContractV6: true);
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);

        await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(1, harness.Repair.CallCount);
        Assert.Equal(1, harness.Cases.AddCallCount);
        var repaired = Assert.Single(harness.Repair.Requests);
        Assert.Equal(
            harness.LocalModel.Requests.Single().Messages[^1].MessageId,
            Assert.Single(repaired.Messages).MessageId);
    }

    [Fact]
    public async Task V6MissingRuleLocalFallbackIsDiagnosedWithoutRepair()
    {
        var validationAttempts = new RecordingValidationAttemptStore();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: V6MissingRuleEnvelope,
            useCompactContractV6: true,
            validationAttemptStore: validationAttempts);
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);

        await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(0, harness.Repair.CallCount);
        var attempts = validationAttempts.Attempts.ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(AiValidationAttemptKind.Initial, attempts[0].AttemptKind);
        Assert.Equal(AiValidationAttemptKind.LocalFallback, attempts[1].AttemptKind);
        Assert.Equal(AiValidationOutcome.Fallback, attempts[1].Outcome);
        Assert.Equal(1, attempts[1].AffectedMessageCount);
    }

    [Fact]
    public async Task ValidationDiagnosticsFailureDoesNotStopV6Moderation()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidV6Envelope,
            useCompactContractV6: true,
            validationAttemptStore: new ThrowingValidationAttemptStore());
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);

        await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(0, harness.Repair.CallCount);
        Assert.Equal(1, harness.Cases.AddCallCount);
        Assert.True(harness.Coordinator.IsRunning);
    }

    [Fact]
    public async Task V6FullRepairReplacesInitiallyValidDecisionsWithoutMergeCollision()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: V6EnvelopeWithUnknownId,
            repairResponseFactory: V6NoActionEnvelope,
            useCompactContractV6: true);
        var allowed = CompletionAfter(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.Allowed,
            count: 20);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);

        await allowed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(1, harness.Repair.CallCount);
        Assert.Equal(20, Assert.Single(harness.Repair.Requests).Messages.Count);
        Assert.Equal(0, harness.Cases.AddCallCount);
    }

    [Fact]
    public async Task V6RuntimeRejectsLegacyResponseEvenAfterSingleRepair()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            repairResponseFactory: ValidEnvelope,
            useCompactContractV6: true);
        var rejected = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.BatchRejected);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);

        var result = await rejected.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("invalid_ai_envelope", result.StatusCode);
        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(1, harness.Repair.CallCount);
        Assert.Equal(0, harness.Cases.AddCallCount);
    }

    [Fact]
    public async Task FailedAiBatchDoesNotStopCoordinatorAndNextBatchIsProcessed()
    {
        var modelAttempt = 0;
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: request =>
            {
                if (Interlocked.Increment(ref modelAttempt) == 1)
                {
                    throw new HttpRequestException("LM Studio connection failed.");
                }

                return ValidEnvelope(request);
            });
        var events = new ConcurrentQueue<ModerationPipelineEventArgs>();
        harness.Coordinator.EventReceived += (_, item) => events.Enqueue(item);
        var failed = CompletionFor(
            harness.Coordinator,
            item =>
                item.Kind == ModerationPipelineEventKind.BatchRejected
                && item.StatusCode == "ai_batch_failed");
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, startIndex: 1, count: 20);

        var failedEvent = await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.IsType<HttpRequestException>(failedEvent.Exception);
        Assert.True(harness.Coordinator.IsRunning);

        PublishMessages(harness.EventSub, startIndex: 21, count: 20);
        var createdEvent = await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal("message-21", createdEvent.ChatMessage?.Identity.TwitchMessageId);
        Assert.Equal(2, harness.LocalModel.CallCount);
        Assert.Equal(1, harness.Cases.AddCallCount);
        Assert.True(harness.Coordinator.IsRunning);
        Assert.DoesNotContain(
            events,
            item => item.Kind == ModerationPipelineEventKind.Faulted);
    }

    [Fact]
    public async Task MissingRuleUsesSafeFallbackBeforeRepairAndKeepsProposalReviewable()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: MissingRuleEnvelope,
            repairResponseFactory: MissingRuleEnvelope);
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);

        var pipelineEvent = await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(0, harness.Repair.CallCount);
        Assert.Equal(1, harness.Cases.AddCallCount);
        Assert.Equal(
            ModerationRuleCodes.OtherAbuse,
            pipelineEvent.Decision?.RuleCode);
        var persisted = await harness.Cases.GetAsync(pipelineEvent.CaseId!.Value);
        Assert.Equal(ModerationRuleCodes.OtherAbuse, persisted?.RuleCode);
    }

    [Fact]
    public async Task PolicyRevisionChangeRequeuesWholeBatchBeforeInference()
    {
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            generalRevisionForCall: call => call <= 20 ? 1 : 2);
        var stale = CompletionFor(
            harness.Coordinator,
            item =>
                item.Kind == ModerationPipelineEventKind.StalePolicyRequeued
                && item.StatusCode == "stale_policy_requeued");
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);

        await stale.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(0, harness.Repair.CallCount);
        Assert.Equal(1, harness.Cases.AddCallCount);
        Assert.True(harness.PolicyStore.GetCallCount >= 23);

        var activities = harness.AiRunActivities.ToArray();
        var superseded = Assert.Single(
            activities,
            item => item.State == LocalAiRunState.Superseded);
        Assert.Equal("policy_revision_changed", superseded.ErrorCode);
        Assert.Contains(
            activities,
            item => item.State == LocalAiRunState.Queued
                && string.Equals(
                    item.CorrelationId,
                    superseded.CorrelationId,
                    StringComparison.Ordinal));
        Assert.Contains(
            activities,
            item => item.State == LocalAiRunState.Running
                && string.Equals(
                    item.CorrelationId,
                    superseded.CorrelationId,
                    StringComparison.Ordinal));
        Assert.Contains(
            activities,
            item => item.State == LocalAiRunState.Queued
                && !string.Equals(
                    item.CorrelationId,
                    superseded.CorrelationId,
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task PolicyRevisionChangeDoesNotRestartCollectionDelay()
    {
        var batcher = new ModerationBatcher(
            new ModerationBatcherOptions(
                MaximumMessages: 20,
                MaximumCollectionDelay: TimeSpan.FromSeconds(10),
                MaximumMessageCharacters: 6_000,
                MaximumQueuedMessages: 500));
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            generalRevisionForCall: call => call <= 3 ? 1 : 2,
            batcher: batcher);
        var queued = CompletionAfter(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.ChatQueued,
            count: 2);
        var stale = CompletionFor(
            harness.Coordinator,
            item => item.StatusCode == "stale_policy_requeued");
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(MessageEvent(1, "Erste Nachricht"));
        harness.EventSub.Publish(MessageEvent(2, "Zweite Nachricht"));
        await queued.Task.WaitAsync(TimeSpan.FromSeconds(3));

        harness.EventSub.Publish(
            new TwitchChatEvent(
                TwitchChatEventKind.EventGap,
                "event-gap-policy-refresh",
                Baseline.AddSeconds(1),
                Message: null,
                ChannelId,
                TargetMessageId: null,
                TargetUserId: null,
                ChannelContext: null));

        await stale.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(0, batcher.WaitingMessageCount);
        Assert.Contains(
            harness.AiRunActivities,
            item => item.State == LocalAiRunState.Superseded
                && item.ErrorCode == "policy_revision_changed");
    }

    [Fact]
    public async Task LearningRevisionChangeDuringInferenceRequeuesWithoutCreatingStaleCase()
    {
        var firstRequestStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRequest = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        var learningRevision = new LearningContextRevision();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            asyncModelResponse: async (request, cancellationToken) =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    firstRequestStarted.TrySetResult();
                    await releaseFirstRequest.Task.WaitAsync(cancellationToken);
                }

                return ValidEnvelope(request);
            },
            learningContextRevision: learningRevision);
        var requeued = CompletionFor(
            harness.Coordinator,
            item =>
                item.Kind == ModerationPipelineEventKind.StaleLearningRequeued
                && item.StatusCode == "stale_learning_requeued");
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);
        await firstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        learningRevision.Advance();
        releaseFirstRequest.TrySetResult();

        await requeued.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(2, harness.LocalModel.CallCount);
        Assert.Equal(1, harness.Cases.AddCallCount);
        Assert.Contains(
            harness.AiRunActivities,
            item => item.State == LocalAiRunState.Superseded
                && item.ErrorCode == "learning_context_changed");
    }

    [Fact]
    public async Task BatchPassesEveryLowercaseMessageToLearningRetrieval()
    {
        var retrieval = new RecordingRetrievalExampleStore();
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            retrievalExampleStore: retrieval);
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        PublishMessages(harness.EventSub, 20);
        await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var query = Assert.Single(retrieval.Queries);
        Assert.Equal(20, query.Count);
        Assert.Equal("toxische nachricht 1", query[0]);
        Assert.Equal("toxische nachricht 20", query[^1]);
    }

    [Fact]
    public async Task ReanalyzeStaleCaseQueuesCurrentPolicyAndCreatesFreshCase()
    {
        var normalizer = new CoreChatTextNormalizer();
        var staleMessage = Snapshot(90, "erneut prüfen", normalizer);
        var staleCase = new ModerationCase(
            42,
            3,
            staleMessage,
            ModerationActionKind.Timeout,
            300,
            "Alter Vorschlag",
            PolicySetCode.General,
            ["prüfen"],
            0.7,
            "old-policy",
            CoreModerationCaseStatus.StalePolicy,
            Baseline);
        var repository = new FakeCaseRepository(staleCase);
        var oneMessageBatcher = new ModerationBatcher(
            new ModerationBatcherOptions(
                MaximumMessages: 1,
                MaximumCollectionDelay: TimeSpan.FromSeconds(10),
                MaximumMessageCharacters: 6_000,
                MaximumQueuedMessages: 500));
        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: ValidEnvelope,
            caseRepository: repository,
            batcher: oneMessageBatcher);
        var requeued = CompletionFor(
            harness.Coordinator,
            item => item.StatusCode == "stale_case_requeued");
        var created = CompletionFor(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.CaseCreated);

        await harness.Coordinator.StartAsync();
        await harness.Coordinator.ReanalyzeCaseAsync(42);

        var requeueEvent = await requeued.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var createdEvent = await created.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(42, requeueEvent.CaseId);
        Assert.Equal(staleMessage.Identity.TwitchMessageId, createdEvent.ChatMessage?.Identity.TwitchMessageId);
        Assert.Equal(1, harness.LocalModel.CallCount);
        Assert.Equal(1, repository.AddCallCount);
    }

    [Fact]
    public async Task DifferentChannelsRunConcurrentlyWithOneWorkerPerChannel()
    {
        var contexts = new[]
        {
            Context(),
            Context() with { ChannelId = "channel-2" },
        };
        var bothStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var active = 0;
        var maximumActive = 0;
        async ValueTask<string> RespondAsync(
            ModerationRequestEnvelopeV3 request,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref active);
            var observedMaximum = Volatile.Read(ref maximumActive);
            while (current > observedMaximum)
            {
                var previous = Interlocked.CompareExchange(
                    ref maximumActive,
                    current,
                    observedMaximum);
                if (previous == observedMaximum)
                {
                    break;
                }

                observedMaximum = previous;
            }
            if (Interlocked.Increment(ref started) == 2)
            {
                bothStarted.TrySetResult();
            }

            await release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref active);
            return NoActionEnvelope(request);
        }

        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: NoActionEnvelope,
            asyncModelResponse: RespondAsync,
            batcher: OneMessageBatcher(),
            channelContexts: contexts);
        var bothQueued = CompletionAfter(
            harness.Coordinator,
            item => item.Kind == ModerationPipelineEventKind.ChatQueued,
            2);

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(MessageEvent(1, "erste nachricht", channelId: ChannelId));
        harness.EventSub.Publish(MessageEvent(2, "zweite nachricht", channelId: "channel-2"));

        await bothQueued.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var startedTask = await Task.WhenAny(
            bothStarted.Task,
            Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(
            ReferenceEquals(startedTask, bothStarted.Task),
            $"Expected two concurrent starts, observed {harness.LocalModel.CallCount}; "
            + $"queued messages: {harness.AiRunActivities.Count(activity => activity.State == LocalAiRunState.Queued)}, "
            + $"running activities: {harness.AiRunActivities.Count(activity => activity.State == LocalAiRunState.Running)}.");
        Assert.Equal(2, maximumActive);
        Assert.Equal(
            [ChannelId, "channel-2"],
            harness.LocalModel.SchedulingKeys.Order(StringComparer.Ordinal));
        release.TrySetResult();
    }

    [Fact]
    public async Task SameChannelBatchesRemainSerial()
    {
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = Channel.CreateUnbounded<bool>();
        var started = 0;
        async ValueTask<string> RespondAsync(
            ModerationRequestEnvelopeV3 request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref started) == 1)
            {
                firstStarted.TrySetResult();
            }
            else
            {
                secondStarted.TrySetResult();
            }

            await releases.Reader.ReadAsync(cancellationToken);
            return NoActionEnvelope(request);
        }

        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: NoActionEnvelope,
            asyncModelResponse: RespondAsync,
            batcher: OneMessageBatcher());

        await harness.Coordinator.StartAsync();
        harness.EventSub.Publish(MessageEvent(11, "erste nachricht"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        harness.EventSub.Publish(MessageEvent(12, "zweite nachricht"));

        await Task.Delay(150);
        Assert.Equal(1, Volatile.Read(ref started));
        releases.Writer.TryWrite(true);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        releases.Writer.TryWrite(true);
    }

    [Fact]
    public async Task FiveChannelsUseAtMostFourWorkersAndFifthStartsAfterRelease()
    {
        var contexts = Enumerable.Range(1, 5)
            .Select(index => Context() with { ChannelId = $"channel-{index}" })
            .ToArray();
        var fourStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fiveStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releases = Channel.CreateUnbounded<bool>();
        var started = 0;
        var active = 0;
        var maximumActive = 0;
        async ValueTask<string> RespondAsync(
            ModerationRequestEnvelopeV3 request,
            CancellationToken cancellationToken)
        {
            var currentActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximumActive, currentActive);
            var currentStarted = Interlocked.Increment(ref started);
            if (currentStarted == 4)
            {
                fourStarted.TrySetResult();
            }
            else if (currentStarted == 5)
            {
                fiveStarted.TrySetResult();
            }

            await releases.Reader.ReadAsync(cancellationToken);
            Interlocked.Decrement(ref active);
            return NoActionEnvelope(request);
        }

        await using var harness = CoordinatorHarness.Create(
            whitelist: Set(),
            modelResponse: NoActionEnvelope,
            asyncModelResponse: RespondAsync,
            batcher: OneMessageBatcher(),
            channelContexts: contexts);

        await harness.Coordinator.StartAsync();
        for (var index = 1; index <= 5; index++)
        {
            harness.EventSub.Publish(MessageEvent(
                20 + index,
                $"nachricht {index}",
                channelId: $"channel-{index}"));
        }

        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(150);
        Assert.Equal(4, Volatile.Read(ref started));
        Assert.Equal(4, maximumActive);

        releases.Writer.TryWrite(true);
        await fiveStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(4, maximumActive);
        for (var index = 0; index < 4; index++)
        {
            releases.Writer.TryWrite(true);
        }
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        var observed = Volatile.Read(ref maximum);
        while (candidate > observed)
        {
            var previous = Interlocked.CompareExchange(
                ref maximum,
                candidate,
                observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private static ModerationBatcher OneMessageBatcher() =>
        new(
            new ModerationBatcherOptions(
                MaximumMessages: 1,
                MaximumCollectionDelay: TimeSpan.FromSeconds(10),
                MaximumMessageCharacters: 6_000,
                MaximumQueuedMessages: 500));

    private static ModerationRuleDefinition PersonalAttackRule() =>
        new(
            ModerationRuleCodes.PersonalAttack,
            PolicySetCode.General,
            "Beleidigung & Belästigung",
            ModerationRuleCodes.PersonalAttackPromptInstruction,
            ModerationActionKind.Timeout,
            600,
            "Beleidigung oder Belästigung eines Nutzers oder Streamers.",
            Priority: 50);

    private static ModerationBatch HumanPriorityBatch(
        string text,
        ModerationRuleDefinition rule,
        IReadOnlyList<BlacklistMatch> localMatches)
    {
        var policy = new CoreEffectivePolicy(
            [new PolicySetRevision(PolicySetCode.General, 1)],
            "human-priority-policy")
        {
            ActiveRules = [rule],
        };
        var batchId = Guid.Parse("f8cacbb8-e907-4a22-a5cb-5af4736f9bf4");
        return new ModerationBatch(
            batchId,
            new ModerationBatchContext(Context(), policy),
            [new ModerationBatchMessage(
                "m1",
                Snapshot(900, text, new CoreChatTextNormalizer()),
                localMatches)],
            Baseline,
            Baseline.AddSeconds(1),
            BatchSealReason.ManualFlush);
    }

    private static ModerationValidationReport ValidReport(
        Guid batchId,
        ValidatedModerationDecision decision) =>
        new(
            new ValidatedModerationEnvelope(batchId, [decision]),
            [decision],
            Issues: [],
            RepairMessageIds: [],
            RequiresFullRepair: false);

    private static TaskCompletionSource<ModerationPipelineEventArgs> CompletionFor(
        IModerationCoordinator coordinator,
        Func<ModerationPipelineEventArgs, bool> predicate)
    {
        var completion = new TaskCompletionSource<ModerationPipelineEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.EventReceived += (_, item) =>
        {
            if (predicate(item))
            {
                completion.TrySetResult(item);
            }
        };
        return completion;
    }

    private static TaskCompletionSource<ModerationPipelineEventArgs> CompletionAfter(
        IModerationCoordinator coordinator,
        Func<ModerationPipelineEventArgs, bool> predicate,
        int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var completion = new TaskCompletionSource<ModerationPipelineEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = 0;
        coordinator.EventReceived += (_, item) =>
        {
            if (predicate(item)
                && Interlocked.Increment(ref observed) == count)
            {
                completion.TrySetResult(item);
            }
        };
        return completion;
    }

    private static void PublishMessages(FakeEventSubClient eventSub, int count)
    {
        PublishMessages(eventSub, startIndex: 1, count);
    }

    private static void PublishMessages(
        FakeEventSubClient eventSub,
        int startIndex,
        int count)
    {
        for (var index = startIndex; index < startIndex + count; index++)
        {
            eventSub.Publish(MessageEvent(index, $"Toxische Nachricht {index}"));
        }
    }

    private static HashSet<string> Set(params string[] values)
    {
        return new HashSet<string>(values, StringComparer.Ordinal);
    }

    private static TwitchChatEvent MessageEvent(
        int index,
        string text,
        string? userId = null,
        IReadOnlyDictionary<string, string>? badges = null,
        string? sharedChatSourceChannelId = null,
        string? login = null,
        string? displayName = null,
        string? channelId = null)
    {
        channelId ??= ChannelId;
        var receivedAt = Baseline.AddMilliseconds(index);
        userId ??= $"user-{index}";
        login ??= $"login-{userId}";
        displayName ??= $"User {userId}";
        var identity = ChatMessageIdentityV1.Create(
            $"event-{index}",
            $"message-{index}",
            channelId,
            userId,
            login,
            displayName,
            text,
            receivedAt,
            sharedChatSourceChannelId,
            badges);
        return new TwitchChatEvent(
            TwitchChatEventKind.Message,
            identity.EventSubNotificationId,
            receivedAt,
            identity,
            channelId,
            TargetMessageId: null,
            TargetUserId: null,
            ChannelContext: null);
    }

    private static ChatMessageSnapshot Snapshot(
        int index,
        string text,
        CoreChatTextNormalizer normalizer)
    {
        var chatEvent = MessageEvent(index, text);
        return new ChatMessageSnapshot(
            chatEvent.Message!,
            normalizer.Process(text),
            Context(),
            index);
    }

    private static ChannelContextSnapshot Context()
    {
        return new ChannelContextSnapshot(
            ChannelId,
            EffectivePolicyResolver.BootstrapJustChattingCategoryId,
            "Just Chatting",
            Baseline,
            Revision: 1,
            IsVerified: true);
    }

    private static string ValidEnvelope(ModerationRequestEnvelopeV3 request)
    {
        var decisions = request.Messages
            .Select((message, index) => new Dictionary<string, object?>
            {
                ["messageId"] = message.MessageId,
                ["action"] = index == 0 ? "timeout" : "none",
                ["timeoutSeconds"] = index == 0 ? 600 : null,
                ["policySet"] = index == 0 ? "G" : null,
                ["ruleCode"] = index == 0
                    ? ModerationRuleCodes.GeneralPhrase
                    : null,
                ["reason"] = index == 0 ? "Lokaler Moderationstest" : string.Empty,
                ["triggerPhrases"] = index == 0 ? new[] { message.LowerText } : [],
                ["confidence"] = index == 0 ? 0.95 : 0.05,
                ["replyTemplateId"] = null,
                ["replyConfidence"] = null,
            })
            .ToArray();
        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["envelopeVersion"] = ModerationEnvelopeValidator.ResponseEnvelopeVersion,
                ["batchId"] = request.BatchId,
                ["decisions"] = decisions,
            });
    }

    private static string NoActionEnvelope(ModerationRequestEnvelopeV3 request) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["envelopeVersion"] = ModerationEnvelopeValidator.ResponseEnvelopeVersion,
                ["batchId"] = request.BatchId,
                ["decisions"] = request.Messages.Select(message =>
                    new Dictionary<string, object?>
                    {
                        ["messageId"] = message.MessageId,
                        ["action"] = "none",
                        ["timeoutSeconds"] = null,
                        ["policySet"] = null,
                        ["ruleCode"] = null,
                        ["reason"] = string.Empty,
                        ["triggerPhrases"] = Array.Empty<string>(),
                        ["confidence"] = 0.9,
                        ["replyTemplateId"] = null,
                        ["replyConfidence"] = null,
                    }).ToArray(),
            });

    private static string PartiallyInvalidV6Envelope(
        ModerationRequestEnvelopeV3 request)
    {
        var first = request.Messages[0];
        var invalid = request.Messages[^1];
        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["envelopeVersion"] =
                    ModerationEnvelopeValidator.SparseResponseEnvelopeVersion,
                ["batchId"] = request.BatchId,
                ["noActionMessageIds"] = request.Messages
                    .Skip(1)
                    .SkipLast(1)
                    .Select(message => message.MessageId)
                    .ToArray(),
                ["moderations"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["messageId"] = first.MessageId,
                        ["action"] = "timeout",
                        ["timeoutSeconds"] = 600,
                        ["ruleCode"] = ModerationRuleCodes.GeneralPhrase,
                        ["reason"] = "Sonstiger Verstoß gegen die Chatregeln.",
                        ["triggerPhrases"] = new[] { first.LowerText },
                        ["confidence"] = 0.95,
                    },
                    new Dictionary<string, object?>
                    {
                        ["messageId"] = invalid.MessageId,
                        ["action"] = "timeout",
                        ["timeoutSeconds"] = 600,
                        ["ruleCode"] = "UNKNOWN",
                        ["reason"] = "Ungültige Kategorie.",
                        ["triggerPhrases"] = new[] { invalid.LowerText },
                        ["confidence"] = 0.9,
                    },
                },
            });
    }

    private static string ValidV6Envelope(ModerationRequestEnvelopeV3 request) =>
        CreateV6ModerationEnvelope(request, includeRuleCode: true);

    private static string V6MissingRuleEnvelope(ModerationRequestEnvelopeV3 request) =>
        CreateV6ModerationEnvelope(request, includeRuleCode: false);

    private static string CreateV6ModerationEnvelope(
        ModerationRequestEnvelopeV3 request,
        bool includeRuleCode)
    {
        var first = request.Messages[0];
        var moderation = new Dictionary<string, object?>
        {
            ["messageId"] = first.MessageId,
            ["action"] = "timeout",
            ["timeoutSeconds"] = 600,
            ["reason"] = "Sonstiger Verstoß gegen die Chatregeln.",
            ["triggerPhrases"] = new[] { first.LowerText },
            ["confidence"] = 0.95,
        };
        if (includeRuleCode)
        {
            moderation["ruleCode"] = ModerationRuleCodes.GeneralPhrase;
        }

        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["envelopeVersion"] =
                    ModerationEnvelopeValidator.SparseResponseEnvelopeVersion,
                ["batchId"] = request.BatchId,
                ["noActionMessageIds"] = request.Messages
                    .Skip(1)
                    .Select(message => message.MessageId)
                    .ToArray(),
                ["moderations"] = new[] { moderation },
            });
    }

    private static string V6NoActionEnvelope(ModerationRequestEnvelopeV3 request)
    {
        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["envelopeVersion"] =
                    ModerationEnvelopeValidator.SparseResponseEnvelopeVersion,
                ["batchId"] = request.BatchId,
                ["noActionMessageIds"] = request.Messages
                    .Select(message => message.MessageId)
                    .ToArray(),
                ["moderations"] = Array.Empty<object>(),
            });
    }

    private static string V7NoActionEnvelope(ModerationRequestEnvelopeV3 request) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["envelopeVersion"] =
                    ModerationPromptCompiler.ResponseEnvelopeVersion,
                ["batchId"] = request.BatchId,
                ["decisions"] = request.Messages.ToDictionary(
                    message => message.MessageId,
                    _ => (object?)null,
                    StringComparer.Ordinal),
            });

    private static string PartiallyInvalidV7Envelope(
        ModerationRequestEnvelopeV3 request)
    {
        var decisions = request.Messages.ToDictionary(
            message => message.MessageId,
            _ => (object?)null,
            StringComparer.Ordinal);
        var first = request.Messages[0];
        decisions[first.MessageId] = new Dictionary<string, object?>
        {
            ["action"] = "timeout",
            ["timeoutSeconds"] = 600,
            ["ruleCode"] = ModerationRuleCodes.GeneralPhrase,
            ["evidenceKind"] = null,
            ["sensitiveDataType"] = null,
            ["violationReason"] = "Konkreter toxischer persönlicher Angriff.",
            ["guidance"] = "Chatregeln künftig bitte beachten.",
            ["triggerPhrases"] = new[] { first.LowerText },
            ["confidence"] = 0.95,
        };
        var invalid = request.Messages[^1];
        decisions[invalid.MessageId] = new Dictionary<string, object?>
        {
            ["action"] = "timeout",
            ["timeoutSeconds"] = 600,
            ["ruleCode"] = "UNKNOWN",
            ["evidenceKind"] = "direct_insult",
            ["sensitiveDataType"] = null,
            ["violationReason"] = "Konkreter toxischer persönlicher Angriff.",
            ["guidance"] = "Chatregeln künftig bitte beachten.",
            ["triggerPhrases"] = new[] { invalid.LowerText },
            ["confidence"] = 0.9,
        };
        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["envelopeVersion"] =
                    ModerationPromptCompiler.ResponseEnvelopeVersion,
                ["batchId"] = request.BatchId,
                ["decisions"] = decisions,
            });
    }

    private static string V6EnvelopeWithUnknownId(
        ModerationRequestEnvelopeV3 request)
    {
        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["envelopeVersion"] =
                    ModerationEnvelopeValidator.SparseResponseEnvelopeVersion,
                ["batchId"] = request.BatchId,
                ["noActionMessageIds"] = request.Messages
                    .Select(message => message.MessageId)
                    .Append("unknown")
                    .ToArray(),
                ["moderations"] = Array.Empty<object>(),
            });
    }

    private static string MissingRuleEnvelope(ModerationRequestEnvelopeV3 request)
    {
        var decisions = request.Messages
            .Select((message, index) => new Dictionary<string, object?>
            {
                ["messageId"] = message.MessageId,
                ["action"] = index == 0 ? "timeout" : "none",
                ["timeoutSeconds"] = index == 0 ? 600 : null,
                ["policySet"] = index == 0 ? "G" : null,
                ["ruleCode"] = null,
                ["reason"] = index == 0
                    ? "Lokaler Moderationstest"
                    : string.Empty,
                ["triggerPhrases"] = index == 0
                    ? new[] { message.LowerText }
                    : [],
                ["confidence"] = index == 0 ? 0.95 : 0.05,
                ["replyTemplateId"] = null,
                ["replyConfidence"] = null,
            })
            .ToArray();
        return JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["envelopeVersion"] =
                    ModerationEnvelopeValidator.ResponseEnvelopeVersion,
                ["batchId"] = request.BatchId,
                ["decisions"] = decisions,
            });
    }

    private sealed class CoordinatorHarness : IAsyncDisposable
    {
        private CoordinatorHarness(
            ModerationCoordinator coordinator,
            FakeEventSubClient eventSub,
            FakeWhitelistPhraseSource whitelist,
            FakePolicyStore policyStore,
            FakeLocalModerationClient localModel,
            FakeRepairClient repair,
            FakeCaseRepository cases,
            ConcurrentQueue<LocalAiRunActivity> aiRunActivities)
        {
            Coordinator = coordinator;
            EventSub = eventSub;
            Whitelist = whitelist;
            PolicyStore = policyStore;
            LocalModel = localModel;
            Repair = repair;
            Cases = cases;
            AiRunActivities = aiRunActivities;
        }

        public ModerationCoordinator Coordinator { get; }

        public FakeEventSubClient EventSub { get; }

        public FakeWhitelistPhraseSource Whitelist { get; }

        public FakePolicyStore PolicyStore { get; }

        public FakeLocalModerationClient LocalModel { get; }

        public FakeRepairClient Repair { get; }

        public FakeCaseRepository Cases { get; }

        public ConcurrentQueue<LocalAiRunActivity> AiRunActivities { get; }

        public static CoordinatorHarness Create(
            IReadOnlySet<string> whitelist,
            Func<ModerationRequestEnvelopeV3, string> modelResponse,
            string repairResponse = "{}",
            Func<int, long>? generalRevisionForCall = null,
            FakeCaseRepository? caseRepository = null,
            IModerationBatcher? batcher = null,
            ITwitchConnectionReadiness? connectionReadiness = null,
            Func<ModerationRequestEnvelopeV3, string>?
                repairResponseFactory = null,
            IReadOnlySet<string>? whitelistedLogins = null,
            Func<ModerationRequestEnvelopeV3, CancellationToken, ValueTask<string>>?
                asyncModelResponse = null,
            ILearningContextRevision? learningContextRevision = null,
            IRetrievalExampleStore? retrievalExampleStore = null,
            bool useCompactContractV6 = false,
            IAiValidationAttemptStore? validationAttemptStore = null,
            IReadOnlyList<ChannelContextSnapshot>? channelContexts = null)
        {
            var contexts = channelContexts?.ToArray() ?? [Context()];
            var eventSub = new FakeEventSubClient();
            var whitelistSource = new FakeWhitelistPhraseSource(
                whitelist,
                whitelistedLogins ?? Set());
            var policyStore = new FakePolicyStore(
                generalRevisionForCall ?? (_ => 1),
                contexts);
            var localModel = asyncModelResponse is null
                ? new FakeLocalModerationClient(modelResponse)
                : new FakeLocalModerationClient(asyncModelResponse);
            var repair = new FakeRepairClient(
                repairResponseFactory ?? (_ => repairResponse));
            var cases = caseRepository ?? new FakeCaseRepository();
            var aiRunMonitor = new LocalAiRunActivityMonitor();
            var aiRunActivities = new ConcurrentQueue<LocalAiRunActivity>();
            aiRunMonitor.ActivityChanged +=
                (_, args) => aiRunActivities.Enqueue(args.Activity);
            var options = new InfrastructureOptions
            {
                TwitchApi = new TwitchApiOptions
                {
                    BroadcasterId = ChannelId,
                    ReaderUserId = "reader-1",
                    ModeratorUserId = "moderator-1",
                },
            };
            options.TwitchApi.SetBroadcasterIds(
                contexts.Select(context => context.ChannelId));
            options.LmStudio.UseCompactModerationContractV6 =
                useCompactContractV6;
            var coordinator = new ModerationCoordinator(
                eventSub,
                new FakeChannelContextService(contexts),
                new CoreChatTextNormalizer(),
                new WhitelistFilterAgent(whitelistSource),
                policyStore,
                new EffectivePolicyResolver(),
                new EmptyPhraseRuleSource(),
                new BlacklistMatcher(),
                batcher ?? new ModerationBatcher(),
                localModel,
                repair,
                new ModerationEnvelopeValidator(),
                retrievalExampleStore ?? new EmptyRetrievalExampleStore(),
                cases,
                new MessageAvailabilityRegistry(),
                options,
                aiRunActivityMonitor: aiRunMonitor,
                connectionReadiness: connectionReadiness,
                learningContextRevision: learningContextRevision
                    ?? new LearningContextRevision(),
                validationAttemptStore: validationAttemptStore);
            return new CoordinatorHarness(
                coordinator,
                eventSub,
                whitelistSource,
                policyStore,
                localModel,
                repair,
                cases,
                aiRunActivities);
        }

        public async ValueTask DisposeAsync()
        {
            await Coordinator.DisposeAsync();
        }
    }

    private sealed class FakeEventSubClient : ITwitchEventSubClient
    {
        private readonly Channel<TwitchChatEvent> _events =
            Channel.CreateUnbounded<TwitchChatEvent>(
                new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false,
                });

        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Publish(TwitchChatEvent chatEvent)
        {
            if (!_events.Writer.TryWrite(chatEvent))
            {
                throw new InvalidOperationException("The fake EventSub channel is closed.");
            }
        }

        public async IAsyncEnumerable<TwitchChatEvent> ReadEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            await foreach (var chatEvent in _events.Reader
                               .ReadAllAsync(cancellationToken))
            {
                yield return chatEvent;
            }
        }
    }

    private sealed class FakeChannelContextService(
        IReadOnlyList<ChannelContextSnapshot> contexts)
        : IChannelContextService
    {
        private readonly Dictionary<string, ChannelContextSnapshot> _contexts =
            contexts.ToDictionary(context => context.ChannelId, StringComparer.Ordinal);

        public ValueTask<ChannelContextSnapshot> GetCurrentAsync(
            string channelId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(_contexts.TryGetValue(channelId, out var context));
            return ValueTask.FromResult(context!);
        }
    }

    private sealed class RecordingModerationBatcher : IModerationBatcher
    {
        private readonly ModerationBatcher _inner = new();
        private int _enqueueCallCount;

        public int EnqueueCallCount => Volatile.Read(ref _enqueueCallCount);

        public int WaitingMessageCount => _inner.WaitingMessageCount;

        public DateTimeOffset? NextDeadlineUtc => _inner.NextDeadlineUtc;

        public BatchIngressResult Enqueue(
            ModerationBatchMessage message,
            ModerationBatchContext context,
            DateTimeOffset nowUtc)
        {
            Interlocked.Increment(ref _enqueueCallCount);
            return _inner.Enqueue(message, context, nowUtc);
        }

        public ModerationBatch? SealIfDue(DateTimeOffset nowUtc)
        {
            return _inner.SealIfDue(nowUtc);
        }

        public ModerationBatch? Flush(DateTimeOffset nowUtc)
        {
            return _inner.Flush(nowUtc);
        }

        public bool TryDequeue(out ModerationBatch? batch)
        {
            return _inner.TryDequeue(out batch);
        }
    }

    private sealed class FakeWhitelistPhraseSource(
        IReadOnlySet<string> phrases,
        IReadOnlySet<string> lowerUserLogins)
        : IWhitelistPhraseSource
    {
        private int _getCallCount;
        private int _recordedHitCount;
        private WhitelistEntryKind? _lastRecordedKind;
        private string? _lastRecordedValue;

        public int GetCallCount => Volatile.Read(ref _getCallCount);

        public int RecordedHitCount => Volatile.Read(ref _recordedHitCount);

        public WhitelistEntryKind? LastRecordedKind => _lastRecordedKind;

        public string? LastRecordedValue => _lastRecordedValue;

        public ValueTask<ActiveWhitelistSnapshot> GetActiveEntriesAsync(
            string channelId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(string.IsNullOrWhiteSpace(channelId));
            Interlocked.Increment(ref _getCallCount);
            return ValueTask.FromResult(
                new ActiveWhitelistSnapshot(phrases, lowerUserLogins));
        }

        public ValueTask RecordHitAsync(
            string channelId,
            WhitelistEntryKind kind,
            string normalizedValue,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(string.IsNullOrWhiteSpace(channelId));
            Assert.NotEqual(default, occurredAtUtc);
            _lastRecordedKind = kind;
            _lastRecordedValue = normalizedValue;
            Interlocked.Increment(ref _recordedHitCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakePolicyStore(
        Func<int, long> generalRevisionForCall,
        IReadOnlyList<ChannelContextSnapshot> contexts)
        : IPolicyStore
    {
        private readonly Dictionary<string, ChannelContextSnapshot> _contexts =
            contexts.ToDictionary(context => context.ChannelId, StringComparer.Ordinal);
        private int _getCallCount;

        public int GetCallCount => Volatile.Read(ref _getCallCount);

        public Task EnsureBroadcasterSeededAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Contains(broadcasterId, _contexts);
            return Task.CompletedTask;
        }

        public Task<StoredEffectivePolicy> GetEffectivePolicyAsync(
            string broadcasterId,
            string? categoryId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(_contexts.TryGetValue(broadcasterId, out var context));
            Assert.Equal(
                context!.CategoryId,
                categoryId);
            var call = Interlocked.Increment(ref _getCallCount);
            var revision = generalRevisionForCall(call);
            return Task.FromResult(
                new StoredEffectivePolicy(
                    broadcasterId,
                    categoryId,
                    revision,
                    CategoryRevision: 1,
                    $"stored-{revision}",
                    string.Empty,
                    [
                        new BlacklistRuleRecord(
                            RuleId: 1,
                            ScopeId: 1,
                            ModerationRuleCodes.GeneralPhrase,
                            "Sonstiger General-Verstoß",
                            "Sonstiger Missbrauch.",
                            ModerationRuleAction.Timeout,
                            DefaultTimeoutSeconds: 600,
                            "Sonstiger Verstoß gegen die Chatregeln.",
                            Priority: 100,
                            Enabled: true,
                            RowVersion: 1,
                            Baseline,
                            Baseline,
                            ArchivedAt: null),
                    ],
                    []));
        }
    }

    private sealed class EmptyPhraseRuleSource : IPhraseRuleSource
    {
        public ValueTask<IReadOnlyList<PhraseRule>> GetRulesAsync(
            string channelId,
            CoreEffectivePolicy policy,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(string.IsNullOrWhiteSpace(channelId));
            Assert.NotNull(policy);
            return ValueTask.FromResult<IReadOnlyList<PhraseRule>>([]);
        }
    }

    private sealed class FakeLocalModerationClient(
        Func<ModerationRequestEnvelopeV3, CancellationToken, ValueTask<string>>
            responseFactory)
        : ILocalModerationClient
    {
        public FakeLocalModerationClient(
            Func<ModerationRequestEnvelopeV3, string> responseFactory)
            : this(
                (request, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return ValueTask.FromResult(responseFactory(request));
                })
        {
        }

        private readonly ConcurrentQueue<ModerationRequestEnvelopeV3> _requests = [];
        private readonly ConcurrentQueue<string?> _schedulingKeys = [];
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<ModerationRequestEnvelopeV3> Requests => _requests.ToArray();

        public IReadOnlyList<string> SchedulingKeys => _schedulingKeys
            .Where(key => key is not null)
            .Cast<string>()
            .ToArray();

        public ValueTask<string> ModerateAsync(
            CompiledModerationPrompt prompt,
            string? schedulingKey,
            CancellationToken cancellationToken = default)
        {
            _schedulingKeys.Enqueue(schedulingKey);
            return ModerateAsync(prompt.Envelope, cancellationToken);
        }

        public async ValueTask<string> ModerateAsync(
            ModerationRequestEnvelopeV3 request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Enqueue(request);
            Interlocked.Increment(ref _callCount);
            return await responseFactory(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FakeRepairClient(
        Func<ModerationRequestEnvelopeV3, string> responseFactory)
        : IModerationRepairClient
    {
        private int _callCount;
        private readonly ConcurrentQueue<ModerationRequestEnvelopeV3> _requests = [];

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<ModerationRequestEnvelopeV3> Requests => _requests.ToArray();

        public ValueTask<string> RepairAsync(
            ModerationRequestEnvelopeV3 originalRequest,
            string invalidResponse,
            IReadOnlyList<EnvelopeValidationError> errors,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(originalRequest);
            Assert.NotNull(invalidResponse);
            Assert.NotEmpty(errors);
            _requests.Enqueue(originalRequest);
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(responseFactory(originalRequest));
        }
    }

    private sealed class RecordingValidationAttemptStore : IAiValidationAttemptStore
    {
        private readonly ConcurrentQueue<AiValidationAttempt> _attempts = [];

        public IReadOnlyList<AiValidationAttempt> Attempts => _attempts.ToArray();

        public ValueTask RecordAsync(
            AiValidationAttempt attempt,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _attempts.Enqueue(attempt);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingValidationAttemptStore : IAiValidationAttemptStore
    {
        public ValueTask RecordAsync(
            AiValidationAttempt attempt,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("diagnostics unavailable");
    }

    private sealed class EmptyRetrievalExampleStore : IRetrievalExampleStore
    {
        public ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantAsync(
            string channelId,
            string? categoryId,
            CoreEffectivePolicy policy,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.False(string.IsNullOrWhiteSpace(channelId));
            Assert.Equal(
                EffectivePolicyResolver.BootstrapJustChattingCategoryId,
                categoryId);
            Assert.NotNull(policy);
            return ValueTask.FromResult<IReadOnlyList<RetrievalExample>>([]);
        }
    }

    private sealed class RecordingRetrievalExampleStore : IRetrievalExampleStore
    {
        private readonly ConcurrentQueue<IReadOnlyList<string>> _queries = [];

        public IReadOnlyList<IReadOnlyList<string>> Queries => _queries.ToArray();

        public ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantAsync(
            string channelId,
            string? categoryId,
            CoreEffectivePolicy policy,
            CancellationToken cancellationToken = default) =>
            GetRelevantAsync(channelId, categoryId, policy, [], cancellationToken);

        public ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantAsync(
            string channelId,
            string? categoryId,
            CoreEffectivePolicy policy,
            IReadOnlyList<string> queryLowerTexts,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ChannelId, channelId);
            Assert.NotNull(policy);
            _queries.Enqueue(queryLowerTexts.ToArray());
            return ValueTask.FromResult<IReadOnlyList<RetrievalExample>>([]);
        }
    }

    private sealed class FakeCaseRepository : IModerationCaseRepository
    {
        private readonly ConcurrentDictionary<long, ModerationCase> _cases = new();
        private readonly object _dedupeGate = new();
        private long _nextId = 100;
        private int _addCallCount;

        public FakeCaseRepository(params ModerationCase[] initialCases)
        {
            foreach (var moderationCase in initialCases)
            {
                _cases[moderationCase.CaseId] = moderationCase;
            }
        }

        public int AddCallCount => Volatile.Read(ref _addCallCount);

        public ValueTask<ModerationCase?> GetAsync(
            long caseId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _cases.TryGetValue(caseId, out var moderationCase);
            return ValueTask.FromResult(moderationCase);
        }

        public ValueTask<long> AddAsync(
            ChatMessageSnapshot message,
            ValidatedModerationDecision decision,
            string policyRevisionFingerprint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                AddCase(message, decision, policyRevisionFingerprint));
        }

        private long AddCase(
            ChatMessageSnapshot message,
            ValidatedModerationDecision decision,
            string policyRevisionFingerprint)
        {
            var caseId = Interlocked.Increment(ref _nextId);
            var moderationCase = new ModerationCase(
                caseId,
                RowVersion: 1,
                message,
                decision.Action,
                decision.TimeoutSeconds,
                decision.Reason,
                decision.PolicySet
                    ?? throw new InvalidOperationException(
                        "An actionable fake decision requires a policy set."),
                decision.TriggerPhrases,
                decision.Confidence,
                policyRevisionFingerprint,
                CoreModerationCaseStatus.Pending,
                DateTimeOffset.UtcNow)
            {
                RuleCode = decision.RuleCode,
            };
            if (!_cases.TryAdd(caseId, moderationCase))
            {
                throw new InvalidOperationException("Duplicate fake moderation case ID.");
            }

            Interlocked.Increment(ref _addCallCount);
            return caseId;
        }

        public ValueTask<ModerationCaseAddResult> AddIfNoActiveDuplicateAsync(
            ChatMessageSnapshot message,
            ValidatedModerationDecision decision,
            string policyRevisionFingerprint,
            ModerationCaseCreationMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_dedupeGate)
            {
                if (!metadata.ReanalysisCaseId.HasValue)
                {
                    var duplicate = _cases.Values.FirstOrDefault(
                        item =>
                            item.Message.Identity.ChannelId ==
                                message.Identity.ChannelId
                            && item.Message.Identity.TwitchUserId ==
                                message.Identity.TwitchUserId
                            && item.Message.Text.RuleNormalizedText ==
                                message.Text.RuleNormalizedText
                            && item.Status is CoreModerationCaseStatus.Pending
                                or CoreModerationCaseStatus.Executing
                                or CoreModerationCaseStatus.OutcomeUnknown);
                    if (duplicate is not null)
                    {
                        return ValueTask.FromResult(
                            new ModerationCaseAddResult(
                                duplicate.CaseId,
                                IsCreated: false));
                    }
                }

                var caseId = AddCase(
                    message,
                    decision,
                    policyRevisionFingerprint);
                return ValueTask.FromResult(
                    new ModerationCaseAddResult(caseId, IsCreated: true));
            }
        }

        public ValueTask<bool> TryTransitionAsync(
            long caseId,
            long expectedRowVersion,
            CoreModerationCaseStatus fromStatus,
            CoreModerationCaseStatus toStatus,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            while (_cases.TryGetValue(caseId, out var existing))
            {
                if (existing.RowVersion != expectedRowVersion || existing.Status != fromStatus)
                {
                    return ValueTask.FromResult(false);
                }

                if (_cases.TryUpdate(
                        caseId,
                        existing with
                        {
                            RowVersion = existing.RowVersion + 1,
                            Status = toStatus,
                        },
                        existing))
                {
                    return ValueTask.FromResult(true);
                }
            }

            return ValueTask.FromResult(false);
        }
    }
}
