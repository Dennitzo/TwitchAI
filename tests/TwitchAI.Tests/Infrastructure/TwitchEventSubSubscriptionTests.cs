using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.Tests.Infrastructure;

public sealed class TwitchEventSubSubscriptionTests
{
    [Fact]
    public void BuildSubscriptionRequests_ModerationModeCreatesFourChatSubscriptionsPerChannel()
    {
        var channel = new TwitchEventSubChannel("channel-1", "reader-1")
        {
            BroadcasterIds = ["channel-1", "channel-2"],
        };

        var requests = TwitchEventSubWebSocketClient.BuildSubscriptionRequests(
            channel,
            "session-1",
            isReadOnly: false);

        Assert.Equal(8, requests.Count);
        Assert.Equal(
            ["channel-1", "channel-2"],
            requests
                .Select(request => request.Condition["broadcaster_user_id"])
                .Distinct(StringComparer.Ordinal));
        Assert.All(requests, request => Assert.Equal("session-1", request.SessionId));
        Assert.All(
            requests.Where(request => request.Type.StartsWith("channel.chat", StringComparison.Ordinal)),
            request => Assert.Equal("reader-1", request.Condition["user_id"]));
        Assert.Equal(
            2,
            requests.Count(request => request.Type == "channel.chat.message_delete"));
        Assert.DoesNotContain(requests, request => request.Type == "channel.update");
    }

    [Fact]
    public void BuildSubscriptionRequests_ReadOnlyCreatesOnlyCostFreeChatMessages()
    {
        var channel = new TwitchEventSubChannel("channel-1", "reader-1")
        {
            BroadcasterIds = ["channel-1", "channel-2", "channel-3"],
        };

        var requests = TwitchEventSubWebSocketClient.BuildSubscriptionRequests(
            channel,
            "session-read-only",
            isReadOnly: true);

        Assert.Equal(3, requests.Count);
        Assert.All(
            requests,
            request => Assert.Equal("channel.chat.message", request.Type));
        Assert.Equal(
            3,
            requests.Count(request => request.Type == "channel.chat.message"));
        Assert.DoesNotContain(requests, request => request.Type == "channel.update");
    }

    [Fact]
    public void BuildSubscriptionRequests_ElevenReadOnlyChannelsStayBelowCostLimit()
    {
        var broadcasterIds = Enumerable.Range(1, 11)
            .Select(index => $"channel-{index}")
            .ToArray();
        var channel = new TwitchEventSubChannel(broadcasterIds[0], "reader-1")
        {
            BroadcasterIds = broadcasterIds,
        };

        var requests = TwitchEventSubWebSocketClient.BuildSubscriptionRequests(
            channel,
            "session-read-only",
            isReadOnly: true);

        Assert.Equal(11, requests.Count);
        Assert.All(
            requests,
            request => Assert.Equal("channel.chat.message", request.Type));
    }
}
