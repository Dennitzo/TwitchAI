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

        Assert.Equal("whitelist_exact_match", pipelineEvent.StatusCode);
        Assert.Equal("KEKW", pipelineEvent.ChatMessage?.Text.OriginalText);
        Assert.Equal("kekw", pipelineEvent.ChatMessage?.Text.LowerText);
        Assert.Equal(1, harness.Whitelist.RecordedHitCount);
        Assert.Equal(0, harness.LocalModel.CallCount);
        Assert.Equal(0, harness.Repair.CallCount);
        Assert.Equal(0, harness.Cases.AddCallCount);
    }

    [Fact]
    public async Task BroadcasterAndModeratorMessagesBypassWhitelistAndLocalModel()
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
            count: 5);

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
    public async Task MissingRuleAfterRepairUsesFallbackAndKeepsProposalReviewable()
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
        Assert.Equal(1, harness.Repair.CallCount);
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
        for (var index = 1; index <= count; index++)
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
        string? sharedChatSourceChannelId = null)
    {
        var receivedAt = Baseline.AddMilliseconds(index);
        userId ??= $"user-{index}";
        var identity = ChatMessageIdentityV1.Create(
            $"event-{index}",
            $"message-{index}",
            ChannelId,
            userId,
            $"login-{userId}",
            $"User {userId}",
            text,
            receivedAt,
            sharedChatSourceChannelId,
            badges);
        return new TwitchChatEvent(
            TwitchChatEventKind.Message,
            identity.EventSubNotificationId,
            receivedAt,
            identity,
            ChannelId,
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
                ["timeoutSeconds"] = index == 0 ? 300 : null,
                ["policySet"] = index == 0 ? "G" : null,
                ["ruleCode"] = index == 0
                    ? ModerationRuleCodes.GeneralPhrase
                    : null,
                ["reason"] = index == 0 ? "Lokaler Moderationstest" : string.Empty,
                ["triggerPhrases"] = index == 0 ? new[] { message.LowerText } : [],
                ["confidence"] = index == 0 ? 0.95 : 0.05,
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

    private static string MissingRuleEnvelope(ModerationRequestEnvelopeV3 request)
    {
        var decisions = request.Messages
            .Select((message, index) => new Dictionary<string, object?>
            {
                ["messageId"] = message.MessageId,
                ["action"] = index == 0 ? "timeout" : "none",
                ["timeoutSeconds"] = index == 0 ? 300 : null,
                ["policySet"] = index == 0 ? "G" : null,
                ["ruleCode"] = null,
                ["reason"] = index == 0
                    ? "Lokaler Moderationstest"
                    : string.Empty,
                ["triggerPhrases"] = index == 0
                    ? new[] { message.LowerText }
                    : [],
                ["confidence"] = index == 0 ? 0.95 : 0.05,
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
                repairResponseFactory = null)
        {
            var eventSub = new FakeEventSubClient();
            var whitelistSource = new FakeWhitelistPhraseSource(whitelist);
            var policyStore = new FakePolicyStore(generalRevisionForCall ?? (_ => 1));
            var localModel = new FakeLocalModerationClient(modelResponse);
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
            var coordinator = new ModerationCoordinator(
                eventSub,
                new FakeChannelContextService(Context()),
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
                new EmptyRetrievalExampleStore(),
                cases,
                new MessageAvailabilityRegistry(),
                options,
                aiRunActivityMonitor: aiRunMonitor,
                connectionReadiness: connectionReadiness);
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

    private sealed class FakeChannelContextService(ChannelContextSnapshot context)
        : IChannelContextService
    {
        public ValueTask<ChannelContextSnapshot> GetCurrentAsync(
            string channelId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(context.ChannelId, channelId);
            return ValueTask.FromResult(context);
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

    private sealed class FakeWhitelistPhraseSource(IReadOnlySet<string> phrases)
        : IWhitelistPhraseSource
    {
        private int _getCallCount;
        private int _recordedHitCount;

        public int GetCallCount => Volatile.Read(ref _getCallCount);

        public int RecordedHitCount => Volatile.Read(ref _recordedHitCount);

        public ValueTask<IReadOnlySet<string>> GetActiveLowerPhrasesAsync(
            string channelId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ChannelId, channelId);
            Interlocked.Increment(ref _getCallCount);
            return ValueTask.FromResult(phrases);
        }

        public ValueTask RecordHitAsync(
            string channelId,
            string lowerPhrase,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ChannelId, channelId);
            Assert.Equal("kekw", lowerPhrase);
            Assert.NotEqual(default, occurredAtUtc);
            Interlocked.Increment(ref _recordedHitCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakePolicyStore(Func<int, long> generalRevisionForCall)
        : IPolicyStore
    {
        private int _getCallCount;

        public int GetCallCount => Volatile.Read(ref _getCallCount);

        public Task EnsureBroadcasterSeededAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ChannelId, broadcasterId);
            return Task.CompletedTask;
        }

        public Task<StoredEffectivePolicy> GetEffectivePolicyAsync(
            string broadcasterId,
            string? categoryId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(ChannelId, broadcasterId);
            Assert.Equal(
                EffectivePolicyResolver.BootstrapJustChattingCategoryId,
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
            Assert.Equal(ChannelId, channelId);
            Assert.NotNull(policy);
            return ValueTask.FromResult<IReadOnlyList<PhraseRule>>([]);
        }
    }

    private sealed class FakeLocalModerationClient(
        Func<ModerationRequestEnvelopeV3, string> responseFactory)
        : ILocalModerationClient
    {
        private readonly ConcurrentQueue<ModerationRequestEnvelopeV3> _requests = [];
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public IReadOnlyList<ModerationRequestEnvelopeV3> Requests => _requests.ToArray();

        public ValueTask<string> ModerateAsync(
            ModerationRequestEnvelopeV3 request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _requests.Enqueue(request);
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(responseFactory(request));
        }
    }

    private sealed class FakeRepairClient(
        Func<ModerationRequestEnvelopeV3, string> responseFactory)
        : IModerationRepairClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

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
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(responseFactory(originalRequest));
        }
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
            Assert.Equal(ChannelId, channelId);
            Assert.Equal(
                EffectivePolicyResolver.BootstrapJustChattingCategoryId,
                categoryId);
            Assert.NotNull(policy);
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
