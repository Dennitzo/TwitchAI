using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class ChatMessageIdentityFingerprintTests
{
    [Fact]
    public void Create_ProducesStableFingerprintRegardlessOfBadgeInsertionOrder()
    {
        var first = Create(
            "message-1",
            "user-1",
            new Dictionary<string, string>
            {
                ["subscriber"] = "12",
                ["moderator"] = "1",
            });
        var second = Create(
            "message-1",
            "user-1",
            new Dictionary<string, string>
            {
                ["moderator"] = "1",
                ["subscriber"] = "12",
            });

        Assert.Equal(first.IdentityFingerprint, second.IdentityFingerprint);
        Assert.True(ChatMessageIdentityFingerprint.IsValid(first));
    }

    [Theory]
    [InlineData("message-2", "user-1")]
    [InlineData("message-1", "user-2")]
    public void Create_DifferentAuthoritativeIdsProduceDifferentFingerprints(
        string messageId,
        string userId)
    {
        var baseline = Create("message-1", "user-1");
        var different = Create(messageId, userId);

        Assert.NotEqual(baseline.IdentityFingerprint, different.IdentityFingerprint);
    }

    [Fact]
    public void IsValid_DetectsTamperingWithIdentityBoundFields()
    {
        var original = Create("message-1", "user-1");
        var tamperedUser = original with
        {
            TwitchUserId = "attacker",
        };
        var tamperedText = original with
        {
            OriginalText = "other message",
        };

        Assert.False(ChatMessageIdentityFingerprint.IsValid(tamperedUser));
        Assert.False(ChatMessageIdentityFingerprint.IsValid(tamperedText));
    }

    [Fact]
    public void Create_CopiesBadgeDictionaryBeforeFingerprinting()
    {
        var badges = new Dictionary<string, string>
        {
            ["subscriber"] = "12",
        };
        var identity = Create("message-1", "user-1", badges);

        badges["moderator"] = "1";

        Assert.False(identity.Badges.ContainsKey("moderator"));
        Assert.True(ChatMessageIdentityFingerprint.IsValid(identity));
    }

    private static ChatMessageIdentityV1 Create(
        string messageId,
        string userId,
        IReadOnlyDictionary<string, string>? badges = null)
    {
        return ChatMessageIdentityV1.Create(
            "event-1",
            messageId,
            "channel-1",
            userId,
            "viewer",
            "Viewer",
            "same message",
            new DateTimeOffset(2026, 7, 26, 10, 0, 0, TimeSpan.Zero),
            badges: badges);
    }
}
