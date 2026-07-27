using Microsoft.Data.Sqlite;
using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Tests.Infrastructure;

public sealed class DatabaseTableReaderTests
{
    [Fact]
    public async Task ReadPage_SearchesOnServerAndEscapesLikeWildcards()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertPhrasesAsync(
            database,
            ["alpha general", "Alpha Backseat", "100% legit", "100x legit"]);
        var reader = CreateReader(database);

        var alpha = await reader.ReadPageAsync(
            "whitelist_phrases",
            new DatabaseTableQuery(
                PageSize: 25,
                SearchText: "alpha",
                SortColumn: "subject_value",
                SortDirection: DatabaseSortDirection.Descending));
        var literalPercent = await reader.ReadPageAsync(
            "whitelist_phrases",
            new DatabaseTableQuery(
                PageSize: 25,
                SearchText: "100%",
                SortColumn: "subject_value"));

        Assert.Equal(2, alpha.TotalCount);
        Assert.Equal(
            ["alpha general", "Alpha Backseat"],
            alpha.Rows.Select(item => item.Values["subject_value"]));
        Assert.Equal(1, literalPercent.TotalCount);
        Assert.Equal("100% legit", Assert.Single(literalPercent.Rows).Values["subject_value"]);
    }

    [Fact]
    public async Task ReadPage_UsesValidatedSortingOffsetAndExactTotal()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertPhrasesAsync(
            database,
            Enumerable.Range(0, 30)
                .Select(index => $"reader-page-{index:00}")
                .ToArray());
        var reader = CreateReader(database);
        var query = new DatabaseTableQuery(
            PageSize: 25,
            SearchText: "reader-page-",
            SortColumn: "subject_value");

        var first = await reader.ReadPageAsync("whitelist_phrases", query);
        var second = await reader.ReadPageAsync(
            "whitelist_phrases",
            query with { Offset = 25 });

        Assert.Equal(30, first.TotalCount);
        Assert.Equal(25, first.Rows.Count);
        Assert.True(first.HasMore);
        Assert.Equal("reader-page-00", first.Rows[0].Values["subject_value"]);
        Assert.Equal(30, second.TotalCount);
        Assert.Equal(5, second.Rows.Count);
        Assert.False(second.HasMore);
        Assert.Equal("reader-page-25", second.Rows[0].Values["subject_value"]);
    }

    [Fact]
    public async Task ReadPage_SupportsPrimaryKeyKeysetPagination()
    {
        await using var database = await TestDatabase.CreateAsync();
        await InsertPhrasesAsync(
            database,
            Enumerable.Range(0, 30)
                .Select(index => $"reader-keyset-{index:00}")
                .ToArray());
        var reader = CreateReader(database);
        var query = new DatabaseTableQuery(
            PageSize: 25,
            SearchText: "reader-keyset-",
            SortColumn: "allowlist_id",
            PaginationMode: DatabasePaginationMode.Keyset);

        var first = await reader.ReadPageAsync("whitelist_phrases", query);
        var second = await reader.ReadPageAsync(
            "whitelist_phrases",
            query with { AfterKey = first.NextKey });

        Assert.Equal(30, first.TotalCount);
        Assert.Equal(25, first.Rows.Count);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextKey);
        Assert.Equal(5, second.Rows.Count);
        Assert.False(second.HasMore);
        Assert.All(
            second.Rows,
            row => Assert.True(
                Convert.ToInt64(
                    row.Values["allowlist_id"],
                    System.Globalization.CultureInfo.InvariantCulture) >
                Convert.ToInt64(
                    first.NextKey,
                    System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task ReadPage_RejectsUnexposedSortIdentifier()
    {
        await using var database = await TestDatabase.CreateAsync();
        var reader = CreateReader(database);
        var query = new DatabaseTableQuery(
            PageSize: 25,
            SortColumn: "subject_value\"; DROP TABLE whitelist_phrases; --");

        await Assert.ThrowsAsync<ArgumentException>(
            () => reader.ReadPageAsync("whitelist_phrases", query));

        var validPage = await reader.ReadPageAsync(
            "whitelist_phrases",
            new DatabaseTableQuery(PageSize: 25));
        Assert.True(validPage.TotalCount >= 1);
    }

    [Fact]
    public async Task ReadPage_ReturnsIntrospectedColumnsForEmptyTable()
    {
        await using var database = await TestDatabase.CreateAsync();
        var reader = CreateReader(database);

        var page = await reader.ReadPageAsync(
            "moderation_cases",
            new DatabaseTableQuery(PageSize: 25));

        Assert.Empty(page.Rows);
        Assert.Equal(0, page.TotalCount);
        Assert.Contains(page.Columns, item => item.Name == "case_id");
        Assert.Contains(page.Columns, item => item.Name == "row_version");
    }

    [Fact]
    public async Task CatalogPrimaryKeys_MatchTheActiveSqliteSchema()
    {
        await using var database = await TestDatabase.CreateAsync();
        var catalog = new DatabaseCatalog();
        var reader = new DatabaseTableReader(
            database.ConnectionFactory,
            catalog);

        foreach (var table in catalog.Tables)
        {
            var page = await reader.ReadPageAsync(
                table.Name,
                new DatabaseTableQuery(PageSize: 1));
            var primaryKey = Assert.Single(
                page.Columns,
                column => column.IsPrimaryKey);

            Assert.Equal(table.PrimaryKey, primaryKey.Name);
            Assert.Equal(table.PrimaryKey, page.SortColumn);
        }
    }

    private static DatabaseTableReader CreateReader(TestDatabase database)
    {
        return new DatabaseTableReader(
            database.ConnectionFactory,
            new DatabaseCatalog());
    }

    private static async Task InsertPhrasesAsync(
        TestDatabase database,
        string[] phrases)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();
        foreach (var phrase in phrases)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO whitelist_phrases(
                    broadcaster_id, subject_kind, subject_value, normalized_value,
                    enabled, row_version, created_utc, updated_utc)
                VALUES(
                    'reader-test', 'exact_message', @phrase, lower(@phrase),
                    1, 1, @now, @now);
                """;
            command.Parameters.AddWithValue("@phrase", phrase);
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }
}
