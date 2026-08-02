using TwitchAI.Infrastructure.Twitch;
using TwitchAI.Infrastructure;

namespace TwitchAI.Tests.Infrastructure;

public sealed class TwitchChannelSelectionTests
{
    [Fact]
    public void ParseLogins_NormalizesCommaSeparatedChannels()
    {
        var channels = TwitchChannelSelection.ParseLogins(
            " Channel1, #CHANNEL2, @channel3, channel1, , ");

        Assert.Equal(["channel1", "channel2", "channel3"], channels);
    }

    [Fact]
    public void FormatLogins_UsesStableReadableSeparator()
    {
        var value = TwitchChannelSelection.FormatLogins(
            ["Channel1", " channel2 ", "CHANNEL1"]);

        Assert.Equal("channel1, channel2", value);
    }

    [Fact]
    public void ParseBroadcasterIds_PreservesIdsAndRemovesDuplicates()
    {
        var broadcasterIds = TwitchChannelSelection.ParseBroadcasterIds(
            "100, 200,100,,300");

        Assert.Equal(["100", "200", "300"], broadcasterIds);
    }

    [Fact]
    public void EventSubLimit_DiffersBetweenModerationAndReadOnlyModes()
    {
        TwitchChannelSelection.EnsureWithinEventSubLimit(60, isReadOnly: false);
        TwitchChannelSelection.EnsureWithinEventSubLimit(150, isReadOnly: true);

        Assert.Throws<InvalidOperationException>(
            () => TwitchChannelSelection.EnsureWithinEventSubLimit(
                61,
                isReadOnly: false));
        Assert.Throws<InvalidOperationException>(
            () => TwitchChannelSelection.EnsureWithinEventSubLimit(
                151,
                isReadOnly: true));
    }

    [Fact]
    public void TwitchApiOptions_PreservesLegacyPrimaryBroadcaster()
    {
        var options = new TwitchApiOptions
        {
            BroadcasterId = "legacy-channel",
        };

        Assert.Equal(["legacy-channel"], options.GetBroadcasterIds());

        options.SetBroadcasterIds(["channel-1", "channel-2", "channel-1"]);

        Assert.Equal("channel-1", options.BroadcasterId);
        Assert.Equal(["channel-1", "channel-2"], options.GetBroadcasterIds());
        Assert.True(options.ContainsBroadcaster("channel-2"));
    }
}
