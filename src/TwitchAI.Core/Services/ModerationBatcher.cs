using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed class ModerationBatcher : IModerationBatcher
{
    private readonly object _gate = new();
    private readonly ModerationBatcherOptions _options;
    private readonly LinkedList<ModerationBatch> _ready = [];
    private readonly List<ModerationBatchMessage> _current = [];
    private ModerationBatchContext? _currentContext;
    private DateTimeOffset? _openedAtUtc;
    private int _currentCharacters;
    private int _waitingMessageCount;

    public ModerationBatcher()
        : this(ModerationBatcherOptions.Default)
    {
    }

    public ModerationBatcher(ModerationBatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumMessages, 1);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            options.MaximumCollectionDelay,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumMessageCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            options.MaximumQueuedMessages,
            options.MaximumMessages);
        _options = options;
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
            var dropped = MakeRoomForOneMessage();

            if (_current.Count > 0 && !HasSameContext(_currentContext!, context))
            {
                var reason = HasSameCategory(_currentContext!.Channel, context.Channel)
                    ? BatchSealReason.PolicyRevisionChanged
                    : BatchSealReason.CategoryChanged;
                sealedBatches.Add(SealCurrent(now, reason));
            }

            if (_current.Count > 0
                && _currentCharacters + message.PromptCharacterCount >
                    _options.MaximumMessageCharacters)
            {
                sealedBatches.Add(SealCurrent(now, BatchSealReason.CharacterLimit));
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
