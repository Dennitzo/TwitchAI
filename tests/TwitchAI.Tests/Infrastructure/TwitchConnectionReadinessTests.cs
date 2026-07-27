using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.Tests.Infrastructure;

public sealed class TwitchConnectionReadinessTests
{
    [Fact]
    public async Task FullyReady_RequiresSessionAndEventSub()
    {
        var readiness = new TwitchConnectionReadiness();

        Assert.False(readiness.IsSessionReady);
        Assert.False(readiness.IsEventSubReady);
        Assert.False(readiness.IsFullyReady);

        readiness.SetSessionReady(true, "Twitch verbunden.");
        Assert.True(readiness.IsSessionReady);
        Assert.False(readiness.IsFullyReady);

        var wait = readiness.WaitForEventSubReadyAsync();
        Assert.False(wait.IsCompleted);

        readiness.SetEventSubReady("EventSub aktiv.");
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(readiness.IsFullyReady);
    }

    [Fact]
    public async Task Disconnect_RearmsReadinessWait()
    {
        var readiness = new TwitchConnectionReadiness();
        readiness.SetSessionReady(true, "Twitch verbunden.");
        readiness.SetEventSubReady("EventSub aktiv.");
        await readiness.WaitForEventSubReadyAsync();

        readiness.SetEventSubConnecting(
            isReconnect: true,
            "EventSub wird neu verbunden.");

        var reconnectWait = readiness.WaitForEventSubReadyAsync();
        Assert.False(reconnectWait.IsCompleted);

        readiness.SetEventSubReady("EventSub wieder aktiv.");
        await reconnectWait.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(readiness.IsFullyReady);
    }
}
