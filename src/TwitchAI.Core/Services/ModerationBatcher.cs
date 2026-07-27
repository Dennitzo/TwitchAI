using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed class ModerationBatcher : IModerationBatcher
{
    private readonly object _gate = new();
    private readonly ModerationBatcherOptions _options;
    private readonly IModerationQueueRuntimeSettings _runtimeSettings;
    private readonly LinkedList<ModerationBatch> _ready = [];
    private readonly List<ModerationBatchMessage> _current = [];
    private ModerationBatchContext? _currentContext;
    private DateTimeOffset? _openedAtUtc;
    private int _currentCharacters;
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
                return _openedAtUtc + _options.MaximumCollectionDelay;
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

            if (_current.Count > 0 && !HasSameContext(_currentContext!, context))
            {
                var reason = HasSameCategory(_currentContext!.Channel, context.Channel)
                    ? BatchSealReason.PolicyRevisionChanged
                    : BatchSealReason.CategoryChanged;
                sealedBatches.Add(SealCurrent(now, reason));
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
                    NextDeadlineUtc: NextDeadlineUtc)
                {
                    DuplicateOfTemporaryMessageId =
                        duplicateTemporaryMessageId,
                };
            }

            var dropped = MakeRoomForOneMessage();

            if (_current.Count > 0
                && _currentCharacters + message.PromptCharacterCount >
                    _options.MaximumMessageCharacters)
            {
                sealedBatches.Add(SealCurrent(now, BatchSealReason.CharacterLimit));
            }

            // A sealed tail is still waiting and therefore safe to extend.
            // Keeping its batch ID prevents one slow inference from producing
            // a long queue of tiny, separately displayed AI runs.
            if (_current.Count == 0
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
                    dropped.Count,
                    NextDeadlineUtc: null)
                {
                    DroppedMessages = dropped,
                    UpdatedQueuedBatches = [updatedQueuedBatch],
                };
            }

            if (_current.Count == 0)
            {
                _currentContext = context;
                _openedAtUtc = now;
            }

            if (_current.Any(item =>
                    string.Equals(
                        item.TemporaryMessageId,
                        message.TemporaryMessageId,
                        StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Temporary message ID '{message.TemporaryMessageId}' is duplicated in a batch.");
            }

            _current.Add(message);
            _currentCharacters += message.PromptCharacterCount;
            _waitingMessageCount++;

            if (_current.Count == _options.MaximumMessages)
            {
                sealedBatches.Add(SealCurrent(now, BatchSealReason.MessageLimit));
            }
            else if (_currentCharacters == _options.MaximumMessageCharacters)
            {
                sealedBatches.Add(SealCurrent(now, BatchSealReason.CharacterLimit));
            }

            return new BatchIngressResult(
                sealedBatches.Count == 0
                    ? BatchIngressDisposition.Accepted
                    : BatchIngressDisposition.AcceptedAndSealed,
                sealedBatches,
                dropped.Count,
                _openedAtUtc + _options.MaximumCollectionDelay)
            {
                DroppedMessages = dropped,
            };
        }
    }

    public ModerationBatch? SealIfDue(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (_current.Count == 0
                || nowUtc.ToUniversalTime() <
                    _openedAtUtc + _options.MaximumCollectionDelay)
            {
                return null;
            }

            return SealCurrent(nowUtc.ToUniversalTime(), BatchSealReason.Deadline);
        }
    }

    public ModerationBatch? Flush(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            return _current.Count == 0
                ? null
                : SealCurrent(nowUtc.ToUniversalTime(), BatchSealReason.ManualFlush);
        }
    }

    public bool TryDequeue(out ModerationBatch? batch)
    {
        lock (_gate)
        {
            if (_ready.First is null)
            {
                batch = null;
                return false;
            }

            batch = _ready.First.Value;
            _ready.RemoveFirst();
            _waitingMessageCount -= batch.Messages.Count;
            return true;
        }
    }

    private ModerationBatch SealCurrent(
        DateTimeOffset sealedAtUtc,
        BatchSealReason reason)
    {
        if (_currentContext is null || _openedAtUtc is null || _current.Count == 0)
        {
            throw new InvalidOperationException("An empty moderation batch cannot be sealed.");
        }

        var batch = new ModerationBatch(
            Guid.NewGuid(),
            _currentContext,
            _current.ToArray(),
            _openedAtUtc.Value,
            sealedAtUtc,
            reason);
        _ready.AddLast(batch);
        _current.Clear();
        _currentContext = null;
        _openedAtUtc = null;
        _currentCharacters = 0;
        return batch;
    }

    private bool TryAppendToReadyTail(
        ModerationBatchMessage message,
        ModerationBatchContext context,
        DateTimeOffset nowUtc,
        out ModerationBatch updatedBatch)
    {
        var tailNode = _ready.Last;
        if (tailNode is null
            || !HasSameContext(tailNode.Value.Context, context)
            || tailNode.Value.Messages.Count >= _options.MaximumMessages)
        {
            updatedBatch = null!;
            return false;
        }

        var tail = tailNode.Value;
        var tailCharacters = tail.Messages.Sum(item => item.PromptCharacterCount);
        var updatedCharacters = tailCharacters + message.PromptCharacterCount;
        if (updatedCharacters > _options.MaximumMessageCharacters)
        {
            updatedBatch = null!;
            return false;
        }

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
        tailNode.Value = updatedBatch;
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

        if (_currentContext is not null
            && HasSameContext(_currentContext, context))
        {
            foreach (var candidate in _current)
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

    private List<ModerationBatchMessage> MakeRoomForOneMessage()
    {
        var dropped = new List<ModerationBatchMessage>();
        while (_waitingMessageCount >= _options.MaximumQueuedMessages)
        {
            if (_ready.First is null)
            {
                throw new InvalidOperationException(
                    "The moderation queue reached its limit without an evictable sealed batch.");
            }

            var oldest = _ready.First.Value;
            dropped.Add(oldest.Messages[0]);
            if (oldest.Messages.Count == 1)
            {
                _ready.RemoveFirst();
            }
            else
            {
                _ready.First.Value = oldest with
                {
                    Messages = oldest.Messages.Skip(1).ToArray(),
                };
            }

            _waitingMessageCount--;
        }

        return dropped;
    }

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
}
