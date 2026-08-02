using TwitchAI.Infrastructure.AI;

namespace TwitchAI.Tests.Infrastructure;

public sealed class LocalAiExecutionGateTests
{
    [Fact]
    public async Task AllowsFourConcurrentLeasesAndBlocksFifth()
    {
        using var gate = new LocalAiExecutionGate();
        var leases = new List<IAsyncDisposable>();
        for (var index = 0; index < LocalAiExecutionGate.MaximumConcurrency; index++)
        {
            leases.Add(await gate.AcquireAsync());
        }

        var fifth = gate.AcquireAsync().AsTask();
        Assert.False(fifth.IsCompleted);

        await leases[0].DisposeAsync();
        leases[0] = await fifth;
        foreach (var lease in leases)
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task ManualReviewOvertakesWaitingNormalRunsWithoutPreemption()
    {
        using var gate = new LocalAiExecutionGate();
        var active = new List<IAsyncDisposable>();
        for (var index = 0; index < LocalAiExecutionGate.MaximumConcurrency; index++)
        {
            active.Add(await gate.AcquireAsync());
        }

        var normal = gate.AcquireAsync(LocalAiRunPriority.Normal).AsTask();
        var review = gate.AcquireAsync(LocalAiRunPriority.ManualReview).AsTask();
        await active[0].DisposeAsync();

        var reviewLease = await review;
        Assert.False(normal.IsCompleted);

        await reviewLease.DisposeAsync();
        var normalLease = await normal;
        await normalLease.DisposeAsync();
        foreach (var lease in active.Skip(1))
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task CanceledWaiterDoesNotConsumeReleasedSlot()
    {
        using var gate = new LocalAiExecutionGate();
        var active = new List<IAsyncDisposable>();
        for (var index = 0; index < LocalAiExecutionGate.MaximumConcurrency; index++)
        {
            active.Add(await gate.AcquireAsync());
        }

        using var cancellation = new CancellationTokenSource();
        var canceled = gate.AcquireAsync(
            LocalAiRunPriority.ManualReview,
            schedulingKey: "channel-1",
            cancellation.Token).AsTask();
        var next = gate.AcquireAsync(schedulingKey: "channel-1").AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        await active[0].DisposeAsync();
        var nextLease = await next;
        await nextLease.DisposeAsync();
        foreach (var lease in active.Skip(1))
        {
            await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task SameChannelWaitsWhileDifferentChannelUsesAnotherSlot()
    {
        using var gate = new LocalAiExecutionGate();
        var first = await gate.AcquireAsync(schedulingKey: "channel-1");

        var sameChannel = gate.AcquireAsync(schedulingKey: "channel-1").AsTask();
        var otherChannel = gate.AcquireAsync(schedulingKey: "channel-2").AsTask();

        var otherLease = await otherChannel;
        Assert.False(sameChannel.IsCompleted);

        await first.DisposeAsync();
        var sameLease = await sameChannel;
        await sameLease.DisposeAsync();
        await otherLease.DisposeAsync();
    }

    [Fact]
    public async Task BlockedReviewDoesNotPreventExecutableReviewOrNormalRun()
    {
        using var gate = new LocalAiExecutionGate();
        var channelOne = await gate.AcquireAsync(schedulingKey: "channel-1");
        var fillers = new[]
        {
            await gate.AcquireAsync(schedulingKey: "channel-2"),
            await gate.AcquireAsync(schedulingKey: "channel-3"),
            await gate.AcquireAsync(schedulingKey: "channel-4"),
        };
        var blockedReview = gate.AcquireAsync(
            LocalAiRunPriority.ManualReview,
            "channel-1").AsTask();
        var executableReview = gate.AcquireAsync(
            LocalAiRunPriority.ManualReview,
            "channel-5").AsTask();

        await fillers[0].DisposeAsync();
        var reviewLease = await executableReview;
        Assert.False(blockedReview.IsCompleted);

        await reviewLease.DisposeAsync();
        await channelOne.DisposeAsync();
        var blockedLease = await blockedReview;
        await blockedLease.DisposeAsync();
        await fillers[1].DisposeAsync();
        await fillers[2].DisposeAsync();
    }
}
