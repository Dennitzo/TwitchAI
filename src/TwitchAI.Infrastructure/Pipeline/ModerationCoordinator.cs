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
    ModelLoading,
    ModelReady,
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
        IReadOnlyList<ModerationRuleDefinition>? activeRules = null,
        string? channelId = null)
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
        ChannelId = channelId;
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

    public string? ChannelId { get; }
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
    private readonly object _inferenceSchedulingGate = new();
    private readonly SemaphoreSlim _lifecycleOperationGate = new(1, 1);
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
    private readonly IModerationPromptCompiler _promptCompiler;
    private readonly IRetrievalExampleStore _retrievalExampleStore;
    private readonly ILearningContextRevision _learningContextRevision;
    private readonly IModerationCaseRepository _caseRepository;
    private readonly IMessageAvailabilityRegistry _availabilityRegistry;
    private readonly IMessageAvailabilityStore? _availabilityStore;
    private readonly ILocalAiRunActivityMonitor? _aiRunActivityMonitor;
    private readonly ITwitchConnectionReadiness? _connectionReadiness;
    private readonly IChatReplyStore? _chatReplyStore;
    private readonly IAiValidationAttemptStore? _validationAttemptStore;
    private readonly InfrastructureOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, IReadOnlyList<PhraseRule>> _ruleCache =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _inferenceSignal = new(0);
    private readonly HashSet<string> _activeInferenceChannels =
        new(StringComparer.Ordinal);
    private string? _lastReleasedInferenceChannel;
    private CancellationTokenSource? _runSource;
    private Task[] _runTasks = [];
    private readonly Dictionary<string, ChannelContextSnapshot> _currentContexts =
        new(StringComparer.Ordinal);
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
        ILearningContextRevision? learningContextRevision = null,
        IAiValidationAttemptStore? validationAttemptStore = null,
        IModerationPromptCompiler? promptCompiler = null)
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
        _promptCompiler = promptCompiler ?? new ModerationPromptCompiler();
        _retrievalExampleStore = retrievalExampleStore;
        _learningContextRevision = learningContextRevision
            ?? LearningContextRevision.ProcessWide;
        _caseRepository = caseRepository;
        _availabilityRegistry = availabilityRegistry;
        _availabilityStore = availabilityStore;
        _aiRunActivityMonitor = aiRunActivityMonitor;
        _connectionReadiness = connectionReadiness;
        _chatReplyStore = chatReplyStore;
        _validationAttemptStore = validationAttemptStore;
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
        await _lifecycleOperationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleOperationGate.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connectionReadiness is not null
            && !_connectionReadiness.IsSessionReady)
        {
            throw new InvalidOperationException(
                "Die Analyse kann erst gestartet werden, wenn die Twitch-Anmeldung "
                + "und die konfigurierten Kanäle erfolgreich verbunden sind.");
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
            var broadcasterIds = _options.TwitchApi.GetBroadcasterIds();
            DiscardQueuedBatches("analysis_restart");
            ClearCurrentContexts();
            lock (_inferenceSchedulingGate)
            {
                _activeInferenceChannels.Clear();
                _lastReleasedInferenceChannel = null;
            }
            var initialChannels = new List<(
                ChannelContextSnapshot Context,
                CoreEffectivePolicySnapshot Policy)>(broadcasterIds.Count);
            foreach (var broadcasterId in broadcasterIds)
            {
                await _policyStore
                    .EnsureBroadcasterSeededAsync(broadcasterId, source.Token)
                    .ConfigureAwait(false);
                var context = await _channelContextService
                    .GetCurrentAsync(broadcasterId, source.Token)
                    .ConfigureAwait(false);
                SetCurrentContext(context);
                var policy = await ResolvePolicyAsync(context, source.Token)
                    .ConfigureAwait(false);
                initialChannels.Add((context, policy));
            }

            if (initialChannels.Count == 0)
            {
                throw new InvalidOperationException(
                    "Mindestens ein Twitch-Kanal mit Regelkontext muss für die Analyse konfiguriert sein.");
            }

            if (_connectionReadiness is not null
                && !_connectionReadiness.IsSessionReady)
            {
                throw new InvalidOperationException(
                    "Die Twitch-Verbindung wurde während des Analysestarts getrennt. "
                    + "Bitte Twitch erneut verbinden.");
            }

            Publish(new(
                ModerationPipelineEventKind.ModelLoading,
                "ai_model_loading",
                "Das ausgewählte lokale AI-Modell wird geladen."));
            try
            {
                await _localModerationClient.PrepareAsync(source.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Publish(new(
                    ModerationPipelineEventKind.Faulted,
                    "ai_model_load_failed",
                    exception.Message,
                    exception: exception));
                throw;
            }
            Publish(new(
                ModerationPipelineEventKind.ModelReady,
                "ai_model_ready",
                "Das ausgewählte lokale AI-Modell ist geladen."));

            var inferenceWorkerCount = Math.Min(
                LocalAiExecutionGate.MaximumConcurrency,
                broadcasterIds.Count);
            _runTasks =
            [
                ConsumeEventSubAsync(source),
                SealDeadlinesAsync(source),
                .. Enumerable.Range(0, inferenceWorkerCount)
                    .Select(_ => RunInferenceAsync(source)),
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

            // EventSub can observe a category change while startup is waiting
            // for readiness. Project that current context after Started resets
            // the UI's previous per-channel policy presentation.
            for (var index = 0; index < initialChannels.Count; index++)
            {
                var initial = initialChannels[index];
                var context = GetCurrentContext(initial.Context.ChannelId)
                    ?? initial.Context;
                if (context != initial.Context)
                {
                    initialChannels[index] = (
                        context,
                        await ResolvePolicyAsync(context, source.Token)
                            .ConfigureAwait(false));
                }
            }

            Publish(
                new(
                    ModerationPipelineEventKind.Started,
                    "analysis_started",
                    "Twitch moderation analysis started.",
                    channelContext: initialChannels[0].Context,
                    activePolicySets: initialChannels[0].Policy.ActivePolicySets
                        .Select(item => item.Code)
                        .ToArray()));
            foreach (var (context, policy) in initialChannels)
            {
                Publish(new(
                    ModerationPipelineEventKind.CategoryChanged,
                    "channel_policy_initialized",
                    "The current channel policy was applied after analysis startup.",
                    channelContext: context,
                    activePolicySets: policy.ActivePolicySets
                        .Select(item => item.Code)
                        .ToArray()));
            }
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

            DiscardQueuedBatches("analysis_start_failed");

            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleOperationGate.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleOperationGate.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
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
            // Once shutdown has started, observe every old worker before the
            // lifecycle gate is released. Otherwise a cancelled caller could
            // start a new run while old workers still touch the shared batcher.
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            source.IsCancellationRequested)
        {
            // A paused coordinator cancels EventSub and an in-flight local inference.
        }
        finally
        {
            DiscardQueuedBatches("analysis_stopped");
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
        _lifecycleOperationGate.Dispose();
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
            case TwitchChatEventKind.AutoModMessageHeld when chatEvent.Message is not null:
                await ProcessMessageAsync(
                        chatEvent.Message,
                        chatEvent.TwitchEmoteCount,
                        chatEvent.IsTwitchEmoteOnly,
                        chatEvent.IsAutoModHeld,
                        chatEvent.AutoModReason,
                        chatEvent.AutoModCategory,
                        chatEvent.AutoModLevel,
                        cancellationToken)
                    .ConfigureAwait(false);
                break;
            case TwitchChatEventKind.ChannelUpdated when chatEvent.ChannelContext is not null:
                {
                    var previousContext = GetCurrentContext(chatEvent.ChannelId);
                    var batchingContextChanged = previousContext is null
                        || !HasSameBatchingCategory(
                            previousContext,
                            chatEvent.ChannelContext);
                    if (previousContext is not null && batchingContextChanged)
                    {
                        var flushed = _batcher.FlushChannel(
                            chatEvent.ChannelId,
                            _timeProvider.GetUtcNow());
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
                    var currentContext = GetCurrentContext(chatEvent.ChannelId);
                    if (currentContext is not null)
                    {
                        var flushed = _batcher.FlushChannel(
                            chatEvent.ChannelId,
                            _timeProvider.GetUtcNow());
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
        int twitchEmoteCount,
        bool isTwitchEmoteOnly,
        bool isAutoModHeld,
        string? autoModReason,
        string? autoModCategory,
        int? autoModLevel,
        CancellationToken cancellationToken)
    {
        var context = GetCurrentContext(identity.ChannelId);
        if (context is null)
        {
            context = await _channelContextService
                .GetCurrentAsync(identity.ChannelId, cancellationToken)
                .ConfigureAwait(false);
            SetCurrentContext(context);
        }
        var processed = _normalizer.Process(identity.OriginalText);
        var snapshot = new ChatMessageSnapshot(
            identity,
            processed,
            context,
            Interlocked.Increment(ref _ingressSequence))
        {
            TwitchEmoteCount = twitchEmoteCount,
            IsTwitchEmoteOnly = isTwitchEmoteOnly,
            IsAutoModHeld = isAutoModHeld,
            AutoModReason = autoModReason,
            AutoModCategory = autoModCategory,
            AutoModLevel = autoModLevel,
        };
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
                    "The message, Twitch login, or standalone Twitch clip matched the whitelist rules.",
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
                isAutoModHeld ? "automod_held_queued_for_ai" : "queued_for_ai",
                isAutoModHeld
                    ? "An AutoMod-held message was queued for independent content classification."
                    : "The lowercase message was queued for local moderation.",
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
                var now = _timeProvider.GetUtcNow();
                while (_batcher.SealIfDue(now) is { } batch)
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
                if (!TryClaimInferenceBatch(out var batch) || batch is null)
                {
                    continue;
                }

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
                finally
                {
                    ReleaseInferenceChannel(batch.Context.Channel.ChannelId);
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

    private bool TryClaimInferenceBatch(out ModerationBatch? batch)
    {
        lock (_inferenceSchedulingGate)
        {
            if (_lastReleasedInferenceChannel is not null)
            {
                var temporarilyExcluded = new HashSet<string>(
                    _activeInferenceChannels,
                    StringComparer.Ordinal)
                {
                    _lastReleasedInferenceChannel,
                };
                if (_batcher.TryDequeue(temporarilyExcluded, out batch)
                    && batch is not null)
                {
                    _activeInferenceChannels.Add(
                        batch.Context.Channel.ChannelId);
                    return true;
                }
            }

            if (!_batcher.TryDequeue(_activeInferenceChannels, out batch)
                || batch is null)
            {
                return false;
            }

            _activeInferenceChannels.Add(batch.Context.Channel.ChannelId);
            return true;
        }
    }

    private void ReleaseInferenceChannel(string channelId)
    {
        lock (_inferenceSchedulingGate)
        {
            _activeInferenceChannels.Remove(channelId);
            _lastReleasedInferenceChannel = channelId;
        }

        if (_batcher.WaitingMessageCount > 0)
        {
            _inferenceSignal.Release();
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
        IReadOnlyList<RetrievalExample> retrievalExamples = [];
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
        var inferenceStarted = Stopwatch.GetTimestamp();
        ModerationValidationReport validation;
        // Live moderation has one canonical request/response path. The legacy
        // staged branch remains below only as temporary source compatibility;
        // persisted settings can no longer select it.
        if (_options.LocalAi.UseCompactModerationContractV6 ||
            !_options.LocalAi.UseCompactModerationContractV6)
        {
            var stagedValidation = await AnalyzeStagedBatchAsync(
                    batch,
                    analysisBatch,
                    retrievalExamples,
                    latestPolicy,
                    learningRevision,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stagedValidation is null)
            {
                return;
            }

            validation = stagedValidation;
        }
        else
        {
            var prompt = ModerationRequestFactory.Create(
                analysisBatch,
                retrievalExamples,
                _promptCompiler);
            var rawResponse = await _localModerationClient
                .ModerateAsync(
                    prompt.CompiledPrompt,
                    batch.Context.Channel.ChannelId,
                    cancellationToken)
                .ConfigureAwait(false);
            validation = ValidateRuntimeResponse(rawResponse, analysisBatch);
            await RecordValidationAttemptAsync(
                    correlationId: batch.BatchId.ToString("D"),
                    parentCorrelationId: null,
                    AiValidationAttemptKind.Initial,
                    prompt.CompiledPrompt,
                    rawResponse,
                    validation,
                    validation.IsValid
                        ? AiValidationOutcome.Valid
                        : AiValidationOutcome.Rejected,
                    validation.RepairMessageIds.Count,
                    cancellationToken)
                .ConfigureAwait(false);
            await RecordLocalFallbackAttemptAsync(
                    batch.BatchId.ToString("D"),
                    AiValidationAttemptKind.Initial,
                    prompt.CompiledPrompt,
                    validation.LocalFallbackMessageIds,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!validation.IsValid)
            {
                // Preserve decisions which passed identity and semantic validation.
                // The one bounded repair request contains only affected messages;
                // malformed root envelopes deliberately fall back to the full batch.
                var repairBatch = CreateRepairBatch(
                    analysisBatch,
                    validation.RepairMessageIds);
                var repairResult = await _repairClient
                    .RepairWithDiagnosticsAsync(
                        new ModerationRepairRequest(
                            prompt.CompiledPrompt,
                            validation.RepairMessageIds,
                            validation.Issues,
                            validation.RequiresFullRepair,
                            batch.Context.Channel.ChannelId),
                        cancellationToken)
                    .ConfigureAwait(false);
                var repairedResponse = repairResult.RawResponse;
                var repairedValidation = ValidateRuntimeResponse(
                    repairedResponse,
                    repairBatch);
                var diagnosticRepairPrompt = repairResult.Prompt
                    ?? _promptCompiler.Compile(
                        prompt.Envelope with
                        {
                            Messages = prompt.Envelope.Messages
                                .Where(message => validation.RepairMessageIds.Contains(
                                    message.MessageId,
                                    StringComparer.Ordinal))
                                .ToArray(),
                        });
                await RecordValidationAttemptAsync(
                        correlationId: $"{batch.BatchId:D}-repair",
                        parentCorrelationId: batch.BatchId.ToString("D"),
                        AiValidationAttemptKind.Repair,
                        diagnosticRepairPrompt,
                        repairedResponse,
                        repairedValidation,
                        repairedValidation.IsValid
                            ? AiValidationOutcome.Repaired
                            : AiValidationOutcome.Rejected,
                        validation.RepairMessageIds.Count,
                        cancellationToken)
                    .ConfigureAwait(false);
                await RecordLocalFallbackAttemptAsync(
                        batch.BatchId.ToString("D"),
                        AiValidationAttemptKind.Repair,
                        diagnosticRepairPrompt,
                        repairedValidation.LocalFallbackMessageIds,
                        cancellationToken)
                    .ConfigureAwait(false);
                validation = repairedValidation.IsValid
                    ? validation.RequiresFullRepair
                        ? repairedValidation
                        : MergeRepairResult(
                            analysisBatch,
                            validation,
                            repairedValidation)
                    : repairedValidation;
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

            validation = await VerifyActionableDecisionsAsync(
                    analysisBatch,
                    validation,
                    retrievalExamples,
                    cancellationToken)
                .ConfigureAwait(false);

            validation = ApplyHumanPriorityDecisions(
                analysisBatch,
                validation,
                retrievalExamples);
        }

        if (!validation.IsValid)
        {
            Publish(
                new(
                    ModerationPipelineEventKind.BatchRejected,
                    "invalid_ai_envelope",
                    string.Join(
                        "; ",
                        validation.Issues.Select(error => $"{error.Code}: {error.Message}"))));
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
        foreach (var envelopeDecision in validation.Envelope!.Decisions)
        {
            var decision = envelopeDecision;
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
            // decisions and can change while Lokale AI is evaluating a batch.
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

            var addResult = await _caseRepository
                .AddIfNoActiveDuplicateAsync(
                    message,
                    decision,
                    batch.Context.EffectivePolicy.RevisionFingerprint,
                    new ModerationCaseCreationMetadata(
                        batch.BatchId,
                        localMatchCodes,
                        aiLatencyMilliseconds)
                    {
                        ReanalysisCaseId = batchMessage.ReanalysisCaseId,
                        ReanalysisCaseRowVersion =
                            batchMessage.ReanalysisCaseRowVersion,
                        PhraseMatches = batchMessage.LocalMatches,
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
                        decision,
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
                    decision,
                    caseId,
                    caseRowVersion: persistedCase?.RowVersion ?? 1,
                    localMatchCodes: localMatchCodes,
                    aiLatencyMilliseconds: aiLatencyMilliseconds,
                    activeRules: analysisBatch.Context.EffectivePolicy.ActiveRules));
        }
    }

    private async Task<ModerationValidationReport?> AnalyzeStagedBatchAsync(
        ModerationBatch originalBatch,
        ModerationBatch analysisBatch,
        IReadOnlyList<RetrievalExample> retrievalExamples,
        CoreEffectivePolicySnapshot expectedPolicy,
        long learningRevision,
        CancellationToken cancellationToken)
    {
        return await AnalyzeSingleLiveRunAsync(
                originalBatch,
                analysisBatch,
                retrievalExamples,
                expectedPolicy,
                learningRevision,
                cancellationToken)
            .ConfigureAwait(false);

#pragma warning disable CS0162
        var fullEnvelope = ModerationRequestFactory.CreateEnvelope(
            analysisBatch,
            retrievalExamples);
        var humanPriorityById = analysisBatch.Messages.ToDictionary(
            message => message.TemporaryMessageId,
            message => HumanModerationPriority.Resolve(
                message.Message.Text.LowerText,
                analysisBatch.Context.EffectivePolicy,
                message.LocalMatches,
                retrievalExamples),
            StringComparer.Ordinal);
        var candidates = new List<ModerationConfirmationCandidate>();
        var classificationIds = new HashSet<string>(StringComparer.Ordinal);
        var replyOnlyIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in analysisBatch.Messages)
        {
            var priority = humanPriorityById[message.TemporaryMessageId];
            if (priority is null)
            {
                classificationIds.Add(message.TemporaryMessageId);
                continue;
            }

            if (priority.IsAllow)
            {
                if (analysisBatch.ReplyTemplates.Count > 0)
                {
                    classificationIds.Add(message.TemporaryMessageId);
                    replyOnlyIds.Add(message.TemporaryMessageId);
                }

                continue;
            }

            var rule = priority.Rule
                ?? throw new InvalidOperationException(
                    "An actionable human-priority decision requires an active rule.");
            candidates.Add(
                new ModerationConfirmationCandidate(
                    message.TemporaryMessageId,
                    priority.Action,
                    priority.TimeoutSeconds,
                    rule.PolicySet,
                    rule.RuleCode,
                    priority.EvidenceKind,
                    SensitiveDataType: null,
                    priority.TriggerPhrases,
                    Confidence: 1d,
                    string.Equals(
                        priority.Source,
                        "exact_moderate_learning",
                        StringComparison.Ordinal)
                        ? ModerationCandidateSource.ExactModerateLearning
                        : ModerationCandidateSource.ManualBlacklistPhrase));
        }

        var classificationDecisions = Array.Empty<ModerationClassificationDecision>();
        if (classificationIds.Count > 0)
        {
            var classificationEnvelope = fullEnvelope with
            {
                EnvelopeVersion =
                    ModerationPromptCompiler.ClassificationRequestEnvelopeVersion,
                Messages = fullEnvelope.Messages
                    .Where(message => classificationIds.Contains(message.MessageId))
                    .ToArray(),
            };
            var classificationRequest = new ModerationClassificationRequest(
                classificationEnvelope,
                classificationEnvelope.Messages
                    .Select(message => message.MessageId)
                    .Where(replyOnlyIds.Contains)
                    .ToArray());
            var classificationPrompt = _promptCompiler.CompileClassification(
                classificationRequest);
            var rawClassification = await _localModerationClient
                .ModerateAsync(
                    classificationPrompt,
                    originalBatch.Context.Channel.ChannelId,
                    cancellationToken)
                .ConfigureAwait(false);
            var classification = _envelopeValidator.ValidateClassification(
                rawClassification,
                classificationRequest);
            await RecordClassificationValidationAttemptAsync(
                    originalBatch.BatchId.ToString("D"),
                    parentCorrelationId: null,
                    AiValidationAttemptKind.Initial,
                    classificationPrompt,
                    rawClassification,
                    classification,
                    classification.IsValid
                        ? AiValidationOutcome.Valid
                        : AiValidationOutcome.Rejected,
                    classification.RepairMessageIds.Count,
                    cancellationToken)
                .ConfigureAwait(false);

            var usableClassifications = classification.ValidDecisions.ToList();
            if (!classification.IsValid && classification.RepairMessageIds.Count > 0)
            {
                var repairIds = classification.RepairMessageIds
                    .ToHashSet(StringComparer.Ordinal);
                var repairResult = await _repairClient
                    .RepairWithDiagnosticsAsync(
                        new ModerationRepairRequest(
                            classificationPrompt,
                            classification.RepairMessageIds,
                            classification.Issues,
                            classification.RequiresFullRepair,
                            originalBatch.Context.Channel.ChannelId),
                        cancellationToken)
                    .ConfigureAwait(false);
                var repairEnvelope = (repairResult.Prompt?.Envelope
                        ?? classificationEnvelope with
                        {
                            Messages = classificationEnvelope.Messages
                                .Where(message => repairIds.Contains(message.MessageId))
                                .ToArray(),
                        }) with
                {
                    EnvelopeVersion =
                        ModerationPromptCompiler.ClassificationRequestEnvelopeVersion,
                };
                var repairRequest = new ModerationClassificationRequest(
                    repairEnvelope,
                    repairEnvelope.Messages
                        .Select(message => message.MessageId)
                        .Where(replyOnlyIds.Contains)
                        .ToArray());
                var repairedClassification = _envelopeValidator
                    .ValidateClassification(
                        repairResult.RawResponse,
                        repairRequest);
                var diagnosticRepairPrompt = repairResult.Prompt
                    ?? _promptCompiler.CompileClassification(repairRequest);
                await RecordClassificationValidationAttemptAsync(
                        $"{originalBatch.BatchId:D}-repair",
                        originalBatch.BatchId.ToString("D"),
                        AiValidationAttemptKind.Repair,
                        diagnosticRepairPrompt,
                        repairResult.RawResponse,
                        repairedClassification,
                        repairedClassification.IsValid
                            ? AiValidationOutcome.Repaired
                            : AiValidationOutcome.Rejected,
                        classification.RepairMessageIds.Count,
                        cancellationToken)
                    .ConfigureAwait(false);
                usableClassifications = MergeClassificationDecisions(
                    classification.ValidDecisions,
                    repairedClassification.ValidDecisions,
                    repairIds,
                    classificationEnvelope.Messages);

                var unresolvedIds = repairIds
                    .Where(id => usableClassifications.All(decision =>
                        !string.Equals(
                            decision.TemporaryMessageId,
                            id,
                            StringComparison.Ordinal)))
                    .ToArray();
                if (unresolvedIds.Length > 0)
                {
                    Publish(
                        new(
                            ModerationPipelineEventKind.BatchRejected,
                            "classification_messages_rejected",
                            $"{unresolvedIds.Length} classification result(s) remained invalid after the bounded repair."));
                }
            }

            classificationDecisions = usableClassifications.ToArray();
        }

        foreach (var classification in classificationDecisions.Where(
                     decision => decision.Action != ModerationActionKind.None))
        {
            var rule = analysisBatch.Context.EffectivePolicy.FindRule(
                classification.RuleCode!);
            if (rule is null)
            {
                continue;
            }

            candidates.Add(
                new ModerationConfirmationCandidate(
                    classification.TemporaryMessageId,
                    classification.Action,
                    classification.TimeoutSeconds,
                    rule.PolicySet,
                    rule.RuleCode,
                    classification.EvidenceKind,
                    classification.SensitiveDataType,
                    TriggerPhrases: [],
                    classification.Confidence,
                    ModerationCandidateSource.SemanticClassification));
        }

        if (candidates.Count > 0)
        {
            if (_learningContextRevision.Current != learningRevision)
            {
                await RequeueForLearningAsync(
                        originalBatch,
                        expectedPolicy,
                        cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            var currentPolicy = await ResolvePolicyAsync(
                    originalBatch.Context.Channel,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    currentPolicy.RevisionFingerprint,
                    originalBatch.Context.EffectivePolicy.RevisionFingerprint,
                    StringComparison.Ordinal))
            {
                await RequeueForPolicyAsync(
                        originalBatch,
                        currentPolicy,
                        cancellationToken)
                    .ConfigureAwait(false);
                return null;
            }

            var candidateIds = candidates
                .Select(candidate => candidate.TemporaryMessageId)
                .ToHashSet(StringComparer.Ordinal);
            var confirmationEnvelope = fullEnvelope with
            {
                EnvelopeVersion =
                    ModerationPromptCompiler.ConfirmationRequestEnvelopeVersion,
                Messages = fullEnvelope.Messages
                    .Where(message => candidateIds.Contains(message.MessageId))
                    .ToArray(),
                ReplyTemplates = [],
            };
            var confirmationRequest = new ModerationConfirmationRequest(
                confirmationEnvelope,
                candidates);
            var confirmationPrompt = _promptCompiler.CompileConfirmation(
                confirmationRequest);
            var rawConfirmation = await _localModerationClient
                .ModerateAsync(
                    confirmationPrompt,
                    originalBatch.Context.Channel.ChannelId,
                    cancellationToken)
                .ConfigureAwait(false);
            var confirmation = _envelopeValidator.ValidateConfirmation(
                rawConfirmation,
                confirmationRequest);
            await RecordConfirmationValidationAttemptAsync(
                    $"{originalBatch.BatchId:D}-confirm",
                    originalBatch.BatchId.ToString("D"),
                    confirmationPrompt,
                    rawConfirmation,
                    confirmation,
                    confirmation.IsEnvelopeValid && confirmation.Issues.Count == 0
                        ? AiValidationOutcome.Valid
                        : AiValidationOutcome.Rejected,
                    confirmation.RejectedMessageIds.Count,
                    cancellationToken)
                .ConfigureAwait(false);
            if (confirmation.Issues.Count > 0)
            {
                Publish(
                    new(
                        ModerationPipelineEventKind.BatchRejected,
                        "confirmation_candidates_rejected",
                        string.Join(
                            "; ",
                            confirmation.Issues.Select(issue =>
                                $"{issue.Code}: {issue.Message}"))));
            }

            var confirmedById = confirmation.ConfirmedDecisions.ToDictionary(
                decision => decision.TemporaryMessageId,
                StringComparer.Ordinal);
            return BuildStagedFinalReport(
                analysisBatch,
                humanPriorityById,
                classificationDecisions,
                confirmedById);
        }

        return BuildStagedFinalReport(
            analysisBatch,
            humanPriorityById,
            classificationDecisions,
            new Dictionary<string, ValidatedModerationDecision>(StringComparer.Ordinal));
#pragma warning restore CS0162
    }

    private async Task<ModerationValidationReport?> AnalyzeSingleLiveRunAsync(
        ModerationBatch originalBatch,
        ModerationBatch analysisBatch,
        IReadOnlyList<RetrievalExample> initialExamples,
        CoreEffectivePolicySnapshot expectedPolicy,
        long learningRevision,
        CancellationToken cancellationToken)
    {
        _ = initialExamples;
        var enrichedMessages = new List<ModerationBatchMessage>(analysisBatch.Messages.Count);
        var examplesByText = await _retrievalExampleStore
            .GetRelevantPerMessageAsync(
                originalBatch.Context.Channel.ChannelId,
                originalBatch.Context.Channel.CategoryId,
                analysisBatch.Context.EffectivePolicy,
                analysisBatch.Messages
                    .Select(message => message.Message.Text.LowerText)
                    .ToArray(),
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var message in analysisBatch.Messages)
        {
            var examples = examplesByText.GetValueOrDefault(
                message.Message.Text.LowerText) ?? [];
            var allowMatches = examples
                .Where(example => !example.IsPositive)
                .Select(example => AllowLearningPhraseMatcher.TryMatch(
                        message.Message.Text.LowerText,
                        example,
                        out var match)
                    ? match
                    : null)
                .Where(match => match is not null)
                .Cast<EffectiveAllowMatch>()
                .OrderByDescending(match => match.Score)
                .ThenByDescending(match => match.MatchedPhrase.Length)
                .Take(4)
                .ToArray();
            var phraseMatches = message.LocalMatches
                .Select(match => new EffectivePhraseMatch(
                    match.PhraseId,
                    match.RuleCode,
                    match.PolicySet,
                    match.MatchedPhrase,
                    match.Score,
                    match.MatchKind))
                .ToArray();
            enrichedMessages.Add(message with
            {
                EffectiveAllowMatches = allowMatches,
                EffectivePhraseMatches = phraseMatches,
                EffectiveLearningMatches = [],
                DecisionConstraint = ModerationDecisionConstraint.Free,
                LearningConsensus = null,
            });
        }

        var effectiveBatch = analysisBatch with
        {
            Messages = enrichedMessages,
        };
        var prompt = ModerationRequestFactory.Create(
            effectiveBatch,
            [],
            _promptCompiler);
        var rawResponse = await _localModerationClient
            .ModerateAsync(
                prompt.CompiledPrompt,
                originalBatch.Context.Channel.ChannelId,
                cancellationToken)
            .ConfigureAwait(false);
        var validation = _envelopeValidator.ValidateLive(
            rawResponse,
            effectiveBatch);
        await RecordValidationAttemptAsync(
                originalBatch.BatchId.ToString("D"),
                parentCorrelationId: null,
                AiValidationAttemptKind.Initial,
                prompt.CompiledPrompt,
                rawResponse,
                validation,
                validation.Issues.Count == 0
                    ? AiValidationOutcome.Valid
                    : AiValidationOutcome.Rejected,
                validation.Issues.Count,
                cancellationToken)
            .ConfigureAwait(false);

        var canRunRepair = validation.RepairMessageIds.Count > 0
            && (!validation.RequiresFullRepair
                || validation.RepairMessageIds.Count == 1);
        if (!validation.IsValid && canRunRepair)
        {
            var repairBatch = CreateRepairBatch(
                effectiveBatch,
                validation.RepairMessageIds);
            var repairResult = await _repairClient
                .RepairWithDiagnosticsAsync(
                    new ModerationRepairRequest(
                        prompt.CompiledPrompt,
                        validation.RepairMessageIds,
                        validation.Issues,
                        validation.RequiresFullRepair,
                        originalBatch.Context.Channel.ChannelId),
                    cancellationToken)
                .ConfigureAwait(false);
            var repairedValidation = _envelopeValidator.ValidateLive(
                repairResult.RawResponse,
                repairBatch);
            var repairPrompt = repairResult.Prompt
                ?? _promptCompiler.Compile(
                    prompt.Envelope with
                    {
                        Messages = prompt.Envelope.Messages
                            .Where(message => validation.RepairMessageIds.Contains(
                                message.MessageId,
                                StringComparer.Ordinal))
                            .ToArray(),
                    });
            await RecordValidationAttemptAsync(
                    $"{originalBatch.BatchId:D}-repair",
                    originalBatch.BatchId.ToString("D"),
                    AiValidationAttemptKind.Repair,
                    repairPrompt,
                    repairResult.RawResponse,
                    repairedValidation,
                    repairedValidation.IsValid
                        ? AiValidationOutcome.Repaired
                        : AiValidationOutcome.Rejected,
                    validation.RepairMessageIds.Count,
                    cancellationToken)
                .ConfigureAwait(false);
            validation = repairedValidation.IsValid
                ? validation.RequiresFullRepair
                    ? repairedValidation
                    : MergeRepairResult(
                        effectiveBatch,
                        validation,
                        repairedValidation)
                : validation;
        }
        if (_learningContextRevision.Current != learningRevision)
        {
            await RequeueForLearningAsync(
                    originalBatch,
                    expectedPolicy,
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        var currentPolicy = await ResolvePolicyAsync(
                originalBatch.Context.Channel,
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(
                currentPolicy.RevisionFingerprint,
                originalBatch.Context.EffectivePolicy.RevisionFingerprint,
                StringComparison.Ordinal))
        {
            await RequeueForPolicyAsync(
                    originalBatch,
                    currentPolicy,
                    cancellationToken)
                .ConfigureAwait(false);
            return null;
        }

        var decisionsById = validation.ValidDecisions
            .ToDictionary(item => item.TemporaryMessageId, StringComparer.Ordinal);
        var completeDecisions = analysisBatch.Messages
            .Select(message => decisionsById.TryGetValue(
                    message.TemporaryMessageId,
                    out var decision)
                ? decision
                : NoActionDecision(message.TemporaryMessageId))
            .ToArray();
        if (validation.Issues.Count > 0)
        {
            Publish(
                new(
                    ModerationPipelineEventKind.BatchRejected,
                    "invalid_ai_messages",
                    $"{validation.Issues.Count} AI decision(s) were discarded individually."));
        }

        return validation with
        {
            Envelope = new ValidatedModerationEnvelope(
                originalBatch.BatchId,
                completeDecisions),
            ValidDecisions = completeDecisions,
            Issues = [],
            RepairMessageIds = [],
            RequiresFullRepair = false,
        };
    }

    private static ValidatedModerationDecision NoActionDecision(string messageId) =>
        new(messageId, ModerationActionKind.None, null, null, string.Empty, [], 0d);

    private static List<ModerationClassificationDecision> MergeClassificationDecisions(
        IReadOnlyList<ModerationClassificationDecision> initial,
        IReadOnlyList<ModerationClassificationDecision> repaired,
        HashSet<string> repairIds,
        IReadOnlyList<ModerationRequestMessageV3> sourceMessages)
    {
        var byId = initial
            .Where(decision => !repairIds.Contains(decision.TemporaryMessageId))
            .ToDictionary(
                decision => decision.TemporaryMessageId,
                StringComparer.Ordinal);
        foreach (var decision in repaired)
        {
            byId[decision.TemporaryMessageId] = decision;
        }

        return sourceMessages
            .Where(message => byId.ContainsKey(message.MessageId))
            .Select(message => byId[message.MessageId])
            .ToList();
    }

    private static ModerationValidationReport BuildStagedFinalReport(
        ModerationBatch batch,
        Dictionary<string, HumanPriorityDecision?> humanPriorityById,
        IReadOnlyList<ModerationClassificationDecision> classifications,
        Dictionary<string, ValidatedModerationDecision> confirmedById)
    {
        var classificationById = classifications.ToDictionary(
            decision => decision.TemporaryMessageId,
            StringComparer.Ordinal);
        var decisions = batch.Messages.Select(message =>
        {
            var messageId = message.TemporaryMessageId;
            if (confirmedById.TryGetValue(messageId, out var confirmed))
            {
                return confirmed;
            }

            if (classificationById.TryGetValue(messageId, out var classification)
                && classification.Action == ModerationActionKind.None)
            {
                return new ValidatedModerationDecision(
                    messageId,
                    ModerationActionKind.None,
                    null,
                    null,
                    string.Empty,
                    [],
                    classification.Confidence,
                    ReplyTemplateId: classification.ReplyTemplateId,
                    ReplyConfidence: classification.ReplyConfidence);
            }

            var confidence = humanPriorityById.TryGetValue(messageId, out var priority)
                && priority?.IsAllow == true
                    ? 1d
                    : classificationById.TryGetValue(messageId, out classification)
                        ? classification.Confidence
                        : 0d;
            return new ValidatedModerationDecision(
                messageId,
                ModerationActionKind.None,
                null,
                null,
                string.Empty,
                [],
                confidence);
        }).ToArray();
        return new ModerationValidationReport(
            new ValidatedModerationEnvelope(batch.BatchId, decisions),
            decisions,
            [],
            [],
            RequiresFullRepair: false);
    }

    private async Task<ModerationValidationReport> VerifyActionableDecisionsAsync(
        ModerationBatch batch,
        ModerationValidationReport initial,
        IReadOnlyList<RetrievalExample> retrievalExamples,
        CancellationToken cancellationToken)
    {
        if (!initial.IsValid || initial.Envelope is null)
        {
            return initial;
        }

        var actionable = initial.Envelope!.Decisions
            .Where(decision => decision.Action != ModerationActionKind.None)
            .ToArray();
        if (actionable.Length == 0)
        {
            return initial;
        }

        var ids = actionable.Select(item => item.TemporaryMessageId)
            .ToHashSet(StringComparer.Ordinal);
        var ruleCodes = actionable.Select(item => item.RuleCode!)
            .ToHashSet(StringComparer.Ordinal);
        var verifyPolicy = batch.Context.EffectivePolicy with
        {
            ActiveRules = batch.Context.EffectivePolicy.ActiveRules
                .Where(rule => ruleCodes.Contains(rule.RuleCode))
                .ToArray(),
        };
        var verifyBatch = batch with
        {
            Context = new ModerationBatchContext(batch.Context.Channel, verifyPolicy),
            Messages = batch.Messages
                .Where(message => ids.Contains(message.TemporaryMessageId))
                .ToArray(),
            ReplyTemplates = [],
        };
        var allowBoundary = retrievalExamples
            .Where(example => !example.IsPositive)
            .Take(1)
            .ToArray();
        var verifyPrompt = ModerationRequestFactory.Create(
            verifyBatch,
            allowBoundary,
            _promptCompiler);
        var verifyResponse = await _localModerationClient
            .ModerateAsync(
                verifyPrompt.CompiledPrompt,
                batch.Context.Channel.ChannelId,
                cancellationToken)
            .ConfigureAwait(false);
        var verified = ValidateRuntimeResponse(verifyResponse, verifyBatch);
        var verifiedById = verified.IsValid
            ? verified.Envelope!.Decisions.ToDictionary(
                item => item.TemporaryMessageId,
                StringComparer.Ordinal)
            : new Dictionary<string, ValidatedModerationDecision>(StringComparer.Ordinal);
        var decisions = initial.Envelope.Decisions
            .Select(decision => decision.Action == ModerationActionKind.None
                ? decision
                : verifiedById.TryGetValue(decision.TemporaryMessageId, out var confirmation)
                    && confirmation.Action != ModerationActionKind.None
                    && string.Equals(
                        confirmation.RuleCode,
                        decision.RuleCode,
                        StringComparison.Ordinal)
                        ? decision with
                        {
                            Reason = confirmation.Reason,
                        }
                        : new ValidatedModerationDecision(
                            decision.TemporaryMessageId,
                            ModerationActionKind.None,
                            null,
                            null,
                            string.Empty,
                            [],
                            confirmation?.Confidence ?? decision.Confidence))
            .ToArray();
        var envelope = new ValidatedModerationEnvelope(batch.BatchId, decisions);
        return initial with
        {
            Envelope = envelope,
            ValidDecisions = decisions,
        };
    }

    internal static ModerationValidationReport ApplyHumanPriorityDecisions(
        ModerationBatch batch,
        ModerationValidationReport validation,
        IReadOnlyList<RetrievalExample> retrievalExamples)
    {
        if (!validation.IsValid)
        {
            return validation;
        }

        var currentById = validation.Envelope!.Decisions.ToDictionary(
            decision => decision.TemporaryMessageId,
            StringComparer.Ordinal);
        var decisions = batch.Messages.Select(message =>
        {
            var current = currentById[message.TemporaryMessageId];
            var priority = HumanModerationPriority.Resolve(
                message.Message.Text.LowerText,
                batch.Context.EffectivePolicy,
                message.LocalMatches,
                retrievalExamples);
            if (priority is null)
            {
                return current;
            }

            if (priority.IsAllow)
            {
                return new ValidatedModerationDecision(
                    current.TemporaryMessageId,
                    ModerationActionKind.None,
                    null,
                    null,
                    string.Empty,
                    [],
                    1d);
            }

            var priorityRule = priority.Rule
                ?? throw new InvalidOperationException(
                    "A moderate human-priority decision requires an active rule.");

            var hasCompatibleAiEvidence =
                current.Action != ModerationActionKind.None
                && StringComparer.Ordinal.Equals(
                    current.RuleCode,
                    priorityRule.RuleCode)
                && ModerationEvidenceKinds.IsValid(
                    priorityRule.RuleCode,
                    current.EvidenceKind);
            if (hasCompatibleAiEvidence)
            {
                return current with
                {
                    Action = priority.Action,
                    TimeoutSeconds = priority.TimeoutSeconds,
                    PolicySet = priorityRule.PolicySet,
                    TriggerPhrases = priority.TriggerPhrases.Count > 0
                        ? priority.TriggerPhrases
                        : current.TriggerPhrases,
                    Confidence = 1d,
                    RuleCode = priorityRule.RuleCode,
                    EvidenceKind = priority.EvidenceKind ?? current.EvidenceKind,
                };
            }

            // Never synthesize an actionable decision from a generic rule
            // reason. A moderation proposal must carry a contextual reason
            // produced and validated from the AI response for this message.
            return current.Action == ModerationActionKind.None
                ? current
                : new ValidatedModerationDecision(
                    current.TemporaryMessageId,
                    ModerationActionKind.None,
                    null,
                    null,
                    string.Empty,
                    [],
                    current.Confidence);
        }).ToArray();

        return validation with
        {
            Envelope = new ValidatedModerationEnvelope(batch.BatchId, decisions),
            ValidDecisions = decisions,
        };
    }

    private ModerationValidationReport ValidateRuntimeResponse(
        string rawResponse,
        ModerationBatch batch) =>
        _options.LocalAi.UseCompactModerationContractV6
            ? _envelopeValidator.ValidateV6(rawResponse, batch)
            : _envelopeValidator.ValidateV4(rawResponse, batch);

    private async ValueTask RecordValidationAttemptAsync(
        string correlationId,
        string? parentCorrelationId,
        AiValidationAttemptKind attemptKind,
        CompiledModerationPrompt prompt,
        string rawResponse,
        ModerationValidationReport validation,
        AiValidationOutcome outcome,
        int affectedMessageCount,
        CancellationToken cancellationToken)
    {
        if (_validationAttemptStore is null)
        {
            return;
        }

        var contractVersion = prompt.ContractVersion;
        var schemaVersion = ModerationPromptCompiler.ResponseEnvelopeVersion;
        var attempt = new AiValidationAttempt(
            correlationId,
            parentCorrelationId,
            attemptKind,
            AiValidationWorkloadKind.LiveModeration,
            contractVersion,
            schemaVersion,
            prompt.MessageIds.Count,
            prompt.PolicyHash,
            validation.Issues
                .Select(issue => issue.Code)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            affectedMessageCount,
            prompt.PromptCharacters,
            AiValidationResponseFingerprint.FromRawResponse(rawResponse),
            outcome,
            _timeProvider.GetUtcNow());
        await TryRecordValidationAttemptAsync(attempt, cancellationToken)
            .ConfigureAwait(false);
    }

    private ValueTask RecordClassificationValidationAttemptAsync(
        string correlationId,
        string? parentCorrelationId,
        AiValidationAttemptKind attemptKind,
        CompiledModerationPrompt prompt,
        string rawResponse,
        ModerationClassificationReport validation,
        AiValidationOutcome outcome,
        int affectedMessageCount,
        CancellationToken cancellationToken) =>
        RecordStageValidationAttemptAsync(
            correlationId,
            parentCorrelationId,
            attemptKind,
            prompt,
            rawResponse,
            validation.Issues,
            outcome,
            affectedMessageCount,
            cancellationToken);

    private ValueTask RecordConfirmationValidationAttemptAsync(
        string correlationId,
        string? parentCorrelationId,
        CompiledModerationPrompt prompt,
        string rawResponse,
        ModerationConfirmationReport validation,
        AiValidationOutcome outcome,
        int affectedMessageCount,
        CancellationToken cancellationToken) =>
        RecordStageValidationAttemptAsync(
            correlationId,
            parentCorrelationId,
            AiValidationAttemptKind.Confirmation,
            prompt,
            rawResponse,
            validation.Issues,
            outcome,
            affectedMessageCount,
            cancellationToken);

    private async ValueTask RecordStageValidationAttemptAsync(
        string correlationId,
        string? parentCorrelationId,
        AiValidationAttemptKind attemptKind,
        CompiledModerationPrompt prompt,
        string rawResponse,
        IReadOnlyList<ModerationValidationIssue> issues,
        AiValidationOutcome outcome,
        int affectedMessageCount,
        CancellationToken cancellationToken)
    {
        if (_validationAttemptStore is null)
        {
            return;
        }

        var attempt = new AiValidationAttempt(
            correlationId,
            parentCorrelationId,
            attemptKind,
            AiValidationWorkloadKind.LiveModeration,
            prompt.ContractVersion,
            prompt.ResponseContractVersion,
            prompt.MessageIds.Count,
            prompt.PolicyHash,
            issues
                .Select(issue => issue.Code)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            affectedMessageCount,
            prompt.PromptCharacters,
            AiValidationResponseFingerprint.FromRawResponse(rawResponse),
            outcome,
            _timeProvider.GetUtcNow());
        await TryRecordValidationAttemptAsync(attempt, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask RecordLocalFallbackAttemptAsync(
        string batchCorrelationId,
        AiValidationAttemptKind sourceAttempt,
        CompiledModerationPrompt prompt,
        IReadOnlyList<string> fallbackMessageIds,
        CancellationToken cancellationToken)
    {
        if (_validationAttemptStore is null || fallbackMessageIds.Count == 0)
        {
            return;
        }

        var sourceSuffix = sourceAttempt == AiValidationAttemptKind.Repair
            ? "repair"
            : "initial";
        var parentCorrelationId = sourceAttempt == AiValidationAttemptKind.Repair
            ? $"{batchCorrelationId}-repair"
            : batchCorrelationId;
        var attempt = new AiValidationAttempt(
            $"{batchCorrelationId}-{sourceSuffix}-fallback",
            parentCorrelationId,
            AiValidationAttemptKind.LocalFallback,
            AiValidationWorkloadKind.LiveModeration,
            prompt.ContractVersion,
            ModerationPromptCompiler.ResponseEnvelopeVersion,
            prompt.MessageIds.Count,
            prompt.PolicyHash,
            ["rule_missing_local_fallback"],
            fallbackMessageIds.Count,
            prompt.PromptCharacters,
            AiValidationResponseFingerprint.None,
            AiValidationOutcome.Fallback,
            _timeProvider.GetUtcNow());
        await TryRecordValidationAttemptAsync(attempt, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask TryRecordValidationAttemptAsync(
        AiValidationAttempt attempt,
        CancellationToken cancellationToken)
    {
        try
        {
            await _validationAttemptStore!
                .RecordAsync(attempt, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Diagnostics are deliberately best-effort and must never stop
            // live moderation or cause an extra inference attempt.
            Debug.WriteLine(
                $"AI validation diagnostics could not be stored: {exception}");
        }
    }

    private static ModerationBatch CreateRepairBatch(
        ModerationBatch original,
        IReadOnlyList<string> repairMessageIds)
    {
        var targetIds = repairMessageIds.ToHashSet(StringComparer.Ordinal);
        if (targetIds.Count == 0)
        {
            throw new InvalidOperationException(
                "An invalid moderation response did not identify a repair scope.");
        }

        var messages = original.Messages
            .Where(message => targetIds.Contains(message.TemporaryMessageId))
            .ToArray();
        if (messages.Length != targetIds.Count)
        {
            throw new InvalidOperationException(
                "The moderation repair scope contains an unknown message ID.");
        }

        return original with { Messages = messages };
    }

    private static ModerationValidationReport MergeRepairResult(
        ModerationBatch originalBatch,
        ModerationValidationReport initial,
        ModerationValidationReport repaired)
    {
        var mergedById = new Dictionary<string, ValidatedModerationDecision>(
            StringComparer.Ordinal);
        foreach (var decision in initial.ValidDecisions)
        {
            mergedById.Add(decision.TemporaryMessageId, decision);
        }

        foreach (var decision in repaired.Envelope!.Decisions)
        {
            if (!mergedById.TryAdd(decision.TemporaryMessageId, decision))
            {
                return new(
                    Envelope: null,
                    ValidDecisions: initial.ValidDecisions,
                    Issues:
                    [
                        new ModerationValidationIssue(
                            "repair_message_duplicate",
                            $"Repair returned temporary message ID '{decision.TemporaryMessageId}' more than once.",
                            decision.TemporaryMessageId),
                    ],
                    RepairMessageIds: [],
                    RequiresFullRepair: false);
            }
        }

        var ordered = new List<ValidatedModerationDecision>(
            originalBatch.Messages.Count);
        foreach (var message in originalBatch.Messages)
        {
            if (!mergedById.TryGetValue(
                    message.TemporaryMessageId,
                    out var decision))
            {
                return new(
                    Envelope: null,
                    ValidDecisions: ordered,
                    Issues:
                    [
                        new ModerationValidationIssue(
                            "repair_set_incomplete",
                            $"Repair did not return temporary message ID '{message.TemporaryMessageId}'.",
                            message.TemporaryMessageId),
                    ],
                    RepairMessageIds: [],
                    RequiresFullRepair: false);
            }

            ordered.Add(decision);
        }

        return new ModerationValidationReport(
            new ValidatedModerationEnvelope(originalBatch.BatchId, ordered),
            ordered,
            [],
            [],
            RequiresFullRepair: false)
        {
            LocalFallbackMessageIds = initial.LocalFallbackMessageIds
                .Concat(repaired.LocalFallbackMessageIds)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
        };
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
        if (_batcher.FlushChannel(
                batch.Context.Channel.ChannelId,
                _timeProvider.GetUtcNow()) is { } refreshedBatch)
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
                    rule.DefaultAction switch
                    {
                        _ when string.Equals(rule.RuleCode, ModerationRuleCodes.LocalBlacklist, StringComparison.Ordinal) => ModerationActionKind.Ban,
                        _ when string.Equals(rule.RuleCode, ModerationRuleCodes.BotScam, StringComparison.Ordinal) => ModerationActionKind.Ban,
                        ModerationRuleAction.Timeout => ModerationActionKind.Timeout,
                        ModerationRuleAction.Ban => ModerationActionKind.Ban,
                        _ => ModerationActionKind.Delete,
                    },
                    rule.DefaultTimeoutSeconds,
                    rule.PublicReason,
                    rule.Priority,
                    ModerationRuleCodes.AllowsTriggerLearning(rule.RuleCode))
                {
                    EvaluationMode = rule.EvaluationMode switch
                    {
                        RuleEvaluationMode.DeterministicPhrase =>
                            ModerationRuleEvaluationMode.DeterministicPhrase,
                        RuleEvaluationMode.HumanOnly =>
                            ModerationRuleEvaluationMode.HumanOnly,
                        _ => ModerationRuleEvaluationMode.SemanticAi,
                    },
                })
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
        if (_options.TwitchApi.GetBroadcasterIds().Count == 0)
        {
            throw new ArgumentException(
                "Mindestens eine Twitch-Broadcaster-ID muss konfiguriert sein.",
                nameof(_options));
        }

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

    private ChannelContextSnapshot? GetCurrentContext(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        lock (_contextGate)
        {
            return _currentContexts.GetValueOrDefault(channelId);
        }
    }

    private void SetCurrentContext(ChannelContextSnapshot context)
    {
        lock (_contextGate)
        {
            _currentContexts[context.ChannelId] = context;
        }
    }

    private void ClearCurrentContexts()
    {
        lock (_contextGate)
        {
            _currentContexts.Clear();
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
            _inferenceSignal.Release();
        }
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
                _options.LocalAi.Model,
                PromptTokens: null,
                CompletionTokens: null,
                TotalTokens: null,
                ElapsedMilliseconds: null,
                batch.SealedAtUtc));
    }

    private void DiscardQueuedBatches(string reasonCode)
    {
        while (_batcher.TryDequeue(out var batch) && batch is not null)
        {
            PublishSupersededAiRun(batch, reasonCode);
        }

        // Open, not-yet-sealed batches have no footer activity yet. Clear
        // removes those together with any implementation-specific residue.
        _batcher.Clear();
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
                _options.LocalAi.Model,
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
                _options.LocalAi.Model,
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
                occurredAtUtc: chatEvent.ReceivedAtUtc,
                channelId: chatEvent.ChannelId));
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
