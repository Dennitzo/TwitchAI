using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class EventSubDedupeStoreTests
{
    [Fact]
    public async Task TryMarkProcessed_EventEnvelopeIdIsIdempotencyKey()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = new EventSubDedupeStore(database.ConnectionFactory);
        var now = DateTimeOffset.UtcNow;

        var first = await sut.TryMarkProcessedAsync(
            "event-1",
            "channel.chat.message",
            now);
        var duplicate = await sut.TryMarkProcessedAsync(
            "event-1",
            "channel.chat.message_delete",
            now.AddSeconds(1));

        Assert.True(first);
        Assert.False(duplicate);
    }

    [Fact]
    public async Task DeleteOlderThan_OnlyRemovesEventsBeforeCutoff()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = new EventSubDedupeStore(database.ConnectionFactory);
        var cutoff = DateTimeOffset.UtcNow;
        await sut.TryMarkProcessedAsync(
            "old-event",
            "channel.chat.message",
            cutoff.AddSeconds(-1));
        await sut.TryMarkProcessedAsync(
            "new-event",
            "channel.chat.message",
            cutoff);

        var deleted = await sut.DeleteOlderThanAsync(cutoff);
        var oldCanBeAddedAgain = await sut.TryMarkProcessedAsync(
            "old-event",
            "channel.chat.message",
            cutoff);
        var newIsStillDuplicate = await sut.TryMarkProcessedAsync(
            "new-event",
            "channel.chat.message",
            cutoff);

        Assert.Equal(1, deleted);
        Assert.True(oldCanBeAddedAgain);
        Assert.False(newIsStillDuplicate);
    }

    [Fact]
    public async Task ReleaseProcessed_AllowsSameEventToBeRetried()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = new EventSubDedupeStore(database.ConnectionFactory);
        var now = DateTimeOffset.UtcNow;

        Assert.True(await sut.TryMarkProcessedAsync(
            "retry-event",
            "channel.chat.message",
            now));
        Assert.False(await sut.TryMarkProcessedAsync(
            "retry-event",
            "channel.chat.message",
            now));

        await sut.ReleaseProcessedAsync("retry-event");

        Assert.True(await sut.TryMarkProcessedAsync(
            "retry-event",
            "channel.chat.message",
            now.AddSeconds(1)));
        Assert.False(await sut.TryMarkProcessedAsync(
            "retry-event",
            "channel.chat.message",
            now.AddSeconds(1)));
    }
}
