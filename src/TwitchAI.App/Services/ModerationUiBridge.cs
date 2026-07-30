using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using TwitchAI.App.ViewModels;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Pipeline;

namespace TwitchAI.App.Services;

public sealed class ModerationUiBridge : IDisposable
{
    private const int AbortHResult = unchecked((int)0x80004004);
    private const int FailHResult = unchecked((int)0x80004005);

    private readonly AppState _state;
    private readonly IModerationCoordinator _coordinator;
    private readonly ILocalAiRunActivityMonitor _aiRunActivityMonitor;
    private readonly object _aiRunGate = new();
    private readonly Dictionary<string, LocalAiRunActivity> _aiRunsByCorrelationId =
        new(StringComparer.Ordinal);
    private readonly LinkedList<string> _aiRunOrder = [];
    private DispatcherQueue? _dispatcher;
    private long _aiRunVersion;
    private int _aiRunDispatchScheduled;
    private int _disposed;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public ModerationUiBridge(
        AppState state,
        IModerationCoordinator coordinator,
        ILocalAiRunActivityMonitor aiRunActivityMonitor)
    {
        _state = state;
        _coordinator = coordinator;
        _aiRunActivityMonitor = aiRunActivityMonitor;
        _coordinator.EventReceived += OnEventReceived;
        _aiRunActivityMonitor.ActivityChanged += OnAiRunActivityChanged;
    }

