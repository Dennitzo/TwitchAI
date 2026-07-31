using System.Collections.Concurrent;
using System.Diagnostics;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Twitch;
using CoreEffectivePolicySnapshot = TwitchAI.Core.Models.EffectivePolicySnapshot;
using CoreModerationCaseStatus = TwitchAI.Core.Models.ModerationCaseStatus;
using CoreTextNormalizer = TwitchAI.Core.Contracts.IChatTextNormalizer;

namespace TwitchAI.Infrastructure.Pipeline;

public enum ModerationPipelineEventKind
{
    Started,
    Stopped,
    CategoryChanged,
    ChatQueued,
    QueueDuplicateSkipped,
    WhitelistSkipped,
    Allowed,
    CaseCreated,
    ChatReplyCreated,
    DuplicateProposalSuppressed,
    DroppedOverload,
    BatchRejected,
    StalePolicyRequeued,
    StaleLearningRequeued,
    AvailabilityChanged,
    Faulted,
}

public sealed class ModerationPipelineEventArgs : EventArgs
{
    public ModerationPipelineEventArgs(
        ModerationPipelineEventKind kind,
        string statusCode,
        string message,
        ChatMessageSnapshot? chatMessage = null,
        ValidatedModerationDecision? decision = null,
        long? caseId = null,
        ChannelContextSnapshot? channelContext = null,
        string? targetMessageId = null,
        string? targetUserId = null,
        DateTimeOffset? occurredAtUtc = null,
        long? caseRowVersion = null,
        IReadOnlyList<string>? localMatchCodes = null,
        long? aiLatencyMilliseconds = null,
        IReadOnlyList<PolicySetCode>? activePolicySets = null,
        Exception? exception = null,
        ChatReplyProposalRecord? chatReplyProposal = null,
        IReadOnlyList<ModerationRuleDefinition>? activeRules = null)
    {
        Kind = kind;
        StatusCode = statusCode;
        Message = message;
        ChatMessage = chatMessage;
        Decision = decision;
        CaseId = caseId;
        ChannelContext = channelContext;
        TargetMessageId = targetMessageId;
        TargetUserId = targetUserId;
        OccurredAtUtc = occurredAtUtc;
        CaseRowVersion = caseRowVersion;
        LocalMatchCodes = localMatchCodes ?? [];
        AiLatencyMilliseconds = aiLatencyMilliseconds;
        ActivePolicySets = activePolicySets ?? [];
        ActiveRules = activeRules?.ToArray() ?? [];
        Exception = exception;
        ChatReplyProposal = chatReplyProposal;
    }

    public ModerationPipelineEventKind Kind { get; }

    public string StatusCode { get; }

    public string Message { get; }

    public ChatMessageSnapshot? ChatMessage { get; }

    public ValidatedModerationDecision? Decision { get; }

    public long? CaseId { get; }

    public ChannelContextSnapshot? ChannelContext { get; }

    public string? TargetMessageId { get; }

    public string? TargetUserId { get; }

    public DateTimeOffset? OccurredAtUtc { get; }

    public long? CaseRowVersion { get; }

    public IReadOnlyList<string> LocalMatchCodes { get; }

    public long? AiLatencyMilliseconds { get; }

    public IReadOnlyList<PolicySetCode> ActivePolicySets { get; }

    public IReadOnlyList<ModerationRuleDefinition> ActiveRules { get; }

    public Exception? Exception { get; }

    public ChatReplyProposalRecord? ChatReplyProposal { get; }
}

public interface IModerationCoordinator
{
    event EventHandler<ModerationPipelineEventArgs>? EventReceived;

    bool IsRunning { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);

    Task ReanalyzeCaseAsync(
        long caseId,
        CancellationToken cancellationToken = default);
}

public sealed class ModerationCoordinator : IModerationCoordinator, IAsyncDisposable
{
    private static readonly TimeSpan DeadlineResolution = TimeSpan.FromMilliseconds(100);
    private const int MaximumReplyTemplatesPerBatch = 32;
    private const int MaximumReplyTemplateCharacters = 2_400;

    private readonly object _lifecycleGate = new();
    private readonly object _contextGate = new();
    private readonly ITwitchEventSubClient _eventSubClient;
    private readonly IChannelContextService _channelContextService;
    private readonly CoreTextNormalizer _normalizer;
    private readonly IWhitelistFilterAgent _whitelist;
    private readonly IPolicyStore _policyStore;
    private readonly IEffectivePolicyResolver _policyResolver;
    private readonly IPhraseRuleSource _phraseRuleSource;
    private readonly IBlacklistMatcher _blacklistMatcher;
    private readonly IModerationBatcher _batcher;
    private readonly ILocalModerationClient _localModerationClient;
    private readonly IModerationRepairClient _repairClient;
    private readonly IModerationEnvelopeValidator _envelopeValidator;
    private readonly IRetrievalExampleStore _retrievalExampleStore;
    private readonly ILearningContextRevision _learningContextRevision;
    private readonly IModerationCaseRepository _caseRepository;
    private readonly IMessageAvailabilityRegistry _availabilityRegistry;
    private readonly IMessageAvailabilityStore? _availabilityStore;
    private readonly ILocalAiRunActivityMonitor? _aiRunActivityMonitor;
    private readonly ITwitchConnectionReadiness? _connectionReadiness;
    private readonly IChatReplyStore? _chatReplyStore;
    private readonly InfrastructureOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, IReadOnlyList<PhraseRule>> _ruleCache =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _inferenceSignal = new(0);
    private CancellationTokenSource? _runSource;
    private Task[] _runTasks = [];
    private ChannelContextSnapshot? _currentContext;
    private long _ingressSequence;
    private long _temporaryMessageSequence;
    private bool _disposed;

