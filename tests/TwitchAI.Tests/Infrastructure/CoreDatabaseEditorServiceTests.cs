using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class CoreDatabaseEditorServiceTests
{
    [Fact]
    public async Task Commit_ArchiveThenRestore_UsesExplicitArchiveState()
    {
        await using var database = await TestDatabase.CreateAsync();
        var catalog = new DatabaseCatalog();
        using var tableEditor = new DatabaseTableEditor(
            database.ConnectionFactory,
            catalog,
            new ChatTextNormalizer(),
            new WhitelistTextNormalizer(),
            []);
        var coreCatalog = new CoreDatabaseTableCatalog(
            catalog,
            database.Initializer,
            database.ConnectionFactory);
        var service = new CoreDatabaseEditorService(
            database.ConnectionFactory,
            coreCatalog,
            tableEditor);

        var inserted = await service.CommitAsync(
            new DatabaseChangeSet(
            [
                new DatabaseRowDraft(
                    "whitelist_phrases",
                    PrimaryKeyValue: null,
                    ExpectedRowVersion: null,
                    new Dictionary<string, object?>
                    {
                        ["broadcaster_id"] = GlobalPolicyStorage.BroadcasterId,
                        ["subject_kind"] = "exact_message",
                        ["subject_value"] = "restore-fixture",
                        ["enabled"] = true,
                    },
                    ArchiveState: null),
            ],
            "Restore contract fixture"));
        Assert.Equal(1, inserted.ChangedRows);

        var page = await service.QueryAsync(
            "whitelist_phrases",
            searchText: "restore-fixture",
            sortColumn: "allowlist_id",
            descending: false,
            offset: 0,
            pageSize: 25);
        var row = Assert.Single(page.Rows);
        var id = Convert.ToInt64(
            row.Values["allowlist_id"],
            System.Globalization.CultureInfo.InvariantCulture);

        await service.CommitAsync(
            new DatabaseChangeSet(
            [
                new DatabaseRowDraft(
                    "whitelist_phrases",
                    id,
                    ExpectedRowVersion: 1,
                    new Dictionary<string, object?>(),
                    ArchiveState: true),
            ],
            "Archive restore fixture"));

        await service.CommitAsync(
            new DatabaseChangeSet(
            [
                new DatabaseRowDraft(
                    "whitelist_phrases",
                    id,
                    ExpectedRowVersion: 2,
                    new Dictionary<string, object?>(),
                    ArchiveState: false),
            ],
            "Restore fixture"));

        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT archived_utc, row_version
            FROM whitelist_phrases
            WHERE allowlist_id = @id;
            """;
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal(3L, reader.GetInt64(1));
    }
}
