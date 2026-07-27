using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ModerationProposalEligibilityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CanBeReviewed_ActionWithoutViolationCategory_IsRejected(
        string? ruleCode)
    {
        var decision = new ValidatedModerationDecision(
            "m1",
            ModerationActionKind.Timeout,
            600,
            PolicySetCode.General,
            "Testgrund.",
            [],
            0.9,
            ruleCode);

        Assert.False(ModerationProposalEligibility.CanBeReviewed(decision));
    }

    [Fact]
    public void CanBeReviewed_CompleteAction_IsAccepted()
    {
        var decision = new ValidatedModerationDecision(
            "m1",
            ModerationActionKind.Timeout,
            600,
            PolicySetCode.General,
            "Testgrund.",
            [],
            0.9,
            ModerationRuleCodes.GeneralPhrase);

        Assert.True(ModerationProposalEligibility.CanBeReviewed(decision));
    }

    [Fact]
    public void CanBeReviewed_PersistedCaseWithoutViolationCategory_IsRejected()
    {
        var receivedAt = new DateTimeOffset(
            2026,
            7,
            27,
            10,
            0,
            0,
            TimeSpan.Zero);
        var identity = ChatMessageIdentityV1.Create(
            "event-1",
            "message-1",
            "channel-1",
            "user-1",
            "user",
            "User",
            "Testnachricht",
            receivedAt);
        var message = new ChatMessageSnapshot(
            identity,
            new ChatTextNormalizer().Process(identity.OriginalText),
            new ChannelContextSnapshot(
                "channel-1",
                "509658",
                "Just Chatting",
                receivedAt,
                1,
                true),
            IngressSequence: 1);
        var moderationCase = new ModerationCase(
            1,
            1,
            message,
            ModerationActionKind.Delete,
            null,
            string.Empty,
            PolicySetCode.General,
            [],
            0.9,
            "revision-1",
            ModerationCaseStatus.Pending,
            receivedAt)
        {
            RuleCode = null,
        };

        Assert.False(ModerationProposalEligibility.CanBeReviewed(moderationCase));
    }
}
