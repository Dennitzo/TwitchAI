using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class ChatMessageRepositoryTests
{
    [Fact]
    public async Task RoundTrip_PreservesOriginalTextAndIdentityFields()
    {
        await using var database = await TestDatabase.CreateAsync();
        var normalizer = new ChatTextNormalizer();
        var sut = new ChatMessageRepository(database.ConnectionFactory);
        var receivedAt = DateTimeOffset.UtcNow;
        const string original = "  KeKw İ  ";
        var normalized = normalizer.Normalize(original);
        var expected = new ChatMessageRecord(
            "message-1",
            "channel-1",
            "user-1",
            "viewer_login",
            "ViewerName",
            original,
            normalized,
            normalizer.Version,
            normalizer.ComputeSha256(normalized),
            "509658",
            "Just Chatting",
            receivedAt);

        Assert.True(await sut.TryAddAsync(expected));

        var actual = await sut.FindAsync("channel-1", "message-1");

        Assert.NotNull(actual);
        Assert.Equal(original, actual.MessageTextOriginal);
        Assert.Equal(normalized, actual.MessageTextNormalized);
        Assert.Equal("user-1", actual.ChatterUserId);
        Assert.Equal("message-1", actual.TwitchMessageId);
        Assert.Equal("509658", actual.CategoryId);
    }

    [Fact]
    public async Task SameTextWithDifferentMessageIds_RemainsDistinct()
    {
        await using var database = await TestDatabase.CreateAsync();
        var normalizer = new ChatTextNormalizer();
        var sut = new ChatMessageRepository(database.ConnectionFactory);
        var first = CreateMessage(normalizer, "message-1", "user-1", "hello");
        var second = CreateMessage(normalizer, "message-2", "user-2", "hello");

        Assert.True(await sut.TryAddAsync(first));
        Assert.True(await sut.TryAddAsync(second));
        Assert.Equal("user-1", (await sut.FindAsync("channel-1", "message-1"))?.ChatterUserId);
        Assert.Equal("user-2", (await sut.FindAsync("channel-1", "message-2"))?.ChatterUserId);
    }

    [Fact]
    public async Task ReusedMessageId_DoesNotReplaceOriginalIdentity()
    {
        await using var database = await TestDatabase.CreateAsync();
        var normalizer = new ChatTextNormalizer();
        var sut = new ChatMessageRepository(database.ConnectionFactory);

        Assert.True(await sut.TryAddAsync(
            CreateMessage(normalizer, "message-1", "user-1", "hello")));
        Assert.False(await sut.TryAddAsync(
            CreateMessage(normalizer, "message-1", "attacker", "different")));

        var stored = await sut.FindAsync("channel-1", "message-1");
        Assert.NotNull(stored);
        Assert.Equal("user-1", stored.ChatterUserId);
        Assert.Equal("hello", stored.MessageTextOriginal);
    }

    private static ChatMessageRecord CreateMessage(
        ChatTextNormalizer normalizer,
        string messageId,
        string userId,
        string original)
    {
        var normalized = normalizer.Normalize(original);
        return new ChatMessageRecord(
            messageId,
            "channel-1",
            userId,
            userId,
            userId,
            original,
            normalized,
            normalizer.Version,
            normalizer.ComputeSha256(normalized),
            "509658",
            "Just Chatting",
            DateTimeOffset.UtcNow);
    }
}
