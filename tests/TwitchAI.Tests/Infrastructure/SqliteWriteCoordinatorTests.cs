using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class SqliteWriteCoordinatorTests
{
    [Fact]
    public async Task AcquireAsync_SerializesIndependentWriteFlows()
    {
        using var coordinator = new SqliteWriteCoordinator();
        await using var firstLease = await coordinator.AcquireAsync();
        var attempting = NewSignal();
        var entered = NewSignal();
        var releaseSecond = NewSignal();

        Task secondWriter;
        using (ExecutionContext.SuppressFlow())
        {
            secondWriter = Task.Run(
                async () =>
                {
                    attempting.TrySetResult();
                    await using var secondLease = await coordinator.AcquireAsync();
                    entered.TrySetResult();
                    await releaseSecond.Task;
                });
        }

        await attempting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(entered.Task.IsCompleted);

        await firstLease.DisposeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseSecond.TrySetResult();
        await secondWriter.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AcquireAsync_CancelledWaiterDoesNotConsumePermit()
    {
        using var coordinator = new SqliteWriteCoordinator();
        await using var firstLease = await coordinator.AcquireAsync();
        using var cancellation = new CancellationTokenSource();
        var attempting = NewSignal();

        Task cancelledWriter;
        using (ExecutionContext.SuppressFlow())
        {
            cancelledWriter = Task.Run(
                async () =>
                {
                    attempting.TrySetResult();
                    await using var ignored =
                        await coordinator.AcquireAsync(cancellation.Token);
                });
        }

        await attempting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledWriter);

        await firstLease.DisposeAsync();
        await using var nextLease = await coordinator
            .AcquireAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task AcquireAsync_RecursiveLeaseFailsInsteadOfDeadlocking()
    {
        using var coordinator = new SqliteWriteCoordinator();
        await using var firstLease = await coordinator.AcquireAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () =>
            {
                await using var ignored =
                    await coordinator.AcquireAsync(timeout.Token);
            });

        Assert.Contains("recursively", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOnlyRepositoryCall_DoesNotWaitForWriteLease()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var coordinator = new SqliteWriteCoordinator();
        var repository = new ChannelStateRepository(
            database.ConnectionFactory,
            coordinator);
        await using var writeLease = await coordinator.AcquireAsync();

        var result = await repository
            .GetAsync("channel-read")
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(result);
    }

    [Fact]
    public async Task RepositoryWrite_WaitsForInjectedCoordinatorLease()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var coordinator = new SqliteWriteCoordinator();
        var repository = new ChannelStateRepository(
            database.ConnectionFactory,
            coordinator);
        await using var heldLease = await coordinator.AcquireAsync();
        var attempting = NewSignal();
        var state = new ChannelStateRecord(
            "channel-write",
            "channel",
            "Channel",
            "moderator",
            "509658",
            "Just Chatting",
            DateTimeOffset.UtcNow);

        Task writer;
        using (ExecutionContext.SuppressFlow())
        {
            writer = Task.Run(
                async () =>
                {
                    attempting.TrySetResult();
                    await repository.UpsertAsync(state);
                });
        }

        await attempting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(writer.IsCompleted);

        await heldLease.DisposeAsync();
        await writer.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(
            state,
            await repository.GetAsync(state.BroadcasterId));
    }

    private static TaskCompletionSource NewSignal()
    {
        return new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
