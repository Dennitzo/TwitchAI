using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.Tests.Infrastructure;

public sealed class MessageAvailabilityStoreTests
{
    [Fact]
    public async Task UserClear_InvalidatesOnlyOlderMessagesForTheTargetUser()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new SqliteMessageAvailabilityStore(
            database.ConnectionFactory,
            new ChatTextNormalizer());
        var clearedAt = DateTimeOffset.UtcNow;

        await store.RecordUserClearAsync(
            new TwitchUserMessagesClearedEvent(
                "clear-user-1",
                "channel-1",
                clearedAt,
                "user-1",
                "viewer",
                "Viewer"));

        var olderTarget = await store.AssessAsync(
            "channel-1",
            "older-target",
            "user-1",
            clearedAt.AddSeconds(-1));
        var newerTarget = await store.AssessAsync(
            "channel-1",
            "newer-target",
            "user-1",
            clearedAt.AddSeconds(1));
        var olderOtherUser = await store.AssessAsync(
            "channel-1",
            "older-other",
            "user-2",
            clearedAt.AddSeconds(-1));

        Assert.Equal(MessageAvailability.ClearedForUser, olderTarget.Availability);
        Assert.Equal(MessageAvailability.ObservedActive, newerTarget.Availability);
        Assert.Equal(MessageAvailability.ObservedActive, olderOtherUser.Availability);
    }

    [Fact]
    public async Task RoomClear_InvalidatesOnlyMessagesObservedBeforeTheClear()
    {
        await using var database = await TestDatabase.CreateAsync();
        var store = new SqliteMessageAvailabilityStore(
            database.ConnectionFactory,
            new ChatTextNormalizer());
        var clearedAt = DateTimeOffset.UtcNow;

        await store.RecordRoomClearAsync(
            new TwitchRoomClearedEvent("clear-room-1", "channel-1", clearedAt));

        var older = await store.AssessAsync(
            "channel-1",
            "older",
            "user-1",
            clearedAt.AddSeconds(-1));
        var newer = await store.AssessAsync(
            "channel-1",
            "newer",
            "user-1",
            clearedAt.AddSeconds(1));

        Assert.Equal(MessageAvailability.RoomClearedHidden, older.Availability);
        Assert.Equal(MessageAvailability.ObservedActive, newer.Availability);
    }
}