    public ModerationCoordinator(
        ITwitchEventSubClient eventSubClient,
        IChannelContextService channelContextService,
        CoreTextNormalizer normalizer,
        IWhitelistFilterAgent whitelist,
        IPolicyStore policyStore,
        IEffectivePolicyResolver policyResolver,
        IPhraseRuleSource phraseRuleSource,
        IBlacklistMatcher blacklistMatcher,
        IModerationBatcher batcher,
        ILocalModerationClient localModerationClient,
        IModerationRepairClient repairClient,
        IModerationEnvelopeValidator envelopeValidator,
        IRetrievalExampleStore retrievalExampleStore,
        IModerationCaseRepository caseRepository,
        IMessageAvailabilityRegistry availabilityRegistry,
        InfrastructureOptions options,
        IMessageAvailabilityStore? availabilityStore = null,
        ILocalAiRunActivityMonitor? aiRunActivityMonitor = null,
        TimeProvider? timeProvider = null,
        ITwitchConnectionReadiness? connectionReadiness = null,
        IChatReplyStore? chatReplyStore = null,
        ILearningContextRevision? learningContextRevision = null)
    {
        _eventSubClient = eventSubClient;
        _channelContextService = channelContextService;
        _normalizer = normalizer;
        _whitelist = whitelist;
        _policyStore = policyStore;
        _policyResolver = policyResolver;
        _phraseRuleSource = phraseRuleSource;
        _blacklistMatcher = blacklistMatcher;
        _batcher = batcher;
        _localModerationClient = localModerationClient;
        _repairClient = repairClient;
        _envelopeValidator = envelopeValidator;
        _retrievalExampleStore = retrievalExampleStore;
        _learningContextRevision = learningContextRevision
            ?? LearningContextRevision.ProcessWide;
        _caseRepository = caseRepository;
        _availabilityRegistry = availabilityRegistry;
        _availabilityStore = availabilityStore;
        _aiRunActivityMonitor = aiRunActivityMonitor;
        _connectionReadiness = connectionReadiness;
        _chatReplyStore = chatReplyStore;
        _options = options;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public event EventHandler<ModerationPipelineEventArgs>? EventReceived;

    public bool IsRunning
    {
        get
        {
            lock (_lifecycleGate)
            {
                return _runSource is { IsCancellationRequested: false };
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connectionReadiness is not null
            && !_connectionReadiness.IsSessionReady)
        {
            throw new InvalidOperationException(
                "Die Analyse kann erst gestartet werden, wenn die Twitch-Anmeldung "
                + "und der ausgewählte Moderationskanal erfolgreich verbunden sind.");
        }

        CancellationTokenSource source;
        lock (_lifecycleGate)
        {
            if (_runSource is { IsCancellationRequested: false })
            {
                return;
            }

            ValidateRuntimeConfiguration();
            source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _runSource = source;
        }

        try
        {
            await _policyStore
                .EnsureBroadcasterSeededAsync(
                    _options.TwitchApi.BroadcasterId,
                    source.Token)
                .ConfigureAwait(false);
            var initialContext = await _channelContextService
                .GetCurrentAsync(_options.TwitchApi.BroadcasterId, source.Token)
                .ConfigureAwait(false);
            SetCurrentContext(initialContext);
            var initialPolicy = await ResolvePolicyAsync(initialContext, source.Token)
                .ConfigureAwait(false);

            if (_connectionReadiness is not null
                && !_connectionReadiness.IsSessionReady)
            {
                throw new InvalidOperationException(
                    "Die Twitch-Verbindung wurde während des Analysestarts getrennt. "
                    + "Bitte Twitch erneut verbinden.");
            }

            _runTasks =
            [
                ConsumeEventSubAsync(source),
                SealDeadlinesAsync(source),
                RunInferenceAsync(source),
            ];
            if (_connectionReadiness is not null)
            {
                using var readinessTimeout =
                    CancellationTokenSource.CreateLinkedTokenSource(
                        source.Token,
                        cancellationToken);
                readinessTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                try
                {
                    await _connectionReadiness
                        .WaitForEventSubReadyAsync(readinessTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !source.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        "EventSub konnte innerhalb von 45 Sekunden nicht "
                        + "empfangsbereit verbunden werden.");
                }
                catch (OperationCanceledException) when (
                    source.IsCancellationRequested
                    && !cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        "EventSub wurde während des Verbindungsaufbaus beendet. "
                        + _connectionReadiness.Current.Status);
                }

                if (!_connectionReadiness.IsFullyReady)
                {
                    throw new InvalidOperationException(
                        "Die Twitch- oder EventSub-Verbindung wurde während "
                        + "des Analysestarts getrennt.");
                }
            }

            Publish(
                new(
                    ModerationPipelineEventKind.Started,
                    "analysis_started",
                    "Twitch moderation analysis started.",
                    channelContext: initialContext,
                    activePolicySets: initialPolicy.ActivePolicySets
                        .Select(item => item.Code)
                        .ToArray()));
        }
        catch
        {
            await source.CancelAsync().ConfigureAwait(false);
            _inferenceSignal.Release();
            Task[] tasksToAwait = [];
            var ownsSource = false;
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_runSource, source))
                {
                    _runSource = null;
                    tasksToAwait = _runTasks;
                    _runTasks = [];
                    ownsSource = true;
                }
            }

            if (tasksToAwait.Length > 0)
            {
                try
                {
                    await Task.WhenAll(tasksToAwait).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    source.IsCancellationRequested)
                {
                    // Expected cleanup after a failed readiness check.
                }
            }

            if (ownsSource)
            {
                source.Dispose();
            }

            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? source;
        Task[] tasks;
        lock (_lifecycleGate)
        {
            source = _runSource;
            tasks = _runTasks;
            _runSource = null;
            _runTasks = [];
        }

        if (source is null)
        {
            return;
        }