    public void Initialize(DispatcherQueue dispatcher)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        _dispatcher = dispatcher;
        ScheduleAiRunProjection();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _coordinator.EventReceived -= OnEventReceived;
        _aiRunActivityMonitor.ActivityChanged -= OnAiRunActivityChanged;
        _dispatcher = null;
    }

    private void OnEventReceived(
        object? sender,
        ModerationPipelineEventArgs eventArgs)
    {
        var dispatcher = _dispatcher;
        if (IsDisposed || dispatcher is null)
        {
            return;
        }

        if (dispatcher.HasThreadAccess)
        {
            ApplySafely(eventArgs);
        }
        else
        {
            dispatcher.TryEnqueue(
                () =>
                {
                    if (!IsDisposed)
                    {
                        ApplySafely(eventArgs);
                    }
                });
        }
    }

    private void ApplySafely(ModerationPipelineEventArgs eventArgs)
    {
        try
        {
            Apply(eventArgs);
        }
        catch (COMException exception)
            when (exception.HResult is AbortHResult or FailHResult)
        {
            // A navigation can detach the native ItemsRepeater subscriber
            // between enqueue and delivery. Pipeline state remains persisted;
            // only this obsolete visual notification is skipped.
            Debug.WriteLine(
                $"Moderation UI projection skipped: 0x{exception.HResult:X8} "
                + exception.Message);
        }
    }

    private void OnAiRunActivityChanged(
        object? sender,
        LocalAiRunActivityEventArgs eventArgs)
    {
        lock (_aiRunGate)
        {
            if (IsDisposed
                || IsOlderThanCurrentActivity(eventArgs.Activity))
            {
                return;
            }

            _aiRunsByCorrelationId[eventArgs.Activity.CorrelationId] =
                eventArgs.Activity;
            _aiRunOrder.Remove(eventArgs.Activity.CorrelationId);
            _aiRunOrder.AddFirst(eventArgs.Activity.CorrelationId);
            PruneCompletedAiRuns();

            _aiRunVersion++;
        }

        ScheduleAiRunProjection();
    }

    private void Apply(ModerationPipelineEventArgs eventArgs)
    {
        switch (eventArgs.Kind)
        {
            case ModerationPipelineEventKind.Started:
                _state.IsAnalysisRunning = true;
                _state.TwitchStatus = "Twitch verbunden";
                _state.EventSubStatus = "EventSub aktiv";
                ApplyCategory(eventArgs.ChannelContext, eventArgs.ActivePolicySets);
                break;
            case ModerationPipelineEventKind.Stopped:
                _state.IsAnalysisRunning = false;
                _state.EventSubStatus = "EventSub pausiert";
                break;
            case ModerationPipelineEventKind.CategoryChanged:
                ApplyCategory(eventArgs.ChannelContext, eventArgs.ActivePolicySets);
                break;
            case ModerationPipelineEventKind.WhitelistSkipped:
                _state.SkippedWhitelistCount++;
                RemoveReanalyzedCase(eventArgs.CaseId);
                break;
            case ModerationPipelineEventKind.Allowed:
                RemoveReanalyzedCase(eventArgs.CaseId);
                break;
            case ModerationPipelineEventKind.CaseCreated:
                AddCase(eventArgs);
                break;
            case ModerationPipelineEventKind.ChatReplyCreated:
                AddChatReply(eventArgs);
                break;
            case ModerationPipelineEventKind.DroppedOverload:
                _state.SkippedOverloadCount++;
                break;
            case ModerationPipelineEventKind.BatchRejected:
                _state.LmStudioStatus = "Ungültige AI-Antwort";
                break;
            case ModerationPipelineEventKind.StalePolicyRequeued:
                _state.LmStudioStatus = "Policy geändert · neue Analyse";
                break;
            case ModerationPipelineEventKind.AvailabilityChanged:
                ApplyAvailability(eventArgs);
                break;
            case ModerationPipelineEventKind.Faulted:
                _state.EventSubStatus = $"Fehler · {eventArgs.StatusCode}";
                _state.IsAnalysisRunning = false;
                break;
        }
    }

    private void AddCase(ModerationPipelineEventArgs eventArgs)
    {
        if (eventArgs.ChatMessage is not { } message
            || eventArgs.Decision is not { } decision
            || eventArgs.CaseId is not { } caseId
            || !ModerationProposalEligibility.CanBeReviewed(decision))
        {
            return;
        }

        var item = new ModerationCaseItem
        {
            CaseId = caseId,
            ChannelId = message.Identity.ChannelId,
            UserName = message.Identity.DisplayName,
            UserId = message.Identity.TwitchUserId,
            MessageId = message.Identity.TwitchMessageId,
            IdentityFingerprint = message.Identity.IdentityFingerprint,
            OriginalMessage = message.Text.OriginalText,
            CategoryName = message.ChannelContext.CategoryDisplayName,
            CategoryId = message.ChannelContext.CategoryId,
            ReceivedAt = message.Identity.ReceivedAtUtc,
            RowVersion = eventArgs.CaseRowVersion ?? 1,
            Action = decision.Action switch
            {
                ModerationActionKind.Delete => "delete",
                ModerationActionKind.Timeout => "timeout",
                ModerationActionKind.Ban => "ban",
                _ => throw new InvalidDataException(
                    $"Aktion '{decision.Action}' gehört nicht in die Moderationswarteschlange."),
            },
            TimeoutSeconds = decision.TimeoutSeconds
                ?? ModerationActionRules.DefaultTimeoutSeconds,
            Reason = ModerationReasonNormalizer.Normalize(
                decision.Reason,
                decision.RuleCode,
                decision.PolicySet),
            PolicySet = decision.PolicySet == PolicySetCode.Backseatgaming
                ? "Backseatgaming"
                : "General",
            RuleCode = decision.RuleCode,
            Confidence = decision.Confidence,
            LocalMatchCodes = eventArgs.LocalMatchCodes,
            AiLatencyMilliseconds = eventArgs.AiLatencyMilliseconds,
            AvailabilityLabel = "Nachricht verfügbar",
            Status = "Pending",
        };
        foreach (var phrase in decision.TriggerPhrases)
        {
            item.TriggerPhrases.Add(new TriggerPhraseItem(phrase));
        }

        var existingIndex = -1;
        for (var index = 0; index < _state.ModerationCases.Count; index++)
        {
            if (!_state.ModerationCases[index].IsChatReply
                && _state.ModerationCases[index].CaseId == caseId)
            {
                existingIndex = index;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            _state.ModerationCases[existingIndex] = item;
        }
        else
        {
            _state.ModerationCases.Insert(0, item);
        }
    }

    private void AddChatReply(ModerationPipelineEventArgs eventArgs)
    {
        if (eventArgs.ChatReplyProposal is not { } proposal)
        {
            return;
        }

        var item = new ModerationCaseItem
        {
            CaseId = proposal.ProposalId,
            IsChatReply = true,
            ReplyMessage = proposal.RenderedResponse,
            ChannelId = proposal.BroadcasterId,
            UserName = proposal.ChatterDisplayName,
            UserId = proposal.ChatterUserId,
            MessageId = proposal.SourceMessageId,
            IdentityFingerprint = proposal.IdentityFingerprint,
            OriginalMessage = proposal.OriginalMessage,
            CategoryName = proposal.CategoryName,
            CategoryId = proposal.CategoryId,
            ReceivedAt = proposal.MessageReceivedAt,
            RowVersion = proposal.RowVersion,
            Action = "chat",
            TimeoutSeconds = 0,
            Reason = string.Empty,
            PolicySet = string.Empty,
            RuleCode = null,
            Confidence = proposal.Confidence,
            LocalMatchCodes = [],
            AiLatencyMilliseconds = proposal.AiLatencyMilliseconds,
            AvailabilityLabel = "Antwortvorlage semantisch erkannt",
            Status = "Pending",
        };

        var existingIndex = -1;
        for (var index = 0; index < _state.ModerationCases.Count; index++)
        {
            if (_state.ModerationCases[index].IsChatReply
                && _state.ModerationCases[index].CaseId == proposal.ProposalId)
            {
                existingIndex = index;
                break;
            }
        }

        if (existingIndex >= 0)
        {
            _state.ModerationCases[existingIndex] = item;
        }
        else
        {
            _state.ModerationCases.Insert(0, item);
        }
    }

    private bool IsOlderThanCurrentActivity(LocalAiRunActivity activity)
    {
        if (!_aiRunsByCorrelationId.TryGetValue(
            activity.CorrelationId,
            out var current))
        {
            return false;
        }

        if (activity.OccurredAtUtc < current.OccurredAtUtc)
        {
            return true;
        }

        return activity.OccurredAtUtc == current.OccurredAtUtc
            && activity.State < current.State;
    }

    private void ScheduleAiRunProjection()
    {
        var dispatcher = _dispatcher;
        if (IsDisposed
            || dispatcher is null
            || Interlocked.CompareExchange(
                ref _aiRunDispatchScheduled,
                1,
                0) != 0)
        {
            return;
        }

        if (!dispatcher.TryEnqueue(
            DispatcherQueuePriority.Low,
            DrainAiRunProjection))
        {
            Volatile.Write(ref _aiRunDispatchScheduled, 0);
        }
    }

    private void DrainAiRunProjection()
    {
        if (IsDisposed)
        {
            Volatile.Write(ref _aiRunDispatchScheduled, 0);
            return;
        }

        LocalAiRunActivity[] snapshot;
        long capturedVersion;
        lock (_aiRunGate)
        {
            capturedVersion = _aiRunVersion;
            snapshot = _aiRunOrder
                .Select(correlationId =>
                    _aiRunsByCorrelationId[correlationId])
                .ToArray();
        }

        try
        {
            ApplyAiRunSnapshot(snapshot);
        }
        catch (COMException exception)
            when (exception.HResult is AbortHResult or FailHResult)
        {
            // A WinUI element may be between Unloaded and disposal while the
            // low-priority callback runs. The next activity/view activation
            // projects the complete snapshot again.
            Debug.WriteLine(
                $"AI run UI projection skipped: 0x{exception.HResult:X8} "
                + exception.Message);
        }
        finally
        {
            Volatile.Write(ref _aiRunDispatchScheduled, 0);

            long currentVersion;
            lock (_aiRunGate)
            {
                currentVersion = _aiRunVersion;
            }

            if (!IsDisposed && currentVersion != capturedVersion)
            {
                ScheduleAiRunProjection();
            }
        }
    }

    private void ApplyAiRunSnapshot(
        LocalAiRunActivity[] activities)
    {
        // _aiRunOrder is newest-first. Reverse the queued projection so the
        // footer presents the batches in their actual FIFO execution order.
        var queuedActivities = activities
            .Where(activity => activity.State == LocalAiRunState.Queued)
            .Reverse()
            .ToArray();
        SynchronizeQueuedAiRuns(queuedActivities);
        _state.QueuedLocalAiRunCount = queuedActivities.Length;
        _state.QueuedLocalAiMessageCount = queuedActivities
            .Sum(activity => activity.MessageCount);

        var activity = activities.FirstOrDefault(
                candidate => candidate.State == LocalAiRunState.Running)
            ?? activities.FirstOrDefault(
                candidate => candidate.State is LocalAiRunState.Succeeded
                    or LocalAiRunState.Failed);
        if (activity is null)
        {
            _state.CurrentLocalAiRun.Clear();
            if (queuedActivities.FirstOrDefault() is { } waiting)
            {
                _state.LmStudioStatus =
                    $"AI wartet · {waiting.MessageCount:N0} Nachrichten";
            }

            return;
        }

        _state.CurrentLocalAiRun.Apply(activity);

        _state.LmStudioStatus = activity.State switch
        {
            LocalAiRunState.Queued =>
                $"AI wartet · {activity.MessageCount:N0} Nachrichten",
            LocalAiRunState.Running =>
                $"AI arbeitet · {activity.MessageCount:N0} Nachrichten",
            LocalAiRunState.Succeeded when activity.TotalTokens is { } totalTokens =>
                $"AI bereit · {totalTokens:N0} Token",
            LocalAiRunState.Succeeded => "AI bereit · Tokenzahl nicht gemeldet",
            _ => activity.ErrorCode == "cancelled"
                ? "AI-Lauf abgebrochen"
                : $"AI-Fehler · {activity.ErrorCode ?? "unbekannt"}",
        };
    }

    private void SynchronizeQueuedAiRuns(
        LocalAiRunActivity[] queuedActivities)
    {
        var desiredIds = queuedActivities
            .Select(activity => activity.CorrelationId)
            .ToHashSet(StringComparer.Ordinal);

        for (var index = _state.QueuedLocalAiRuns.Count - 1; index >= 0; index--)
        {
            if (!desiredIds.Contains(
                    _state.QueuedLocalAiRuns[index].CorrelationId))
            {
                _state.QueuedLocalAiRuns.RemoveAt(index);
            }
        }

        for (var desiredIndex = 0;
             desiredIndex < queuedActivities.Length;
             desiredIndex++)
        {
            var activity = queuedActivities[desiredIndex];
            var existingIndex = -1;
            for (var index = 0; index < _state.QueuedLocalAiRuns.Count; index++)
            {
                if (string.Equals(
                        _state.QueuedLocalAiRuns[index].CorrelationId,
                        activity.CorrelationId,
                        StringComparison.Ordinal))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                _state.QueuedLocalAiRuns.Insert(
                    desiredIndex,
                    new LocalAiRunItem(activity));
                continue;
            }

            var item = _state.QueuedLocalAiRuns[existingIndex];
            item.Apply(activity);
            if (existingIndex != desiredIndex)
            {
                _state.QueuedLocalAiRuns.Move(existingIndex, desiredIndex);
            }
        }
    }

    private void PruneCompletedAiRuns()
    {
        var retainedTerminalRunCount = 0;
        var currentNode = _aiRunOrder.First;
        while (currentNode is not null)
        {
            var nextNode = currentNode.Next;
            var activity = _aiRunsByCorrelationId[currentNode.Value];
            if ((activity.State is LocalAiRunState.Succeeded
                    or LocalAiRunState.Failed)
                && ++retainedTerminalRunCount > 1)
            {
                _aiRunOrder.Remove(currentNode);
                _aiRunsByCorrelationId.Remove(activity.CorrelationId);
            }

            currentNode = nextNode;
        }
    }

    private void RemoveReanalyzedCase(long? caseId)
    {
        if (caseId is null)
        {
            return;
        }

        var existing = _state.ModerationCases.FirstOrDefault(
            item => !item.IsChatReply && item.CaseId == caseId.Value);
        if (existing is not null)
        {
            _state.ModerationCases.Remove(existing);
        }
    }

    private void ApplyCategory(
        ChannelContextSnapshot? context,
        IReadOnlyList<PolicySetCode> activePolicySets)
    {
        if (context is null)
        {
            return;
        }

        _state.CategoryName = string.IsNullOrWhiteSpace(context.CategoryDisplayName)
            ? "Kategorie unbekannt"
            : context.CategoryDisplayName;
        _state.ActivePolicies = activePolicySets.Contains(
            PolicySetCode.Backseatgaming)
            ? "General + Backseatgaming"
            : "Nur General";
    }

    private void ApplyAvailability(ModerationPipelineEventArgs eventArgs)
    {
        var occurredAt = eventArgs.OccurredAtUtc ?? DateTimeOffset.UtcNow;
        foreach (var item in _state.ModerationCases)
        {
            var affected = eventArgs.StatusCode switch
            {
                "message_deleted" => string.Equals(
                    item.MessageId,
                    eventArgs.TargetMessageId,
                    StringComparison.Ordinal),
                "user_messages_cleared" =>
                    string.Equals(
                        item.UserId,
                        eventArgs.TargetUserId,
                        StringComparison.Ordinal)
                    && item.ReceivedAt <= occurredAt,
                "room_cleared" or "eventsub_gap" => item.ReceivedAt <= occurredAt,
                _ => false,
            };
            if (!affected)
            {
                continue;
            }

            item.AvailabilityLabel = eventArgs.StatusCode switch
            {
                "message_deleted" => "Bereits gelöscht",
                "user_messages_cleared" => "Für User entfernt",
                "room_cleared" => "Durch Chat-Leerung verborgen",
                _ => "Nach EventSub-Lücke nicht sicher",
            };
        }
    }
}
