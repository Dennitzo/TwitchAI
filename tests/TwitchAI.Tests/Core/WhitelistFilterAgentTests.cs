using TwitchAI.Core.Contracts;
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
        var source = new FakeWhitelistPhraseSource("kekw");
        var sut = new WhitelistFilterAgent(source);
        var text = new ChatTextNormalizer().Process(original);
        var observedAt = DateTimeOffset.UtcNow;

        var skipped = await sut.ShouldSkipAsync("channel-1", text, observedAt);

        Assert.True(skipped);
        var hit = Assert.Single(source.Hits);
        Assert.Equal(("channel-1", "kekw", observedAt), hit);
    }

    [Theory]
    [InlineData(" KEKW")]
    [InlineData("KEKW!")]
    [InlineData("KEKW KEKW")]
    public async Task ShouldSkip_DoesNotTrimOrUseSubstringMatching(string original)
    {
        var source = new FakeWhitelistPhraseSource("kekw");
        var sut = new WhitelistFilterAgent(source);

        var skipped = await sut.ShouldSkipAsync(
            "channel-1",
            new ChatTextNormalizer().Process(original),
            DateTimeOffset.UtcNow);

        Assert.False(skipped);
        Assert.Empty(source.Hits);
    }

    private sealed class FakeWhitelistPhraseSource(params string[] phrases)
        : IWhitelistPhraseSource
    {
        private readonly IReadOnlySet<string> _phrases =
            new HashSet<string>(phrases, StringComparer.Ordinal);

        public List<(string ChannelId, string Phrase, DateTimeOffset OccurredAt)> Hits { get; } = [];

        public ValueTask<IReadOnlySet<string>> GetActiveLowerPhrasesAsync(
            string channelId,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(_phrases);
        }

        public ValueTask RecordHitAsync(
            string channelId,
            string lowerPhrase,
            DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken = default)
        {
            Hits.Add((channelId, lowerPhrase, occurredAtUtc));
            return ValueTask.CompletedTask;
        }
    }
}