        await source.CancelAsync().ConfigureAwait(false);
        _inferenceSignal.Release();
        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            source.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            // A paused coordinator cancels EventSub and an in-flight local inference.
        }
        finally
        {
            source.Dispose();
        }

        if (_availabilityStore is not null)
        {
            await _availabilityStore.RecordOfflineGapForPendingCasesAsync(
                _timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
        }

        Publish(
            new(
                ModerationPipelineEventKind.Stopped,
                "analysis_stopped",
                "Twitch moderation analysis stopped."));
    }

    public async Task ReanalyzeCaseAsync(
        long caseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(caseId);
        if (!IsRunning)
        {
            throw new InvalidOperationException(
                "Die Analyse muss für eine erneute Bewertung aktiv sein.");
        }

        var moderationCase = await _caseRepository
            .GetAsync(caseId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Moderationsfall '{caseId}' wurde nicht gefunden.");
        if (moderationCase.Status != CoreModerationCaseStatus.StalePolicy)
        {
            throw new InvalidOperationException(
                "Nur ein wegen veralteter Regeln blockierter Fall kann erneut analysiert werden.");
        }

        var message = moderationCase.Message;
        if (IsPrivilegedSender(message.Identity))
        {
            if (!await _caseRepository
                    .TryCompleteReanalysisWithoutActionAsync(
                        caseId,
                        moderationCase.RowVersion,
                        wasWhitelisted: true,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Der veraltete Moderationsfall wurde während der Rollenprüfung parallel geändert.");
            }

            Publish(
                new(
                    ModerationPipelineEventKind.WhitelistSkipped,
                    "stale_case_privileged_sender",
                    "The stale moderation case belongs to an automatically whitelisted Twitch role and was skipped.",
                    message,
                    caseId: caseId));
            return;
        }

        if (await _whitelist
                .ShouldSkipAsync(
                    message,
                    _timeProvider.GetUtcNow(),
                    cancellationToken)
                .ConfigureAwait(false))
        {
            if (!await _caseRepository
                    .TryCompleteReanalysisWithoutActionAsync(
                        caseId,
                        moderationCase.RowVersion,
                        wasWhitelisted: true,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    "Der veraltete Moderationsfall wurde während der Whitelist-Prüfung parallel geändert.");
            }

            Publish(
                new(
                    ModerationPipelineEventKind.WhitelistSkipped,
                    "stale_case_now_whitelisted",
                    "The stale moderation case now matches the manual whitelist.",
                    message,
                    caseId: caseId));
            return;
        }

        var policy = await ResolvePolicyAsync(message.ChannelContext, cancellationToken)
            .ConfigureAwait(false);
        var rules = await GetRulesAsync(
                message.Identity.ChannelId,
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        var matches = _blacklistMatcher.Match(message.Text, policy, rules);
        var temporaryId = $"m{Interlocked.Increment(ref _temporaryMessageSequence):x}";
        var ingress = _batcher.Enqueue(
            new ModerationBatchMessage(temporaryId, message, matches)
            {
                ReanalysisCaseId = caseId,
                ReanalysisCaseRowVersion = moderationCase.RowVersion,
            },
            new ModerationBatchContext(message.ChannelContext, policy),
            _timeProvider.GetUtcNow());

        foreach (var dropped in ingress.DroppedMessages)
        {
            Publish(
                new(
                    ModerationPipelineEventKind.DroppedOverload,
                    "queue_overflow",
                    "An older queued message was not analyzed because the 500-message limit was reached.",
                    dropped.Message));
        }

        Publish(
            new(
                ModerationPipelineEventKind.StalePolicyRequeued,
                "stale_case_requeued",
                "The blocked moderation case was queued with the current policy revision.",
                message,
                caseId: caseId));
        PublishBatchIngressUpdates(ingress);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _inferenceSignal.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private async Task ConsumeEventSubAsync(CancellationTokenSource source)
    {
        try
        {
            await foreach (var chatEvent in _eventSubClient
                               .ReadEventsAsync(source.Token)
                               .ConfigureAwait(false))
            {
                await ProcessEventAsync(chatEvent, source.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // Expected pause/shutdown path.
        }
        catch (Exception exception)
        {
            PublishFault("eventsub_failed", "EventSub processing stopped.", exception);
            await source.CancelAsync().ConfigureAwait(false);
            _inferenceSignal.Release();
        }
    }

    private async Task ProcessEventAsync(
        TwitchChatEvent chatEvent,
        CancellationToken cancellationToken)
    {
        switch (chatEvent.Kind)
        {
            case TwitchChatEventKind.Message when chatEvent.Message is not null:
                await ProcessMessageAsync(chatEvent.Message, cancellationToken)
                    .ConfigureAwait(false);
                break;
            case TwitchChatEventKind.ChannelUpdated when chatEvent.ChannelContext is not null:
            {
                var previousContext = GetCurrentContext();
                var batchingContextChanged = previousContext is null
                    || !HasSameBatchingCategory(
                        previousContext,
                        chatEvent.ChannelContext);
                if (previousContext is not null && batchingContextChanged)
                {
                    var flushed = _batcher.Flush(_timeProvider.GetUtcNow());
                    if (flushed is not null)
                    {
                        SignalSealedBatch(flushed);
                    }
                }

                SetCurrentContext(chatEvent.ChannelContext);
                var activePolicy = await ResolvePolicyAsync(
                    chatEvent.ChannelContext,
                    cancellationToken).ConfigureAwait(false);
                Publish(
                    new(
                        ModerationPipelineEventKind.CategoryChanged,
                        batchingContextChanged
                            ? "category_changed"
                            : "category_reconciled",
                        batchingContextChanged
                            ? "The Twitch category or verification state changed."
                            : "The Twitch category snapshot was reconciled without changing the batching context.",
                        channelContext: chatEvent.ChannelContext,
                        activePolicySets: activePolicy.ActivePolicySets
                            .Select(item => item.Code)
                            .ToArray()));
                break;
            }
            case TwitchChatEventKind.MessageDeleted
                when chatEvent.TargetMessageId is not null
                     && chatEvent.TargetUserId is not null:
                _availabilityRegistry.MarkDeletedExact(
                    chatEvent.EventSubNotificationId,
                    chatEvent.ChannelId,
                    chatEvent.TargetMessageId,
                    chatEvent.TargetUserId,
                    chatEvent.ReceivedAtUtc,
                    chatEvent.ReceivedAtUtc);
                PublishAvailability(chatEvent, "message_deleted");
                break;
            case TwitchChatEventKind.UserMessagesCleared when chatEvent.TargetUserId is not null:
                _availabilityRegistry.MarkUserCleared(
                    chatEvent.EventSubNotificationId,
                    chatEvent.ChannelId,
                    chatEvent.TargetUserId,
                    chatEvent.ReceivedAtUtc);
                PublishAvailability(chatEvent, "user_messages_cleared");
                break;
            case TwitchChatEventKind.RoomCleared:
                _availabilityRegistry.MarkRoomCleared(
                    chatEvent.EventSubNotificationId,
                    chatEvent.ChannelId,
                    chatEvent.ReceivedAtUtc);
                PublishAvailability(chatEvent, "room_cleared");
                break;
            case TwitchChatEventKind.EventGap:
            {
                _availabilityRegistry.MarkUnknownGap(
                    chatEvent.ChannelId,
                    chatEvent.ReceivedAtUtc);
                var currentContext = GetCurrentContext();
                if (currentContext is not null)
                {
                    var flushed = _batcher.Flush(_timeProvider.GetUtcNow());
                    if (flushed is not null)
                    {
                        SignalSealedBatch(flushed);
                    }

                    var unverified = currentContext with
                    {
                        ObservedAtUtc = chatEvent.ReceivedAtUtc,
                        Revision = currentContext.Revision + 1,
                        IsVerified = false,
                    };
                    SetCurrentContext(unverified);
                    Publish(
                        new(
                            ModerationPipelineEventKind.CategoryChanged,
                            "category_unverified_after_eventsub_gap",
                            "The category could not be verified after an EventSub gap; only General remains active.",
                            channelContext: unverified,
                            activePolicySets: [PolicySetCode.General]));
                }

                PublishAvailability(chatEvent, "eventsub_gap");
                break;
            }
        }
    }

    private async Task ProcessMessageAsync(
        ChatMessageIdentityV1 identity,
        CancellationToken cancellationToken)
    {
        var context = GetCurrentContext()
            ?? await _channelContextService
                .GetCurrentAsync(identity.ChannelId, cancellationToken)
                .ConfigureAwait(false);
        var processed = _normalizer.Process(identity.OriginalText);
        var snapshot = new ChatMessageSnapshot(
            identity,
            processed,
            context,
            Interlocked.Increment(ref _ingressSequence));
        _availabilityRegistry.Observe(snapshot);

        if (IsPrivilegedSender(identity))
        {
            Publish(
                new(
                    ModerationPipelineEventKind.WhitelistSkipped,
                    "privileged_sender",
                    "Messages from broadcasters, moderators, VIPs, and identified chat bots bypass local moderation.",
                    snapshot));
            return;
        }

        if (await _whitelist
                .ShouldSkipAsync(
                    snapshot,
                    identity.ReceivedAtUtc,
                    cancellationToken)
                .ConfigureAwait(false))
        {
            Publish(
                new(
                    ModerationPipelineEventKind.WhitelistSkipped,
                    "whitelist_match",
                    "The message or Twitch login matched the manual whitelist.",
                    snapshot));
            return;
        }

        var policy = await ResolvePolicyAsync(context, cancellationToken).ConfigureAwait(false);
        var rules = await GetRulesAsync(identity.ChannelId, policy, cancellationToken)
            .ConfigureAwait(false);
        var matches = _blacklistMatcher.Match(processed, policy, rules);
        var temporaryId = $"m{Interlocked.Increment(ref _temporaryMessageSequence):x}";
        var ingress = _batcher.Enqueue(
            new ModerationBatchMessage(temporaryId, snapshot, matches),
            new ModerationBatchContext(context, policy),
            _timeProvider.GetUtcNow());

        foreach (var dropped in ingress.DroppedMessages)
        {
            Publish(
                new(
                    ModerationPipelineEventKind.DroppedOverload,
                    "queue_overflow",
                    "An older queued message was not analyzed because the 500-message limit was reached.",
                    dropped.Message));
        }

        if (ingress.Disposition == BatchIngressDisposition.DuplicateSuppressed)
        {
            PublishQueueDuplicateSkipped(snapshot);
            PublishBatchIngressUpdates(ingress);
            return;
        }

        Publish(
            new(
                ModerationPipelineEventKind.ChatQueued,
                "queued_for_ai",
                "The lowercase message was queued for local moderation.",
                snapshot));
        PublishBatchIngressUpdates(ingress);
    }

    private async Task SealDeadlinesAsync(CancellationTokenSource source)
    {
        using var timer = new PeriodicTimer(DeadlineResolution);
        try
        {
            while (await timer.WaitForNextTickAsync(source.Token).ConfigureAwait(false))
            {
                if (_batcher.SealIfDue(_timeProvider.GetUtcNow()) is { } batch)
                {
                    SignalSealedBatch(batch);
                }
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // Expected pause/shutdown path.
        }
    }

    private async Task RunInferenceAsync(CancellationTokenSource source)
    {
        try
        {
            while (!source.IsCancellationRequested)
            {
                await _inferenceSignal.WaitAsync(source.Token).ConfigureAwait(false);
                while (_batcher.TryDequeue(out var batch) && batch is not null)
                {
                    PublishRunningAiRun(batch);
                    try
                    {
                        await AnalyzeBatchAsync(batch, source.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (source.IsCancellationRequested)
                    {
                        PublishFailedAiRun(batch, "cancelled");
                        throw;
                    }
                    catch (Exception exception)
                    {
                        PublishFailedAiRun(batch, exception.GetType().Name);
                        Publish(
                            new(
                                ModerationPipelineEventKind.BatchRejected,
                                "ai_batch_failed",
                                "The local AI batch failed; live analysis continues with the next batch.",
                                exception: exception));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            // Expected pause/shutdown path.
        }
        catch (Exception exception)
        {
            PublishFault("inference_failed", "Local moderation inference stopped.", exception);
            await source.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task AnalyzeBatchAsync(
        ModerationBatch batch,
        CancellationToken cancellationToken)
    {
        var latestPolicy = await ResolvePolicyAsync(batch.Context.Channel, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                latestPolicy.RevisionFingerprint,
                batch.Context.EffectivePolicy.RevisionFingerprint,
                StringComparison.Ordinal))
        {
            await RequeueForPolicyAsync(batch, latestPolicy, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var learningRevision = _learningContextRevision.Current;
        var retrievalExamples = await _retrievalExampleStore
            .GetRelevantAsync(
                batch.Context.Channel.ChannelId,
                batch.Context.Channel.CategoryId,
                batch.Context.EffectivePolicy,
                batch.Messages
                    .Select(item => item.Message.Text.LowerText)
                    .ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        if (_learningContextRevision.Current != learningRevision)
        {
            await RequeueForLearningAsync(batch, latestPolicy, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var replyTemplateMap = await CreateReplyTemplateSnapshotAsync(
                batch,
                cancellationToken)
            .ConfigureAwait(false);
        var analysisBatch = batch with
        {
            ReplyTemplates = replyTemplateMap
                .Select(item => new ChatReplyTemplatePrompt(
                    item.Key,
                    item.Value.NormalizedExample))
                .ToArray(),
        };
        var prompt = ModerationRequestFactory.Create(analysisBatch, retrievalExamples);
        var inferenceStarted = Stopwatch.GetTimestamp();
        var rawResponse = await _localModerationClient
            .ModerateAsync(prompt.Envelope, cancellationToken)
            .ConfigureAwait(false);
        var validation = _envelopeValidator.Validate(rawResponse, analysisBatch);
        if (!validation.IsValid)
        {
            // One bounded repair request includes the validator errors. No third request is issued.
            rawResponse = await _repairClient
                .RepairAsync(
                    prompt.Envelope,
                    rawResponse,
                    validation.Errors,
                    cancellationToken)
                .ConfigureAwait(false);
            validation = _envelopeValidator.Validate(rawResponse, analysisBatch);
        }

        if (!validation.IsValid
            && validation.Errors.Any(
                error => string.Equals(
                    error.Code,
                    "rule_invalid",
                    StringComparison.Ordinal)))
        {
            // The model already received its single bounded repair request.
            // Keep an otherwise valid actionable proposal reviewable by binding
            // an omitted category to a matching local hit or the policy's base
            // rule. Unknown/inactive explicit codes remain invalid.
            validation = _envelopeValidator
                .ValidateWithMissingRuleFallback(rawResponse, analysisBatch);
        }

        // A changed learning set always requires a fresh request, even when the
        // old response happened to be invalid. Otherwise that whole batch would
        // be discarded instead of being evaluated with the corrected examples.
        if (_learningContextRevision.Current != learningRevision)
        {
            await RequeueForLearningAsync(batch, latestPolicy, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!validation.IsValid)
        {
            Publish(
                new(
                    ModerationPipelineEventKind.BatchRejected,
                    "invalid_ai_envelope",
                    string.Join(
                        "; ",
                        validation.Errors.Select(error => $"{error.Code}: {error.Message}"))));
            return;
        }

        var aiLatencyMilliseconds = checked(
            (long)Math.Round(
                Stopwatch.GetElapsedTime(inferenceStarted).TotalMilliseconds,
                MidpointRounding.AwayFromZero));
        latestPolicy = await ResolvePolicyAsync(batch.Context.Channel, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                latestPolicy.RevisionFingerprint,
                batch.Context.EffectivePolicy.RevisionFingerprint,
                StringComparison.Ordinal))
        {
            await RequeueForPolicyAsync(batch, latestPolicy, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (_learningContextRevision.Current != learningRevision)
        {
            await RequeueForLearningAsync(batch, latestPolicy, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var messageById = batch.Messages.ToDictionary(
            item => item.TemporaryMessageId,
            StringComparer.Ordinal);
        foreach (var decision in validation.Envelope!.Decisions)
        {
            var batchMessage = messageById[decision.TemporaryMessageId];
            var message = batchMessage.Message;
            var localMatchCodes = batchMessage.LocalMatches
                .Select(match => match.RuleCode)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (decision.Action == ModerationActionKind.None)
            {
                if (batchMessage.ReanalysisCaseId is long reanalysisCaseId)
                {
                    var completed =
                        batchMessage.ReanalysisCaseRowVersion is long reanalysisRowVersion &&
                        await _caseRepository
                            .TryCompleteReanalysisWithoutActionAsync(
                                reanalysisCaseId,
                                reanalysisRowVersion,
                                wasWhitelisted: false,
                                cancellationToken)
                            .ConfigureAwait(false);
                    if (!completed)
                    {
                        Publish(
                            new(
                                ModerationPipelineEventKind.BatchRejected,
                                "stale_case_completion_conflict",
                                "The stale moderation case changed before its no-action reanalysis could be stored.",
                                message,
                                decision,
                                caseId: reanalysisCaseId));
                        continue;
                    }
                }

                if (decision.ReplyTemplateId is { } replyTemplateId
                    && replyTemplateMap.TryGetValue(
                        replyTemplateId,
                        out var replyTemplate)
                    && _chatReplyStore is not null)
                {
                    if (await _whitelist
                            .ShouldSkipAsync(
                                message,
                                _timeProvider.GetUtcNow(),
                                cancellationToken)
                            .ConfigureAwait(false))
                    {
                        Publish(
                            new(
                                ModerationPipelineEventKind.WhitelistSkipped,
                                "message_whitelisted_during_reply_inference",
                                "The message or Twitch login was added to the manual whitelist while the reply was being evaluated.",
                                message,
                                decision,
                                caseId: batchMessage.ReanalysisCaseId,
                                localMatchCodes: localMatchCodes,
                                aiLatencyMilliseconds: aiLatencyMilliseconds));
                        continue;
                    }

                    string renderedResponse;
                    try
                    {
                        renderedResponse = SqliteChatReplyStore.RenderResponse(
                            replyTemplate.ResponseMessage,
                            message.Identity.Login);
                    }
                    catch (ArgumentException exception)
                    {
                        Publish(
                            new(
                                ModerationPipelineEventKind.BatchRejected,
                                "chat_reply_render_invalid",
                                exception.Message,
                                message,
                                decision,
                                localMatchCodes: localMatchCodes,
                                aiLatencyMilliseconds: aiLatencyMilliseconds));
                        continue;
                    }

                    var replyAddResult = await _chatReplyStore
                        .AddProposalAsync(
                            message,
                            replyTemplate,
                            renderedResponse,
                            decision.ReplyConfidence ?? decision.Confidence,
                            batch.BatchId,
                            aiLatencyMilliseconds,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (replyAddResult is { IsCreated: true })
                    {
                        Publish(
                            new(
                                ModerationPipelineEventKind.ChatReplyCreated,
                                "chat_reply_proposal_created",
                                "A semantic chat reply match awaits human review.",
                                message,
                                decision,
                                caseId: replyAddResult.Proposal.ProposalId,
                                caseRowVersion: replyAddResult.Proposal.RowVersion,
                                localMatchCodes: localMatchCodes,
                                aiLatencyMilliseconds: aiLatencyMilliseconds,
                                chatReplyProposal: replyAddResult.Proposal));
                    }
                    else if (replyAddResult is { IsCreated: false })
                    {
                        Publish(
                            new(
                                ModerationPipelineEventKind.DuplicateProposalSuppressed,
                                "active_duplicate_chat_reply",
                                "This Twitch message already has a chat reply proposal.",
                                message,
                                decision,
                                caseId: replyAddResult.Proposal.ProposalId,
                                localMatchCodes: localMatchCodes,
                                aiLatencyMilliseconds: aiLatencyMilliseconds));
                    }
                    else
                    {
                        Publish(
                            new(
                                ModerationPipelineEventKind.Allowed,
                                "chat_reply_template_changed",
                                "The selected chat reply template changed during inference.",
                                message,
                                decision,
                                localMatchCodes: localMatchCodes,
                                aiLatencyMilliseconds: aiLatencyMilliseconds));
                    }

                    continue;
                }

                Publish(
                    new(
                        ModerationPipelineEventKind.Allowed,
                        "ai_no_action",
                        "The local model proposed no moderation action.",
                        message,
                        decision,
                        caseId: batchMessage.ReanalysisCaseId,
                        localMatchCodes: localMatchCodes,
                        aiLatencyMilliseconds: aiLatencyMilliseconds));
                continue;
            }

            // The manual whitelist is deliberately not learned from moderation
            // decisions and can change while LM Studio is evaluating a batch.
            // Re-check it immediately before a case becomes actionable.
            if (await _whitelist
                    .ShouldSkipAsync(
                        message,
                        _timeProvider.GetUtcNow(),
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                if (batchMessage.ReanalysisCaseId is long reanalysisCaseId)
                {
                    var completed =
                        batchMessage.ReanalysisCaseRowVersion is long reanalysisRowVersion &&
                        await _caseRepository
                            .TryCompleteReanalysisWithoutActionAsync(
                                reanalysisCaseId,
                                reanalysisRowVersion,
                                wasWhitelisted: true,
                                cancellationToken)
                            .ConfigureAwait(false);
                    if (!completed)
                    {
                        Publish(
                            new(
                                ModerationPipelineEventKind.BatchRejected,
                                "stale_case_completion_conflict",
                                "The stale moderation case changed before its whitelist result could be stored.",
                                message,
                                decision,
                                caseId: reanalysisCaseId));
                        continue;
                    }
                }

                Publish(
                    new(
                        ModerationPipelineEventKind.WhitelistSkipped,
                        "message_whitelisted_during_inference",
                        "The message or Twitch login was added to the manual whitelist while the batch was being analyzed.",
                        message,
                        decision,
                        caseId: batchMessage.ReanalysisCaseId,
                        localMatchCodes: localMatchCodes,
                        aiLatencyMilliseconds: aiLatencyMilliseconds));
                continue;
            }

            var normalizedDecision = decision with
            {
                Reason = ModerationReasonNormalizer.Normalize(
                    decision.Reason,
                    decision.RuleCode,
                    decision.PolicySet),
            };
            var addResult = await _caseRepository
                .AddIfNoActiveDuplicateAsync(
                    message,
                    normalizedDecision,
                    batch.Context.EffectivePolicy.RevisionFingerprint,
                    new ModerationCaseCreationMetadata(
                        batch.BatchId,
                        localMatchCodes,
                        aiLatencyMilliseconds)
                    {
                        ReanalysisCaseId = batchMessage.ReanalysisCaseId,
                        ReanalysisCaseRowVersion =
                            batchMessage.ReanalysisCaseRowVersion,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            if (!addResult.IsCreated)
            {
                Publish(
                    new(
                        ModerationPipelineEventKind.DuplicateProposalSuppressed,
                        "active_duplicate_proposal",
                        "An identical message from this Twitch user already has an active moderation proposal.",
                        message,
                        normalizedDecision,
                        addResult.CaseId,
                        localMatchCodes: localMatchCodes,
                        aiLatencyMilliseconds: aiLatencyMilliseconds));
                continue;
            }

            var caseId = addResult.CaseId;
            var persistedCase = await _caseRepository
                .GetAsync(caseId, cancellationToken)
                .ConfigureAwait(false);
            Publish(
                new(
                    ModerationPipelineEventKind.CaseCreated,
                    "moderation_case_created",
                    "A validated moderation proposal awaits human review.",
                    message,
                    normalizedDecision,
                    caseId,
                    caseRowVersion: persistedCase?.RowVersion ?? 1,
                    localMatchCodes: localMatchCodes,
                    aiLatencyMilliseconds: aiLatencyMilliseconds,
                    activeRules: batch.Context.EffectivePolicy.ActiveRules));
        }
    }

    private async Task<IReadOnlyDictionary<string, ChatReplyTemplateRecord>>
        CreateReplyTemplateSnapshotAsync(
            ModerationBatch batch,
            CancellationToken cancellationToken)
    {
        if (_chatReplyStore is null)
        {
            return new Dictionary<string, ChatReplyTemplateRecord>(
                StringComparer.Ordinal);
        }

        var templates = await _chatReplyStore
            .LoadActiveTemplatesAsync(cancellationToken)
            .ConfigureAwait(false);
        if (templates.Count == 0)
        {
            return new Dictionary<string, ChatReplyTemplateRecord>(
                StringComparer.Ordinal);
        }

        var messageTokens = batch.Messages
            .SelectMany(message => Tokenize(message.Message.Text.LowerText))
            .ToHashSet(StringComparer.Ordinal);
        var candidates = templates
            .Select(
                template => new
                {
                    Template = template,
                    Score = Tokenize(template.NormalizedExample)
                        .Count(messageTokens.Contains),
                })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Template.TemplateId)
            .ToArray();

        var result = new Dictionary<string, ChatReplyTemplateRecord>(
            StringComparer.Ordinal);
        var characterCount = 0;
        foreach (var candidate in candidates)
        {
            if (result.Count >= MaximumReplyTemplatesPerBatch)
            {
                break;
            }

            var candidateCharacters = candidate.Template.NormalizedExample.Length;
            if (characterCount + candidateCharacters
                > MaximumReplyTemplateCharacters)
            {
                continue;
            }

            var temporaryId = $"r{result.Count + 1}";
            result.Add(temporaryId, candidate.Template);
            characterCount += candidateCharacters;
        }

        return result;
    }

    private static List<string> Tokenize(string value)
    {
        var tokens = new List<string>();
        var token = new System.Text.StringBuilder();
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (token.Length > 0)
            {
                tokens.Add(token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            tokens.Add(token.ToString());
        }

        return tokens;
    }

    private async Task RequeueForPolicyAsync(
        ModerationBatch batch,
        CoreEffectivePolicySnapshot latestPolicy,
        CancellationToken cancellationToken)
    {
        await RequeueBatchAsync(
                batch,
                latestPolicy,
                refreshLocalMatches: true,
                supersededReason: "policy_revision_changed",
                eventKind: ModerationPipelineEventKind.StalePolicyRequeued,
                statusCode: "stale_policy_requeued",
                eventMessage:
                    "A batch used an old policy revision and was queued for fresh analysis.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private Task RequeueForLearningAsync(
        ModerationBatch batch,
        CoreEffectivePolicySnapshot latestPolicy,
        CancellationToken cancellationToken)
    {
        return RequeueBatchAsync(
            batch,
            latestPolicy,
            refreshLocalMatches: false,
            supersededReason: "learning_context_changed",
            eventKind: ModerationPipelineEventKind.StaleLearningRequeued,
            statusCode: "stale_learning_requeued",
            eventMessage:
                "The learning context changed during inference; the batch was queued for fresh analysis.",
            cancellationToken);
    }

    private async Task RequeueBatchAsync(
        ModerationBatch batch,
        CoreEffectivePolicySnapshot latestPolicy,
        bool refreshLocalMatches,
        string supersededReason,
        ModerationPipelineEventKind eventKind,
        string statusCode,
        string eventMessage,
        CancellationToken cancellationToken)
    {
        // The source batch has already been dequeued. Close its activity before
        // publishing the freshly sealed replacement so the persistent footer
        // cannot retain a stale "Wartet" entry.
        PublishSupersededAiRun(batch, supersededReason);

        IReadOnlyList<PhraseRule> rules = [];
        if (refreshLocalMatches)
        {
            rules = await GetRulesAsync(
                    batch.Context.Channel.ChannelId,
                    latestPolicy,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var queued in batch.Messages)
        {
            var refreshed = refreshLocalMatches
                ? queued with
                {
                    LocalMatches = _blacklistMatcher.Match(
                        queued.Message.Text,
                        latestPolicy,
                        rules),
                }
                : queued;
            var result = _batcher.Enqueue(
                refreshed,
                new ModerationBatchContext(batch.Context.Channel, latestPolicy),
                _timeProvider.GetUtcNow());
            foreach (var dropped in result.DroppedMessages)
            {
                Publish(
                    new(
                        ModerationPipelineEventKind.DroppedOverload,
                        "queue_overflow",
                        "An older queued message was not analyzed because the 500-message limit was reached.",
                        dropped.Message));
            }

            if (result.Disposition == BatchIngressDisposition.DuplicateSuppressed)
            {
                PublishQueueDuplicateSkipped(refreshed.Message);
            }

            PublishBatchIngressUpdates(result);
        }

        // Re-analysis must not restart the original ten-second collection
        // window. The replacement runs as soon as this stale attempt returns.
        if (_batcher.Flush(_timeProvider.GetUtcNow()) is { } refreshedBatch)
        {
            SignalSealedBatch(refreshedBatch);
        }

        Publish(new(eventKind, statusCode, eventMessage));
    }

    private async ValueTask<CoreEffectivePolicySnapshot> ResolvePolicyAsync(
        ChannelContextSnapshot context,
        CancellationToken cancellationToken)
    {
        var stored = await _policyStore
            .GetEffectivePolicyAsync(
                context.ChannelId,
                context.HasKnownCategory ? context.CategoryId : null,
                cancellationToken)
            .ConfigureAwait(false);
        var policyContext =
            context.HasKnownCategory &&
            string.Equals(
                stored.CategoryId,
                context.CategoryId,
                StringComparison.Ordinal)
                ? context
                : context with { IsVerified = false };
        var resolved = _policyResolver.Resolve(
            policyContext,
            Math.Max(1, stored.GeneralRevision),
            Math.Max(1, stored.CategoryRevision));
        return resolved with
        {
            ActiveRules = stored.Rules
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
                .Where(rule => resolved.Contains(rule.PolicySet))
                .OrderBy(rule => rule.Priority)
                .ThenBy(rule => rule.RuleCode, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private async ValueTask<IReadOnlyList<PhraseRule>> GetRulesAsync(
        string channelId,
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken)
    {
        var key = $"{channelId}|{policy.RevisionFingerprint}";
        if (_ruleCache.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var loaded = await _phraseRuleSource
            .GetRulesAsync(channelId, policy, cancellationToken)
            .ConfigureAwait(false);
        return _ruleCache.GetOrAdd(key, loaded);
    }

    private void ValidateRuntimeConfiguration()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.TwitchApi.BroadcasterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.TwitchApi.ReaderUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.TwitchApi.ModeratorUserId);
    }

    private bool IsPrivilegedSender(ChatMessageIdentityV1 identity)
    {
        return TwitchChatBadgeRoles.HasAutomaticModerationBypass(
            identity,
            _options.TwitchApi);
    }

    private static bool HasSameBatchingCategory(
        ChannelContextSnapshot left,
        ChannelContextSnapshot right)
    {
        return string.Equals(
                left.ChannelId,
                right.ChannelId,
                StringComparison.Ordinal)
            && string.Equals(
                left.CategoryId,
                right.CategoryId,
                StringComparison.Ordinal)
            && left.IsVerified == right.IsVerified;
    }

    private ChannelContextSnapshot? GetCurrentContext()
    {
        lock (_contextGate)
        {
            return _currentContext;
        }
    }

    private void SetCurrentContext(ChannelContextSnapshot context)
    {
        lock (_contextGate)
        {
            _currentContext = context;
        }
    }

    private void SignalSealedBatch(ModerationBatch batch)
    {
        PublishQueuedAiRun(batch);
        _inferenceSignal.Release();
    }

    private void SignalSealedBatches(IReadOnlyList<ModerationBatch> batches)
    {
        foreach (var batch in batches)
        {
            PublishQueuedAiRun(batch);
        }

        _inferenceSignal.Release();
    }

    private void PublishBatchIngressUpdates(BatchIngressResult ingress)
    {
        foreach (var batchId in ingress.RemovedQueuedBatchIds)
        {
            PublishSupersededAiRun(
                batchId,
                1,
                "queue_overflow");
        }

        foreach (var batch in ingress.UpdatedQueuedBatches)
        {
            PublishQueuedAiRun(batch);
        }

        if (ingress.NewlySealedBatches.Count > 0)
        {
            SignalSealedBatches(ingress.NewlySealedBatches);
        }
    }

    private void PublishQueueDuplicateSkipped(ChatMessageSnapshot message)
    {
        Publish(
            new(
                ModerationPipelineEventKind.QueueDuplicateSkipped,
                "duplicate_lower_text_queued",
                "An identical lowercase message is already waiting for local moderation.",
                message));
    }

    private void PublishQueuedAiRun(ModerationBatch batch)
    {
        _aiRunActivityMonitor?.Publish(
            new LocalAiRunActivity(
                batch.BatchId.ToString("D"),
                LocalAiRunState.Queued,
                batch.Messages.Count,
                _options.LmStudio.Model,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                ElapsedMilliseconds: null,
                batch.SealedAtUtc));
    }

    private void PublishRunningAiRun(ModerationBatch batch)
    {
        PublishAiRunState(batch, LocalAiRunState.Running);
    }

    private void PublishFailedAiRun(
        ModerationBatch batch,
        string errorCode)
    {
        PublishAiRunState(batch, LocalAiRunState.Failed, errorCode);
    }

    private void PublishAiRunState(
        ModerationBatch batch,
        LocalAiRunState state,
        string? errorCode = null)
    {
        _aiRunActivityMonitor?.Publish(
            new LocalAiRunActivity(
                batch.BatchId.ToString("D"),
                state,
                batch.Messages.Count,
                _options.LmStudio.Model,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                ElapsedMilliseconds: null,
                _timeProvider.GetUtcNow(),
                errorCode));
    }

    private void PublishSupersededAiRun(
        ModerationBatch batch,
        string reasonCode)
    {
        PublishSupersededAiRun(
            batch.BatchId,
            batch.Messages.Count,
            reasonCode);
    }

    private void PublishSupersededAiRun(
        Guid batchId,
        int messageCount,
        string reasonCode)
    {
        _aiRunActivityMonitor?.Publish(
            new LocalAiRunActivity(
                batchId.ToString("D"),
                LocalAiRunState.Superseded,
                messageCount,
                _options.LmStudio.Model,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                ElapsedMilliseconds: null,
                _timeProvider.GetUtcNow(),
                reasonCode));
    }

    private void PublishAvailability(TwitchChatEvent chatEvent, string statusCode)
    {
        Publish(
            new(
                ModerationPipelineEventKind.AvailabilityChanged,
                statusCode,
                "The local EventSub availability view changed.",
                targetMessageId: chatEvent.TargetMessageId,
                targetUserId: chatEvent.TargetUserId,
                occurredAtUtc: chatEvent.ReceivedAtUtc));
    }

    private void PublishFault(string statusCode, string message, Exception exception)
    {
        Publish(
            new(
                ModerationPipelineEventKind.Faulted,
                statusCode,
                message,
                exception: exception));
    }

    private void Publish(ModerationPipelineEventArgs args)
    {
        EventReceived?.Invoke(this, args);
    }
}
