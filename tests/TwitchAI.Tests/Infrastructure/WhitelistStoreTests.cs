using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class WhitelistStoreTests
{
    [Theory]
    [InlineData("KEKW")]
    [InlineData("kekw")]
    [InlineData("KeKw")]
    public async Task ExactLowercaseWhitelist_SkipsCaseVariants(string message)
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = new SqliteWhitelistStore(
            database.ConnectionFactory,
            new WhitelistTextNormalizer());

        var match = await sut.MatchAsync(
            "channel-1",
            "viewer-1",
            message.ToLowerInvariant());

        Assert.True(match.IsMatch);
        Assert.Equal(AllowlistSubjectKind.ExactMessage, match.SubjectKind);
    }

    [Theory]
    [InlineData(" KEKW")]
    [InlineData("KEKW!")]
    [InlineData("KEKW KEKW")]
    public async Task ExactLowercaseWhitelist_DoesNotTrimOrMatchSubstrings(string message)
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = new SqliteWhitelistStore(
            database.ConnectionFactory,
            new WhitelistTextNormalizer());

        var match = await sut.MatchAsync(
            "channel-1",
            "viewer-1",
            message.ToLowerInvariant());

        Assert.False(match.IsMatch);
        Assert.Null(match.AllowlistId);
    }

    [Fact]
    public async Task DisabledEntry_DoesNotMatch()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "UPDATE whitelist_phrases SET enabled = 0 WHERE normalized_value = 'kekw';";
            await command.ExecuteNonQueryAsync();
        }

        var sut = new SqliteWhitelistStore(
            database.ConnectionFactory,
            new WhitelistTextNormalizer());

        var match = await sut.MatchAsync("channel-1", "viewer-1", "kekw");

        Assert.False(match.IsMatch);
    }

    [Fact]
    public async Task GlobalEntry_AppliesToEveryChannelAndInvalidatesEveryChannelCache()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = new SqliteWhitelistStore(
            database.ConnectionFactory,
            new WhitelistTextNormalizer());

        Assert.True((await sut.MatchAsync("channel-1", "viewer-1", "kekw")).IsMatch);
        Assert.True((await sut.MatchAsync("channel-2", "viewer-2", "kekw")).IsMatch);

        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE whitelist_phrases
                SET enabled = 0
                WHERE broadcaster_id = '*' AND normalized_value = 'kekw';
                """;
            await command.ExecuteNonQueryAsync();
        }

        sut.Invalidate(
            new PolicyChangeSet(
                GlobalPolicyStorage.BroadcasterId,
                ScopeKind: null,
                CategoryId: null,
                AllowlistChanged: true,
                NewRevision: null));

        Assert.False((await sut.MatchAsync("channel-1", "viewer-1", "kekw")).IsMatch);
        Assert.False((await sut.MatchAsync("channel-2", "viewer-2", "kekw")).IsMatch);
    }
}
