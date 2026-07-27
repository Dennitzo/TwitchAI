namespace TwitchAI.Core.Models;

public enum MessageAvailability
{
    ObservedActive,
    DeletedExact,
    ClearedForUser,
    RoomClearedHidden,
    ExpiredNotDeletable,
    UnknownAfterEventGap,
    OutcomeUnknown,
}

public sealed record MessageAvailabilityAssessment(
    MessageAvailability Availability,
    DateTimeOffset AssessedAtUtc,
    string EvidenceCode,
    long Revision)
{
    public bool CanExecuteTwitchAction => Availability == MessageAvailability.ObservedActive;
}

public sealed record MessageAvailabilityEvent(
    string EventSubNotificationId,
    string ChannelId,
    string? TwitchMessageId,
    string? TwitchUserId,
    MessageAvailability Availability,
    DateTimeOffset OccurredAtUtc,
    DateTimeOffset ReceivedAtUtc);

public sealed record MessageClearWatermark(
    string ChannelId,
    string? TwitchUserId,
    DateTimeOffset ThroughReceivedAtUtc,
    MessageAvailability Availability,
    long Revision);
