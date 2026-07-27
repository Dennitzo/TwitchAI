using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class CoreWhitelistPhraseSourceTests
{
    [Fact]
    public async Task ActiveSnapshotAndHitRecordingKeepMessageAndLoginKindsSeparate()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO whitelist_phrases(
                    broadcaster_id, subject_kind, subject_value,
                    normalized_value, enabled, row_version,
                    created_utc, updated_utc)
                VALUES(
                    @global_scope, 'user_login', 'KEKW',
                    'kekw', 1, 1,
                    strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                    strftime('%Y-%m-%dT%H:%M:%fZ','now'));

                INSERT INTO whitelist_phrases(
                    broadcaster_id, subject_kind, subject_value,
                    normalized_value, enabled, row_version,
                    created_utc, updated_utc)
                VALUES(
                    @global_scope, 'user_login', 'disabled_user',
                    'disabled_user', 0, 1,
                    strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                    strftime('%Y-%m-%dT%H:%M:%fZ','now'));
                """;
            command.Parameters.AddWithValue(
                "@global_scope",
                GlobalPolicyStorage.BroadcasterId);
            await command.ExecuteNonQueryAsync();
        }

        var sut = new SqliteWhitelistPhraseSource(
            database.ConnectionFactory);
        var snapshot = await sut.GetActiveEntriesAsync("channel-1");

        Assert.Contains("kekw", snapshot.ExactLowerMessages);
        Assert.Contains("kekw", snapshot.LowerUserLogins);
        Assert.DoesNotContain(
            "disabled_user",
            snapshot.LowerUserLogins);

        var occurredAt = new DateTimeOffset(
            2026,
            7,
            27,
            15,
            30,
            0,
            TimeSpan.Zero);
        await sut.RecordHitAsync(
            "channel-1",
            WhitelistEntryKind.UserLogin,
            "kekw",
            occurredAt);

        await using var verify = await database.OpenConnectionAsync();
        await using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText =
            """
            SELECT subject_kind, hit_count, last_hit_utc
            FROM whitelist_phrases
            WHERE broadcaster_id = @global_scope
              AND normalized_value = 'kekw'
            ORDER BY subject_kind;
            """;
        verifyCommand.Parameters.AddWithValue(
            "@global_scope",
            GlobalPolicyStorage.BroadcasterId);
        await using var reader = await verifyCommand.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync());
        Assert.Equal("exact_message", reader.GetString(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.True(reader.IsDBNull(2));

        Assert.True(await reader.ReadAsync());
        Assert.Equal("user_login", reader.GetString(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(
            occurredAt,
            SqliteData.ParseDateTimeOffset(reader.GetString(2)));
        Assert.False(await reader.ReadAsync());
    }
}
