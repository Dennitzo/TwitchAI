using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class DatabaseEditorTests
{
    [Fact]
    public async Task Apply_RejectsProtectedTables()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var editor = CreateEditor(database);
        var change = new StagedDatabaseChange(
            "moderation_cases",
            StagedDatabaseOperation.Insert,
            null,
            null,
            new Dictionary<string, object?>
            {
                ["status"] = "pending",
            });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => editor.ApplyAsync([change], "Protected table test"));
    }

    [Fact]
    public async Task Apply_TreatsSqlMetacharactersAsData()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var editor = CreateEditor(database);
        const string payload = "kekw'); DROP TABLE twitch_categories; --";

        var result = await editor.ApplyAsync(
        [
            NewExactMessageWhitelist(payload),
        ],
        "SQL parameterization test");

        var applied = Assert.Single(result.Changes);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT subject_value
            FROM whitelist_phrases
            WHERE allowlist_id = @id;
            """;
        command.Parameters.AddWithValue("@id", applied.EntityId);
        var stored = await command.ExecuteScalarAsync();

        Assert.Equal(payload, stored);
        Assert.Equal(
            3L,
            Convert.ToInt64(
                await ScalarAsync(connection, "SELECT COUNT(*) FROM twitch_categories;"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Apply_WhenLaterChangeFails_RollsBackWholeTransaction()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var editor = CreateEditor(database);
        var protectedChange = new StagedDatabaseChange(
            "schema_migrations",
            StagedDatabaseOperation.Insert,
            null,
            null,
            new Dictionary<string, object?>
            {
                ["version"] = 999L,
            });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => editor.ApplyAsync(
            [
                NewExactMessageWhitelist("rollback-fixture"),
                protectedChange,
            ],
            "Atomic rollback test"));

        await using var connection = await database.OpenConnectionAsync();
        var count = await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM whitelist_phrases WHERE broadcaster_id <> '*';");
        Assert.Equal(0L, Convert.ToInt64(count, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Apply_UsesOptimisticConcurrencyAndWritesAudit()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var editor = CreateEditor(database);
        var insert = await editor.ApplyAsync(
        [
            NewExactMessageWhitelist("concurrency-fixture"),
        ],
        "Insert concurrency fixture");
        var row = Assert.Single(insert.Changes);
        var update = new StagedDatabaseChange(
            "whitelist_phrases",
            StagedDatabaseOperation.Update,
            row.EntityId,
            ExpectedRowVersion: 1,
            Values: new Dictionary<string, object?>
            {
                ["enabled"] = false,
            });

        var updated = await editor.ApplyAsync([update], "Disable exact whitelist");

        Assert.Equal(2, Assert.Single(updated.Changes).RowVersion);
        await Assert.ThrowsAsync<DatabaseConcurrencyException>(
            () => editor.ApplyAsync([update], "Stale row version test"));

        await using var connection = await database.OpenConnectionAsync();
        var auditCount = await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM configuration_audit;");
        Assert.Equal(2L, Convert.ToInt64(
            auditCount,
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(
            "Insert concurrency fixture",
            await ScalarAsync(
                connection,
                "SELECT change_reason FROM configuration_audit ORDER BY change_id LIMIT 1;"));
    }

    [Fact]
    public async Task Apply_WhitelistPreservesOriginalAndDerivesExactLowercase()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var editor = CreateEditor(database);

        var result = await editor.ApplyAsync(
        [
            NewExactMessageWhitelist(" KEKW "),
        ],
        "Exact whitespace semantics test");
        var row = Assert.Single(result.Changes);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT subject_value, normalized_value
            FROM whitelist_phrases
            WHERE allowlist_id = @id;
            """;
        command.Parameters.AddWithValue("@id", row.EntityId);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(" KEKW ", reader.GetString(0));
        Assert.Equal(" kekw ", reader.GetString(1));
    }

    [Fact]
    public async Task Apply_UserLoginWhitelistPreservesOriginalAndDerivesLowercase()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var editor = CreateEditor(database);

        var result = await editor.ApplyAsync(
        [
            new StagedDatabaseChange(
                "whitelist_phrases",
                StagedDatabaseOperation.Insert,
                null,
                null,
                new Dictionary<string, object?>
                {
                    ["broadcaster_id"] = GlobalPolicyStorage.BroadcasterId,
                    ["subject_kind"] = "user_login",
                    ["subject_value"] = "MixedCase_Login",
                    ["enabled"] = true,
                }),
        ],
        "User login normalization test");
        var row = Assert.Single(result.Changes);

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT subject_kind, subject_value, normalized_value
            FROM whitelist_phrases
            WHERE allowlist_id = @id;
            """;
        command.Parameters.AddWithValue("@id", row.EntityId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("user_login", reader.GetString(0));
        Assert.Equal("MixedCase_Login", reader.GetString(1));
        Assert.Equal("mixedcase_login", reader.GetString(2));
    }

    [Fact]
    public async Task UniversalEditor_UserLoginUpdateRefreshesDerivedLowercase()
    {
        await using var database = await TestDatabase.CreateAsync();
        var editor = new UniversalDatabaseEditor(
            database.ConnectionFactory,
            new DatabaseCatalog(),
            []);
        await editor.SaveRowAsync(
            "whitelist_phrases",
            "allowlist_id",
            primaryKeyValue: null,
            new Dictionary<string, object?>
            {
                ["broadcaster_id"] = GlobalPolicyStorage.BroadcasterId,
                ["subject_kind"] = "user_login",
                ["subject_value"] = "First_Login",
                ["enabled"] = 1L,
            });

        await using var connection = await database.OpenConnectionAsync();
        var allowlistId = Convert.ToInt64(
            await ScalarAsync(
                connection,
                """
                SELECT allowlist_id
                FROM whitelist_phrases
                WHERE subject_kind = 'user_login'
                  AND normalized_value = 'first_login';
                """),
            System.Globalization.CultureInfo.InvariantCulture);

        await editor.SaveRowAsync(
            "whitelist_phrases",
            "allowlist_id",
            allowlistId,
            new Dictionary<string, object?>
            {
                ["subject_value"] = "Changed_Login",
            });

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT subject_value, normalized_value
            FROM whitelist_phrases
            WHERE allowlist_id = @id;
            """;
        command.Parameters.AddWithValue("@id", allowlistId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("Changed_Login", reader.GetString(0));
        Assert.Equal("changed_login", reader.GetString(1));
    }

    [Fact]
    public async Task Apply_ProtectsMandatoryPolicySetsAndBaselineRules()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var editor = CreateEditor(database);
        await using var connection = await database.OpenConnectionAsync();
        var generalBaseline = await ReadIdentityAsync(
            connection,
            """
            SELECT r.rule_id, r.row_version
            FROM blacklist_rules r
            INNER JOIN policy_sets s ON s.scope_id = r.scope_id
            WHERE s.broadcaster_id = '*'
              AND s.set_code = 'general'
              AND r.rule_code = 'G_OTHER_ABUSE'
              AND r.archived_utc IS NULL;
            """);
        var backseatBaseline = await ReadIdentityAsync(
            connection,
            """
            SELECT r.rule_id, r.row_version
            FROM blacklist_rules r
            INNER JOIN policy_sets s ON s.scope_id = r.scope_id
            WHERE s.broadcaster_id = '*'
              AND s.set_code = 'backseatgaming'
              AND r.rule_code = 'BACKSEATGAMING'
              AND r.archived_utc IS NULL;
            """);
        var backseatPolicy = await ReadIdentityAsync(
            connection,
            """
            SELECT scope_id, row_version
            FROM policy_sets
            WHERE broadcaster_id = '*'
              AND set_code = 'backseatgaming'
              AND archived_utc IS NULL;
            """);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => editor.ApplyAsync(
            [
                new StagedDatabaseChange(
                    "blacklist_rules",
                    StagedDatabaseOperation.Update,
                    generalBaseline.Id,
                    generalBaseline.RowVersion,
                    new Dictionary<string, object?>
                    {
                        ["enabled"] = false,
                    }),
            ],
            "Baseline disable protection"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => editor.ApplyAsync(
            [
                new StagedDatabaseChange(
                    "blacklist_rules",
                    StagedDatabaseOperation.Archive,
                    backseatBaseline.Id,
                    backseatBaseline.RowVersion,
                    new Dictionary<string, object?>()),
            ],
            "Baseline archive protection"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => editor.ApplyAsync(
            [
                new StagedDatabaseChange(
                    "policy_sets",
                    StagedDatabaseOperation.Update,
                    backseatPolicy.Id,
                    backseatPolicy.RowVersion,
                    new Dictionary<string, object?>
                    {
                        ["enabled"] = false,
                    }),
            ],
            "Policy-set protection"));
    }

    [Fact]
    public async Task Apply_AllowsDisablingNonBaselineSemanticRule()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var editor = CreateEditor(database);
        await using var connection = await database.OpenConnectionAsync();
        var insultRule = await ReadIdentityAsync(
            connection,
            """
            SELECT r.rule_id, r.row_version
            FROM blacklist_rules r
            INNER JOIN policy_sets s ON s.scope_id = r.scope_id
            WHERE s.broadcaster_id = '*'
              AND r.rule_code = 'G_INSULT'
              AND r.archived_utc IS NULL;
            """);

        var result = await editor.ApplyAsync(
        [
            new StagedDatabaseChange(
                "blacklist_rules",
                StagedDatabaseOperation.Update,
                insultRule.Id,
                insultRule.RowVersion,
                new Dictionary<string, object?>
                {
                    ["enabled"] = false,
                }),
        ],
        "Disable optional semantic rule");

        Assert.Single(result.Changes);
        Assert.Equal(
            0L,
            Convert.ToInt64(
                await ScalarAsync(
                    connection,
                    $"SELECT enabled FROM blacklist_rules WHERE rule_id = {insultRule.Id};"),
                System.Globalization.CultureInfo.InvariantCulture));
    }

    private static DatabaseTableEditor CreateEditor(TestDatabase database)
    {
        return new DatabaseTableEditor(
            database.ConnectionFactory,
            new DatabaseCatalog(),
            new ChatTextNormalizer(),
            new WhitelistTextNormalizer(),
            []);
    }

    private static StagedDatabaseChange NewExactMessageWhitelist(string value)
    {
        return new StagedDatabaseChange(
            "whitelist_phrases",
            StagedDatabaseOperation.Insert,
            null,
            null,
            new Dictionary<string, object?>
            {
                ["broadcaster_id"] = GlobalPolicyStorage.BroadcasterId,
                ["subject_kind"] = "exact_message",
                ["subject_value"] = value,
                ["enabled"] = true,
            });
    }

    private static async Task<object?> ScalarAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<(long Id, long RowVersion)> ReadIdentityAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
    }
}
