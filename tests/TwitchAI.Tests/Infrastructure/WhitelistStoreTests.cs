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
            "viewer_login",
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
            "viewer_login",
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

        var match = await sut.MatchAsync(
            "channel-1",
            "viewer-1",
            "viewer_login",
            "kekw");

        Assert.False(match.IsMatch);
    }

    [Fact]
    public async Task UserIdEntry_MatchesStableTwitchUserId()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertEntryAsync(
            database,
            "user_id",
            "123456789",
            "123456789");
        var sut = new SqliteWhitelistStore(
            database.ConnectionFactory,
            new WhitelistTextNormalizer());

        var match = await sut.MatchAsync(
            "channel-1",
            "123456789",
            "different_login",
            "not whitelisted");

        Assert.True(match.IsMatch);
        Assert.Equal(AllowlistSubjectKind.UserId, match.SubjectKind);
    }

    [Theory]
    [InlineData("MixedCaseName")]
    [InlineData("mixedcasename")]
    [InlineData("MIXEDCASENAME")]
    public async Task UserLoginEntry_MatchesInvariantLowercaseLogin(
        string chatterLogin)
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertEntryAsync(
            database,
            "user_login",
            "MixedCaseName",
            "mixedcasename");
        var sut = new SqliteWhitelistStore(
            database.ConnectionFactory,
            new WhitelistTextNormalizer());

        var match = await sut.MatchAsync(
            "channel-1",
            "unrelated-user-id",
            chatterLogin,
            "not whitelisted");

        Assert.True(match.IsMatch);
        Assert.Equal(AllowlistSubjectKind.UserLogin, match.SubjectKind);
    }

    [Fact]
    public async Task GlobalEntry_AppliesToEveryChannelAndInvalidatesEveryChannelCache()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sut = new SqliteWhitelistStore(
            database.ConnectionFactory,
            new WhitelistTextNormalizer());

        Assert.True((await sut.MatchAsync(
            "channel-1",
            "viewer-1",
            "viewer_login",
            "kekw")).IsMatch);
        Assert.True((await sut.MatchAsync(
            "channel-2",
            "viewer-2",
            "other_login",
            "kekw")).IsMatch);

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

        Assert.False((await sut.MatchAsync(
            "channel-1",
            "viewer-1",
            "viewer_login",
            "kekw")).IsMatch);
        Assert.False((await sut.MatchAsync(
            "channel-2",
            "viewer-2",
            "other_login",
            "kekw")).IsMatch);
    }

    private static async Task InsertEntryAsync(
        TestDatabase database,
        string subjectKind,
        string subjectValue,
        string normalizedValue)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO whitelist_phrases(
                broadcaster_id, subject_kind, subject_value, normalized_value,
                enabled, row_version, created_utc, updated_utc)
            VALUES(
                '*', @subject_kind, @subject_value, @normalized_value,
                1, 1, @now, @now);
            """;
        command.Parameters.AddWithValue("@subject_kind", subjectKind);
        command.Parameters.AddWithValue("@subject_value", subjectValue);
        command.Parameters.AddWithValue("@normalized_value", normalizedValue);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }
}
