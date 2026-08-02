using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed class ModerationBatcher : IModerationBatcher
{
    private static readonly IReadOnlySet<string> NoExcludedChannels =
        new HashSet<string>(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly ModerationBatcherOptions _options;
    private readonly IModerationQueueRuntimeSettings _runtimeSettings;
    private readonly LinkedList<ModerationBatch> _ready = [];
    private readonly Dictionary<string, OpenBatch> _openByChannel =
        new(StringComparer.Ordinal);
    private int _waitingMessageCount;

    public ModerationBatcher()
        : this(
            ModerationBatcherOptions.Default,
            new ModerationQueueRuntimeSettings())
    {
    }

    public ModerationBatcher(ModerationBatcherOptions options)
        : this(options, new ModerationQueueRuntimeSettings())
    {
    }

    public ModerationBatcher(
        IModerationQueueRuntimeSettings runtimeSettings)
        : this(ModerationBatcherOptions.Default, runtimeSettings)
    {
    }

    public ModerationBatcher(
        ModerationBatcherOptions options,
        IModerationQueueRuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumMessages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            options.MaximumCollectionDelay,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumMessageCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaximumQueuedMessages,
            options.MaximumMessages);
        _options = options;
        _runtimeSettings = runtimeSettings;
    }

    public int WaitingMessageCount
    {
        get
        {
            lock (_gate)
            {
                return _waitingMessageCount;
            }
        }
    }

    public DateTimeOffset? NextDeadlineUtc
    {
        get
        {
            lock (_gate)
            {
                return EarliestOpenBatch()?.DeadlineUtc;
            }
        }
    }

    public BatchIngressResult Enqueue(
        ModerationBatchMessage message,
        ModerationBatchContext context,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);
        if (message.PromptCharacterCount > _options.MaximumMessageCharacters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                message.PromptCharacterCount,
                "A single Twitch message exceeds the configured batch character budget.");
        }

        lock (_gate)
        {
            var now = nowUtc.ToUniversalTime();
            var sealedBatches = new List<ModerationBatch>(2);
            var channelId = context.Channel.ChannelId;

            if (_openByChannel.TryGetValue(channelId, out var open)
                && !HasSameContext(open.Context, context))
            {
                var reason = HasSameCategory(open.Context.Channel, context.Channel)
                    ? BatchSealReason.PolicyRevisionChanged
                    : BatchSealReason.CategoryChanged;
                sealedBatches.Add(SealOpenBatch(channelId, open, now, reason));
                open = null;
            }

            if (TryFindQueuedDuplicate(
                    message,
                    context,
                    out var duplicateTemporaryMessageId))
            {
                return new BatchIngressResult(
                    BatchIngressDisposition.DuplicateSuppressed,
                    sealedBatches,
                    DroppedOldestMessages: 0,
                    NextDeadlineUtc: EarliestOpenBatch()?.DeadlineUtc)
                {
                    DuplicateOfTemporaryMessageId =
                        duplicateTemporaryMessageId,
                };
            }

            var eviction = MakeRoomForOneMessage();
            _openByChannel.TryGetValue(channelId, out open);

            if (open is not null
                && open.CharacterCount + message.PromptCharacterCount >
                    _options.MaximumMessageCharacters)
            {
                sealedBatches.Add(
                    SealOpenBatch(
                        channelId,
                        open,
                        now,
                        BatchSealReason.CharacterLimit));
                open = null;
            }

            // A sealed batch is still waiting and therefore safe to extend.
            // Find the newest matching batch even when other channels were
            // queued afterwards. Keeping its batch ID prevents interleaved
            // channels from producing many tiny AI runs.
            if (open is null
                && TryAppendToReadyTail(
                    message,
                    context,
                    now,
                    out var updatedQueuedBatch))
            {
                return new BatchIngressResult(
                    sealedBatches.Count == 0
                        ? BatchIngressDisposition.Accepted
                        : BatchIngressDisposition.AcceptedAndSealed,
                    sealedBatches,
                    eviction.DroppedMessages.Count,
                    NextDeadlineUtc: EarliestOpenBatch()?.DeadlineUtc)
                {
                    DroppedMessages = eviction.DroppedMessages,
                    UpdatedQueuedBatches = MergeUpdatedQueuedBatches(
                        eviction.UpdatedQueuedBatches,
                        updatedQueuedBatch),
                    RemovedQueuedBatchIds = eviction.RemovedQueuedBatchIds,
                };
            }

            if (open is null)
            {
                open = new OpenBatch(
                    context,
                    now,
                    _options.MaximumCollectionDelay);
                _openByChannel.Add(channelId, open);
            }

            if (open.Messages.Any(item =>
                    string.Equals(
                        item.TemporaryMessageId,
                        message.TemporaryMessageId,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Temporary message ID '{message.TemporaryMessageId}' is duplicated in a batch.");
            }

            open.Messages.Add(message);
            open.CharacterCount += message.PromptCharacterCount;
            _waitingMessageCount++;

            if (open.Messages.Count == _options.MaximumMessages)
            {
                sealedBatches.Add(
                    SealOpenBatch(
                        channelId,
                        open,
                        now,
                        BatchSealReason.MessageLimit));
            }
            else if (open.CharacterCount == _options.MaximumMessageCharacters)
            {
                sealedBatches.Add(
                    SealOpenBatch(
                        channelId,
                        open,
                        now,
                        BatchSealReason.CharacterLimit));
            }

            return new BatchIngressResult(
                sealedBatches.Count == 0
                    ? BatchIngressDisposition.Accepted
                    : BatchIngressDisposition.AcceptedAndSealed,
                sealedBatches,
                eviction.DroppedMessages.Count,
                EarliestOpenBatch()?.DeadlineUtc)
            {
                DroppedMessages = eviction.DroppedMessages,
                UpdatedQueuedBatches = eviction.UpdatedQueuedBatches,
                RemovedQueuedBatchIds = eviction.RemovedQueuedBatchIds,
            };
        }
    }

    public ModerationBatch? SealIfDue(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var earliest = EarliestOpenBatch();
            var now = nowUtc.ToUniversalTime();
            if (earliest is null || now < earliest.DeadlineUtc)
            {
                return null;
            }

            return SealOpenBatch(
                earliest.Context.Channel.ChannelId,
                earliest,
                now,
                BatchSealReason.Deadline);
        }
    }

    public ModerationBatch? Flush(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var earliest = EarliestOpenBatch();
            return earliest is null
                ? null
                : SealOpenBatch(
                    earliest.Context.Channel.ChannelId,
                    earliest,
                    nowUtc.ToUniversalTime(),
                    BatchSealReason.ManualFlush);
        }
    }

    public ModerationBatch? FlushChannel(
        string channelId,
        DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        lock (_gate)
        {
            return !_openByChannel.TryGetValue(channelId, out var open)
                ? null
                : SealOpenBatch(
                    channelId,
                    open,
                    nowUtc.ToUniversalTime(),
                    BatchSealReason.ManualFlush);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _ready.Clear();
            _openByChannel.Clear();
            _waitingMessageCount = 0;
        }
    }

    public bool TryDequeue(out ModerationBatch? batch)
        => TryDequeue(NoExcludedChannels, out batch);

    public bool TryDequeue(
        IReadOnlySet<string> excludedChannelIds,
        out ModerationBatch? batch)
    {
        ArgumentNullException.ThrowIfNull(excludedChannelIds);
        lock (_gate)
        {
            var node = _ready.First;
            while (node is not null
                && excludedChannelIds.Contains(
                    node.Value.Context.Channel.ChannelId))
            {
                node = node.Next;
            }

            if (node is null)
            {
                batch = null;
                return false;
            }

            batch = node.Value;
            _ready.Remove(node);
            _waitingMessageCount -= batch.Messages.Count;
            return true;
        }
    }


    private ModerationBatch SealOpenBatch(
        string channelId,
        OpenBatch open,
        DateTimeOffset sealedAtUtc,
        BatchSealReason reason)
    {
        if (open.Messages.Count == 0
            || !_openByChannel.TryGetValue(channelId, out var current)
            || !ReferenceEquals(open, current))
        {
            throw new InvalidOperationException("An empty moderation batch cannot be sealed.");
        }

        var batch = new ModerationBatch(
            Guid.NewGuid(),
            open.Context,
            open.Messages.ToArray(),
            open.OpenedAtUtc,
            sealedAtUtc,
            reason);
        _ready.AddLast(batch);
        _openByChannel.Remove(channelId);
        return batch;
    }

    private bool TryAppendToReadyTail(
        ModerationBatchMessage message,
        ModerationBatchContext context,
        DateTimeOffset nowUtc,
        out ModerationBatch updatedBatch)
    {
        LinkedListNode<ModerationBatch>? targetNode = null;
        for (var candidate = _ready.Last;
             candidate is not null;
             candidate = candidate.Previous)
        {
            if (!string.Equals(
                    candidate.Value.Context.Channel.ChannelId,
                    context.Channel.ChannelId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            // A newer batch for this channel is a hard ordering barrier even
            // when an older batch happens to have the same category again.
            if (!HasSameContext(candidate.Value.Context, context)
                || candidate.Value.Messages.Count >= _options.MaximumMessages
                || candidate.Value.Messages.Sum(
                    item => item.PromptCharacterCount)
                    + message.PromptCharacterCount
                    > _options.MaximumMessageCharacters)
            {
                updatedBatch = null!;
                return false;
            }

            targetNode = candidate;
            break;
        }

        if (targetNode is null)
        {
            updatedBatch = null!;
            return false;
        }

        var tail = targetNode.Value;
        var tailCharacters = tail.Messages.Sum(item => item.PromptCharacterCount);
        var updatedCharacters = tailCharacters + message.PromptCharacterCount;

        if (tail.Messages.Any(item =>
                string.Equals(
                    item.TemporaryMessageId,
                    message.TemporaryMessageId,
                    StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Temporary message ID '{message.TemporaryMessageId}' is duplicated in a batch.");
        }

        var messages = tail.Messages.Append(message).ToArray();
        var sealReason = messages.Length == _options.MaximumMessages
            ? BatchSealReason.MessageLimit
            : updatedCharacters == _options.MaximumMessageCharacters
                ? BatchSealReason.CharacterLimit
                : tail.SealReason;
        updatedBatch = tail with
        {
            Messages = messages,
            SealedAtUtc = nowUtc,
            SealReason = sealReason,
        };
        targetNode.Value = updatedBatch;
        _waitingMessageCount++;
        return true;
    }

    private bool TryFindQueuedDuplicate(
        ModerationBatchMessage message,
        ModerationBatchContext context,
        out string duplicateTemporaryMessageId)
    {
        if (!_runtimeSettings.RemoveIdenticalMessages
            || message.ReanalysisCaseId.HasValue)
        {
            duplicateTemporaryMessageId = string.Empty;
            return false;
        }

        foreach (var batch in _ready)
        {
            if (!HasSameContext(batch.Context, context))
            {
                continue;
            }

            foreach (var candidate in batch.Messages)
            {
                if (IsDuplicateCandidate(candidate, message))
                {
                    duplicateTemporaryMessageId =
                        candidate.TemporaryMessageId;
                    return true;
                }
            }
        }

        foreach (var open in _openByChannel.Values)
        {
            if (!HasSameContext(open.Context, context))
            {
                continue;
            }

            foreach (var candidate in open.Messages)
            {
                if (IsDuplicateCandidate(candidate, message))
                {
                    duplicateTemporaryMessageId =
                        candidate.TemporaryMessageId;
                    return true;
                }
            }
        }

        duplicateTemporaryMessageId = string.Empty;
        return false;
    }

    private static bool IsDuplicateCandidate(
        ModerationBatchMessage candidate,
        ModerationBatchMessage incoming)
    {
        return !candidate.ReanalysisCaseId.HasValue
            && string.Equals(
                candidate.Message.Text.LowerText,
                incoming.Message.Text.LowerText,
                StringComparison.Ordinal);
    }

    private QueueEvictionResult MakeRoomForOneMessage()
    {
        var dropped = new List<ModerationBatchMessage>();
        var removedBatchIds = new List<Guid>();
        var updatedBatches = new Dictionary<Guid, ModerationBatch>();
        while (_waitingMessageCount >= _options.MaximumQueuedMessages)
        {
            if (_ready.First is not null)
            {
                var oldest = _ready.First.Value;
                dropped.Add(oldest.Messages[0]);
                if (oldest.Messages.Count == 1)
                {
                    _ready.RemoveFirst();
                    updatedBatches.Remove(oldest.BatchId);
                    removedBatchIds.Add(oldest.BatchId);
                }
                else
                {
                    var updated = oldest with
                    {
                        Messages = oldest.Messages.Skip(1).ToArray(),
                    };
                    _ready.First.Value = updated;
                    updatedBatches[updated.BatchId] = updated;
                }
            }
            else
            {
                var oldestOpen = _openByChannel.Values
                    .Where(candidate => candidate.Messages.Count > 0)
                    .OrderBy(candidate => candidate.Messages[0].Message.IngressSequence)
                    .ThenBy(candidate => candidate.OpenedAtUtc)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "The moderation queue reached its limit without an evictable message.");
                dropped.Add(oldestOpen.Messages[0]);
                oldestOpen.CharacterCount -=
                    oldestOpen.Messages[0].PromptCharacterCount;
                oldestOpen.Messages.RemoveAt(0);
                if (oldestOpen.Messages.Count == 0)
                {
                    _openByChannel.Remove(
                        oldestOpen.Context.Channel.ChannelId);
                }
            }

            _waitingMessageCount--;
        }

        return new QueueEvictionResult(
            dropped,
            updatedBatches.Values.ToArray(),
            removedBatchIds);
    }

    private static ModerationBatch[] MergeUpdatedQueuedBatches(
        IReadOnlyList<ModerationBatch> evictionUpdates,
        ModerationBatch appendedTail)
    {
        if (evictionUpdates.Count == 0)
        {
            return [appendedTail];
        }

        var merged = evictionUpdates
            .Where(batch => batch.BatchId != appendedTail.BatchId)
            .Append(appendedTail)
            .ToArray();
        return merged;
    }

    private sealed record QueueEvictionResult(
        IReadOnlyList<ModerationBatchMessage> DroppedMessages,
        IReadOnlyList<ModerationBatch> UpdatedQueuedBatches,
        IReadOnlyList<Guid> RemovedQueuedBatchIds);

    private static bool HasSameContext(
        ModerationBatchContext left,
        ModerationBatchContext right)
    {
        return HasSameCategory(left.Channel, right.Channel)
            && string.Equals(
                left.EffectivePolicy.RevisionFingerprint,
                right.EffectivePolicy.RevisionFingerprint,
                StringComparison.Ordinal);
    }

    private static bool HasSameCategory(
        ChannelContextSnapshot left,
        ChannelContextSnapshot right)
    {
        return string.Equals(left.ChannelId, right.ChannelId, StringComparison.Ordinal)
            && string.Equals(left.CategoryId, right.CategoryId, StringComparison.Ordinal)
            && left.IsVerified == right.IsVerified;
    }

    private OpenBatch? EarliestOpenBatch()
    {
        return _openByChannel.Values
            .OrderBy(open => open.DeadlineUtc)
            .ThenBy(open => open.OpenedAtUtc)
            .ThenBy(open => open.Context.Channel.ChannelId, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private sealed class OpenBatch(
        ModerationBatchContext context,
        DateTimeOffset openedAtUtc,
        TimeSpan maximumCollectionDelay)
    {
        public ModerationBatchContext Context { get; } = context;

        public DateTimeOffset OpenedAtUtc { get; } = openedAtUtc;

        public DateTimeOffset DeadlineUtc { get; } =
            openedAtUtc + maximumCollectionDelay;

        public List<ModerationBatchMessage> Messages { get; } = [];

        public int CharacterCount { get; set; }
    }
}
