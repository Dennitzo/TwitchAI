using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class WhitelistFilterAgentTests
{
    [Theory]
    [InlineData("KEKW")]
    [InlineData("kekw")]
    [InlineData("KeKw")]
    public async Task ShouldSkip_UsesExactWholeLowercaseMessage(string original)
    {
        var source = new FakeWhitelistPhraseSource(
            exactMessages: Set("kekw"));
        var sut = new WhitelistFilterAgent(source);
        var observedAt = DateTimeOffset.UtcNow;

        var skipped = await sut.ShouldSkipAsync(
            Message(original, login: "viewer"),
            observedAt);

        Assert.True(skipped);
        var hit = Assert.Single(source.Hits);
        Assert.Equal(
            (
                "channel-1",
                WhitelistEntryKind.ExactMessage,
                "kekw",
                observedAt),
            hit);
    }

    [Theory]
    [InlineData(" KEKW")]
    [InlineData("KEKW!")]
    [InlineData("KEKW KEKW")]
    public async Task ShouldSkip_DoesNotTrimOrUseSubstringMatching(string original)
    {
        var source = new FakeWhitelistPhraseSource(
            exactMessages: Set("kekw"));
        var sut = new WhitelistFilterAgent(source);

        var skipped = await sut.ShouldSkipAsync(
            Message(original, login: "viewer"),
            DateTimeOffset.UtcNow);

        Assert.False(skipped);
        Assert.Empty(source.Hits);
    }

    [Theory]
    [InlineData("TargetUser")]
    [InlineData("targetuser")]
    [InlineData("TARGETUSER")]
    public async Task ShouldSkip_UsesInvariantLowercaseTwitchLogin(string login)
    {
        var source = new FakeWhitelistPhraseSource(
            lowerUserLogins: Set("targetuser"));
        var sut = new WhitelistFilterAgent(source);
        var observedAt = DateTimeOffset.UtcNow;

        var skipped = await sut.ShouldSkipAsync(
            Message("not a whitelisted phrase", login, "Unrelated display name"),
            observedAt);

        Assert.True(skipped);
        var hit = Assert.Single(source.Hits);
        Assert.Equal(
            (
                "channel-1",
                WhitelistEntryKind.UserLogin,
                "targetuser",
                observedAt),
            hit);
    }

    [Fact]
    public async Task ShouldSkip_NeverUsesDisplayNameAsUsername()
    {
        var source = new FakeWhitelistPhraseSource(
            lowerUserLogins: Set("targetuser"));
        var sut = new WhitelistFilterAgent(source);

        var skipped = await sut.ShouldSkipAsync(
            Message(
                "not a whitelisted phrase",
                login: "different_login",
                displayName: "TargetUser"),
            DateTimeOffset.UtcNow);

        Assert.False(skipped);
        Assert.Empty(source.Hits);
    }

    [Theory]
    [InlineData("@targetuser")]
    [InlineData(" targetuser")]
    [InlineData("targetuser ")]
    [InlineData("targetuser2")]
    public async Task ShouldSkip_UsernameMatchIsExact(string login)
    {
        var source = new FakeWhitelistPhraseSource(
            lowerUserLogins: Set("targetuser"));
        var sut = new WhitelistFilterAgent(source);

        var skipped = await sut.ShouldSkipAsync(
            Message("not a whitelisted phrase", login),
            DateTimeOffset.UtcNow);

        Assert.False(skipped);
        Assert.Empty(source.Hits);
    }

    [Fact]
    public async Task ShouldSkip_UsernameTakesPrecedenceOverPhrase()
    {
        var source = new FakeWhitelistPhraseSource(
            exactMessages: Set("kekw"),
            lowerUserLogins: Set("targetuser"));
        var sut = new WhitelistFilterAgent(source);

        var skipped = await sut.ShouldSkipAsync(
            Message("KEKW", login: "TargetUser"),
            DateTimeOffset.UtcNow);

        Assert.True(skipped);
        Assert.Equal(
            WhitelistEntryKind.UserLogin,
            Assert.Single(source.Hits).Kind);
    }

    private static ChatMessageSnapshot Message(
        string originalText,
        string login,
        string displayName = "Viewer")
    {
        var observedAt = new DateTimeOffset(
            2026,
            7,
            27,
            12,
            0,
            0,
            TimeSpan.Zero);
        var identity = ChatMessageIdentityV1.Create(
            "event-1",
            "message-1",
            "channel-1",
            "user-1",
            login,
            displayName,
            originalText,
            observedAt);
        var context = new ChannelContextSnapshot(
            "channel-1",
            "509658",
            "Just Chatting",
            observedAt,
            Revision: 1,
            IsVerified: true);
        return new ChatMessageSnapshot(
            identity,
            new ChatTextNormalizer().Process(originalText),
            context,
            IngressSequence: 1);
    }

    private static HashSet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.Ordinal);

    private sealed class FakeWhitelistPhraseSource(
        IReadOnlySet<string>? exactMessages = null,
        IReadOnlySet<string>? lowerUserLogins = null)
        : IWhitelistPhraseSource
    {
        public List<(
            string ChannelId,
            WhitelistEntryKind Kind,
            string Value,
            DateTimeOffset OccurredAt)> Hits { get; } = [];

        public ValueTask<ActiveWhitelistSnapshot> GetActiveEntriesAsync(
            string channelId,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(
                new ActiveWhitelistSnapshot(
                    exactMessages ?? Set(),
                    lowerUserLogins ?? Set()));
        }

        public ValueTask RecordHitAsync(
            string channelId,
            WhitelistEntryKind kind,
            string normalizedValue,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            Hits.Add((channelId, kind, normalizedValue, occurredAtUtc));
            return ValueTask.CompletedTask;
        }
    }
}
