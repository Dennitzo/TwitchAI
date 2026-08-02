using System.Reflection;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Pipeline;

namespace TwitchAI.App.Tests.Services;

public sealed class ModerationUiBridgeAvailabilityTests
{
    [Fact]
    public void RoomClear_OnlyUpdatesCasesFromMatchingChannel()
    {
        var state = new AppState();
        var firstChannelCase = CreateCase(1, "channel-1", "message-1");
        var secondChannelCase = CreateCase(2, "channel-2", "message-2");
        state.ModerationCases.Add(firstChannelCase);
        state.ModerationCases.Add(secondChannelCase);
        var originalAvailability = secondChannelCase.AvailabilityLabel;

        using var bridge = new ModerationUiBridge(
            state,
            new StubCoordinator(),
            new LocalAiRunActivityMonitor(),
            new InfrastructureOptions());
        var availabilityEvent = new ModerationPipelineEventArgs(
            ModerationPipelineEventKind.AvailabilityChanged,
            "room_cleared",
            "Room cleared.",
            occurredAtUtc: new DateTimeOffset(
                2026,
                8,
                1,
                12,
                0,
                0,
                TimeSpan.Zero),
            channelId: "channel-1");

        InvokeApplyAvailability(bridge, availabilityEvent);

        Assert.Equal("channel-1", availabilityEvent.ChannelId);
        Assert.Equal("Durch Chat-Leerung verborgen", firstChannelCase.AvailabilityLabel);
        Assert.Equal(originalAvailability, secondChannelCase.AvailabilityLabel);
    }

    private static ModerationCaseItem CreateCase(
        long caseId,
        string channelId,
        string messageId) =>
        new()
        {
            CaseId = caseId,
            ChannelId = channelId,
            UserName = "viewer",
            OriginalMessage = "test",
            CategoryName = "Just Chatting",
            CategoryId = "509658",
            ReceivedAt = new DateTimeOffset(
                2026,
                8,
                1,
                10,
                0,
                0,
                TimeSpan.Zero),
            MessageId = messageId,
            UserId = "user-1",
            IdentityFingerprint = $"fingerprint-{caseId}",
            RowVersion = 1,
            Status = "Pending",
        };

    private static void InvokeApplyAvailability(
        ModerationUiBridge bridge,
        ModerationPipelineEventArgs eventArgs)
    {
        var method = typeof(ModerationUiBridge).GetMethod(
            "ApplyAvailability",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(bridge, [eventArgs]);
    }

    private sealed class StubCoordinator : IModerationCoordinator
    {
        public event EventHandler<ModerationPipelineEventArgs>? EventReceived
        {
            add { }
            remove { }
        }

        public bool IsRunning => false;

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ReanalyzeCaseAsync(
            long caseId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
