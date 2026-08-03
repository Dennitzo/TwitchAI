using System.Text.Json;
using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.Tests.Infrastructure;

public sealed class TwitchChatBadgeRolesTests
{
    [Fact]
    public void ParseAutoModMessageHeld_ReadsMessageAndAutoModContext()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "user_id": "user-1",
              "user_login": "viewer",
              "user_name": "Viewer",
              "message_id": "held-1",
              "message": { "text": "you are an idiot", "fragments": [] },
              "reason": "automod",
              "automod": { "category": "aggressive", "level": 3, "boundaries": [] },
              "blocked_term": null
            }
            """);

        var parsed = TwitchEventSubWebSocketClient.ParseAutoModMessageHeld(
            "event-automod-1",
            "channel-1",
            DateTimeOffset.UtcNow,
            document.RootElement);

        Assert.Equal("held-1", parsed.MessageId);
        Assert.Equal("user-1", parsed.UserId);
        Assert.Equal("you are an idiot", parsed.MessageText);
        Assert.Equal("automod", parsed.Reason);
        Assert.Equal("aggressive", parsed.Category);
        Assert.Equal(3, parsed.Level);
    }

    [Fact]
    public void ParseChatMessage_CountsStructuredTwitchEmotes()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "message_id": "message-1",
              "chatter_user_id": "user-1",
              "chatter_user_login": "viewer",
              "chatter_user_name": "Viewer",
              "message": {
                "text": "Kappa Kappa Kappa",
                "fragments": [
                  { "type": "emote", "text": "Kappa" },
                  { "type": "text", "text": " " },
                  { "type": "emote", "text": "Kappa" },
                  { "type": "text", "text": " " },
                  { "type": "emote", "text": "Kappa" }
                ]
              },
              "badges": []
            }
            """);

        var parsed = TwitchEventSubWebSocketClient.ParseChatMessage(
            "event-1",
            "channel-1",
            DateTimeOffset.UtcNow,
            document.RootElement);

        Assert.Equal(3, parsed.TwitchEmoteCount);
        Assert.True(parsed.IsTwitchEmoteOnly);
    }

    [Theory]
    [InlineData("tracked-channel", "tracked-channel", "configured-broadcaster", "configured-moderator", null)]
    [InlineData("configured-broadcaster", "tracked-channel", "configured-broadcaster", "configured-moderator", null)]
    [InlineData("configured-moderator", "tracked-channel", "configured-broadcaster", "configured-moderator", null)]
    [InlineData("source-channel", "tracked-channel", "configured-broadcaster", "configured-moderator", "source-channel")]
    public void HasAutomaticModerationBypass_RecognizesProtectedUserId(
        string twitchUserId,
        string channelId,
        string broadcasterId,
        string moderatorId,
        string? sharedChatSourceChannelId)
    {
        var identity = TwitchAI.Core.Models.ChatMessageIdentityV1.Create(
            "event-id",
            "message-id",
            channelId,
            twitchUserId,
            "login",
            "Display Name",
            "message",
            DateTimeOffset.UtcNow,
            sharedChatSourceChannelId);

        Assert.True(
            TwitchChatBadgeRoles.HasAutomaticModerationBypass(
                identity,
                new TwitchAI.Infrastructure.TwitchApiOptions
                {
                    BroadcasterId = broadcasterId,
                    ModeratorUserId = moderatorId,
                }));
    }

    [Theory]
    [InlineData("broadcaster")]
    [InlineData("MoDeRaToR")]
    [InlineData("LEAD_MODERATOR")]
    [InlineData("Vip")]
    [InlineData("BOT-BADGE")]
    public void GrantsAutomaticModerationBypass_RecognizesSupportedRole(
        string badgeSetId)
    {
        Assert.True(
            TwitchChatBadgeRoles.GrantsAutomaticModerationBypass(
                badgeSetId));
    }

    [Theory]
    [InlineData("subscriber")]
    [InlineData("mod-founder")]
    [InlineData("staff")]
    [InlineData("bot")]
    public void GrantsAutomaticModerationBypass_DoesNotGuessUnsupportedRole(
        string badgeSetId)
    {
        Assert.False(
            TwitchChatBadgeRoles.GrantsAutomaticModerationBypass(
                badgeSetId));
    }

    [Theory]
    [InlineData("moderator", true)]
    [InlineData("lead_moderator", true)]
    [InlineData("vip", false)]
    [InlineData("bot-badge", false)]
    public void ParseChatMessage_ReadsSharedChatSourceBadges(
        string sourceBadge,
        bool expectedModerator)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "message_id": "message-1",
              "chatter_user_id": "user-1",
              "chatter_user_login": "shared_user",
              "chatter_user_name": "Shared User",
              "message": { "text": "shared message" },
              "badges": [],
              "source_broadcaster_user_id": "source-channel",
              "source_message_id": "source-message-1",
              "source_badges": [
                { "set_id": "{{sourceBadge}}", "id": "1", "info": "" }
              ]
            }
            """);

        var parsed = TwitchEventSubWebSocketClient.ParseChatMessage(
            "event-1",
            "target-channel",
            DateTimeOffset.UtcNow,
            document.RootElement);

        Assert.Equal(expectedModerator, parsed.IsModerator);
        Assert.NotNull(parsed.SourceBadges);
        Assert.Equal("1", parsed.SourceBadges[sourceBadge]);
    }
}
