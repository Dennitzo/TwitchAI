using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed class MessageAvailabilityRegistry : IMessageAvailabilityRegistry
{
    public static readonly TimeSpan TwitchDeleteWindow = TimeSpan.FromHours(6);

    private readonly object _gate = new();
    private readonly Dictionary<string, ChatMessageIdentityV1> _observed =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _exactDeletions =
        new(StringComparer.Ordinal);
    private readonly Dictionary<(string ChannelId, string UserId), DateTimeOffset> _userClears = [];
    private readonly Dictionary<string, DateTimeOffset> _roomClears =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _eventGaps =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _unknownOutcomes =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _processedEventIds = new(StringComparer.Ordinal);
    private long _revision;

    public void Observe(ChatMessageSnapshot message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!ChatMessageIdentityFingerprint.IsValid(message.Identity))
        {
            throw new InvalidOperationException(
                "A message with an invalid immutable identity fingerprint cannot be observed.");
        }

        lock (_gate)
        {
            if (_observed.TryGetValue(message.Identity.TwitchMessageId, out var existing)
                && !string.Equals(
                    existing.IdentityFingerprint,
                    message.Identity.IdentityFingerprint,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "A Twitch message ID was observed with a different immutable identity.");
            }

            _observed[message.Identity.TwitchMessageId] = message.Identity;
            _revision++;
        }
    }

    public void MarkDeletedExact(
        string eventSubNotificationId,
        string channelId,
        string twitchMessageId,
        string twitchUserId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset receivedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(twitchMessageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(twitchUserId);
        lock (_gate)
        {
            if (!TryAcceptEvent(eventSubNotificationId))
            {
                return;
            }

            _exactDeletions[twitchMessageId] = occurredAtUtc.ToUniversalTime();
            _revision++;
        }
    }

    public void MarkUserCleared(
        string eventSubNotificationId,
        string channelId,
        string twitchUserId,
        DateTimeOffset throughReceivedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(twitchUserId);
        lock (_gate)
        {
            if (!TryAcceptEvent(eventSubNotificationId))
            {
                return;
            }

            SetLatest(
                _userClears,
                (channelId, twitchUserId),
                throughReceivedAtUtc.ToUniversalTime());
            _revision++;
        }
    }

    public void MarkRoomCleared(
        string eventSubNotificationId,
        string channelId,
        DateTimeOffset throughReceivedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        lock (_gate)
        {
            if (!TryAcceptEvent(eventSubNotificationId))
            {
                return;
            }

            SetLatest(_roomClears, channelId, throughReceivedAtUtc.ToUniversalTime());
            _revision++;
        }
    }

    public void MarkUnknownGap(string channelId, DateTimeOffset throughReceivedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        lock (_gate)
        {
            SetLatest(_eventGaps, channelId, throughReceivedAtUtc.ToUniversalTime());
            _revision++;
        }
    }

    public void MarkOutcomeUnknown(string twitchMessageId, DateTimeOffset occurredAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(twitchMessageId);
        lock (_gate)
        {
            SetLatest(
                _unknownOutcomes,
                twitchMessageId,
                occurredAtUtc.ToUniversalTime());
            _revision++;
        }
    }

    public MessageAvailabilityAssessment Assess(
        ChatMessageIdentityV1 identity,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            var now = nowUtc.ToUniversalTime();

            if (!ChatMessageIdentityFingerprint.IsValid(identity)
                || !_observed.TryGetValue(identity.TwitchMessageId, out var observed)
                || !string.Equals(
                    observed.IdentityFingerprint,
                    identity.IdentityFingerprint,
                    StringComparison.Ordinal))
            {
                return Result(
                    MessageAvailability.UnknownAfterEventGap,
                    now,
                    "identity_not_observed");
            }

            if (_exactDeletions.ContainsKey(identity.TwitchMessageId))
            {
                return Result(MessageAvailability.DeletedExact, now, "exact_delete");
            }

            if (_unknownOutcomes.ContainsKey(identity.TwitchMessageId))
            {
                return Result(MessageAvailability.OutcomeUnknown, now, "outbound_outcome_unknown");
            }

            if (_userClears.TryGetValue(
                    (identity.ChannelId, identity.TwitchUserId),
                    out var userWatermark)
                && identity.ReceivedAtUtc <= userWatermark)
            {
                return Result(MessageAvailability.ClearedForUser, now, "user_clear_watermark");
            }

            if (_roomClears.TryGetValue(identity.ChannelId, out var roomWatermark)
                && identity.ReceivedAtUtc <= roomWatermark)
            {
                return Result(MessageAvailability.RoomClearedHidden, now, "room_clear_watermark");
            }

            if (_eventGaps.TryGetValue(identity.ChannelId, out var gapWatermark)
                && identity.ReceivedAtUtc <= gapWatermark)
            {
                return Result(MessageAvailability.UnknownAfterEventGap, now, "eventsub_gap");
            }

            if (now - identity.ReceivedAtUtc >= TwitchDeleteWindow)
            {
                return Result(
                    MessageAvailability.ExpiredNotDeletable,
                    now,
                    "six_hour_delete_window");
            }

            return Result(MessageAvailability.ObservedActive, now, "locally_observed_active");
        }
    }

    private MessageAvailabilityAssessment Result(
        MessageAvailability availability,
        DateTimeOffset assessedAtUtc,
        string evidenceCode)
    {
        return new MessageAvailabilityAssessment(
            availability,
            assessedAtUtc,
            evidenceCode,
            _revision);
    }

    private bool TryAcceptEvent(string eventSubNotificationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventSubNotificationId);
        return _processedEventIds.Add(eventSubNotificationId);
    }

    private static void SetLatest<TKey>(
        IDictionary<TKey, DateTimeOffset> values,
        TKey key,
        DateTimeOffset candidate)
        where TKey : notnull
    {
        if (!values.TryGetValue(key, out var current) || candidate > current)
        {
            values[key] = candidate;
        }
    }
}
