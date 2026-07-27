using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class MessageAvailabilityRegistryTests
{
    private static readonly DateTimeOffset Baseline =
        new(2026, 7, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Assess_ObservedMessageIsActiveUntilSixHourDeleteWindowExpires()
    {
        var sut = new MessageAvailabilityRegistry();
        var message = Snapshot("message-1", "user-1", Baseline);
        sut.Observe(message);

        var active = sut.Assess(
            message.Identity,
            Baseline.Add(MessageAvailabilityRegistry.TwitchDeleteWindow).AddTicks(-1));
        var expired = sut.Assess(
            message.Identity,
            Baseline.Add(MessageAvailabilityRegistry.TwitchDeleteWindow));

        Assert.Equal(MessageAvailability.ObservedActive, active.Availability);
        Assert.True(active.CanExecuteTwitchAction);
        Assert.Equal(MessageAvailability.ExpiredNotDeletable, expired.Availability);
        Assert.False(expired.CanExecuteTwitchAction);
    }

    [Fact]
    public void ExactDeleteEvent_BlocksTwitchActionAndIsIdempotent()
    {
        var sut = new MessageAvailabilityRegistry();
        var message = Snapshot("message-1", "user-1", Baseline);
        sut.Observe(message);
        sut.MarkDeletedExact(
            "event-delete",
            "channel-1",
            "message-1",
            "user-1",
            Baseline.AddMinutes(1),
            Baseline.AddMinutes(1));
        var afterFirst = sut.Assess(message.Identity, Baseline.AddMinutes(2));

        sut.MarkDeletedExact(
            "event-delete",
            "channel-1",
            "message-1",
            "user-1",
            Baseline.AddMinutes(3),
            Baseline.AddMinutes(3));
        var afterRetry = sut.Assess(message.Identity, Baseline.AddMinutes(4));

        Assert.Equal(MessageAvailability.DeletedExact, afterFirst.Availability);
        Assert.False(afterFirst.CanExecuteTwitchAction);
        Assert.Equal(afterFirst.Revision, afterRetry.Revision);
    }

    [Fact]
    public void UserClear_OnlyInvalidatesOlderMessagesForThatUser()
    {
        var sut = new MessageAvailabilityRegistry();
        var oldTarget = Snapshot("old-target", "user-1", Baseline);
        var newTarget = Snapshot("new-target", "user-1", Baseline.AddMinutes(2));
        var oldOtherUser = Snapshot("old-other", "user-2", Baseline);
        sut.Observe(oldTarget);
        sut.Observe(newTarget);
        sut.Observe(oldOtherUser);

        sut.MarkUserCleared(
            "event-clear-user",
            "channel-1",
            "user-1",
            Baseline.AddMinutes(1));

        Assert.Equal(
            MessageAvailability.ClearedForUser,
            sut.Assess(oldTarget.Identity, Baseline.AddMinutes(3)).Availability);
        Assert.Equal(
            MessageAvailability.ObservedActive,
            sut.Assess(newTarget.Identity, Baseline.AddMinutes(3)).Availability);
        Assert.Equal(
            MessageAvailability.ObservedActive,
            sut.Assess(oldOtherUser.Identity, Baseline.AddMinutes(3)).Availability);
    }

    [Fact]
    public void RoomClear_OnlyInvalidatesMessagesAtOrBeforeWatermark()
    {
        var sut = new MessageAvailabilityRegistry();
        var oldMessage = Snapshot("old", "user-1", Baseline);
        var newMessage = Snapshot("new", "user-2", Baseline.AddMinutes(2));
        sut.Observe(oldMessage);
        sut.Observe(newMessage);

        sut.MarkRoomCleared(
            "event-room-clear",
            "channel-1",
            Baseline.AddMinutes(1));

        Assert.Equal(
            MessageAvailability.RoomClearedHidden,
            sut.Assess(oldMessage.Identity, Baseline.AddMinutes(3)).Availability);
        Assert.Equal(
            MessageAvailability.ObservedActive,
            sut.Assess(newMessage.Identity, Baseline.AddMinutes(3)).Availability);
    }

    [Fact]
    public void EventGap_ConservativelyBlocksOlderMessagesButNotNewOnes()
    {
        var sut = new MessageAvailabilityRegistry();
        var oldMessage = Snapshot("old", "user-1", Baseline);
        var newMessage = Snapshot("new", "user-1", Baseline.AddMinutes(2));
        sut.Observe(oldMessage);
        sut.Observe(newMessage);

        sut.MarkUnknownGap("channel-1", Baseline.AddMinutes(1));

        Assert.Equal(
            MessageAvailability.UnknownAfterEventGap,
            sut.Assess(oldMessage.Identity, Baseline.AddMinutes(3)).Availability);
        Assert.Equal(
            MessageAvailability.ObservedActive,
            sut.Assess(newMessage.Identity, Baseline.AddMinutes(3)).Availability);
    }

    [Fact]
    public void UnknownOutboundOutcome_BlocksAutomaticRetry()
    {
        var sut = new MessageAvailabilityRegistry();
        var message = Snapshot("message-1", "user-1", Baseline);
        sut.Observe(message);

        sut.MarkOutcomeUnknown("message-1", Baseline.AddMinutes(1));

        Assert.Equal(
            MessageAvailability.OutcomeUnknown,
            sut.Assess(message.Identity, Baseline.AddMinutes(2)).Availability);
    }

    [Fact]
    public void UnobservedOrTamperedIdentity_IsNeverExecutable()
    {
        var sut = new MessageAvailabilityRegistry();
        var observed = Snapshot("message-1", "user-1", Baseline);
        sut.Observe(observed);
        var tampered = observed.Identity with
        {
            TwitchUserId = "other-user",
        };

        var result = sut.Assess(tampered, Baseline.AddMinutes(1));

        Assert.Equal(MessageAvailability.UnknownAfterEventGap, result.Availability);
        Assert.False(result.CanExecuteTwitchAction);
        Assert.Equal("identity_not_observed", result.EvidenceCode);
    }

    private static ChatMessageSnapshot Snapshot(
        string messageId,
        string userId,
        DateTimeOffset receivedAt)
    {
        const string original = "message";
        var identity = ChatMessageIdentityV1.Create(
            $"event-{messageId}",
            messageId,
            "channel-1",
            userId,
            userId,
            userId,
            original,
            receivedAt);
        var text = new ChatTextNormalizer().Process(original);
        var context = new ChannelContextSnapshot(
            "channel-1",
            "509658",
            "Just Chatting",
            receivedAt,
            Revision: 1,
            IsVerified: true);
        return new ChatMessageSnapshot(identity, text, context, IngressSequence: 1);
    }
}
