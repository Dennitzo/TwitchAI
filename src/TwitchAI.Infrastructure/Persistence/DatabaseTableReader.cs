using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TwitchAI.Infrastructure.Persistence;

public enum DatabaseSortDirection
{
    Ascending,
    Descending,
}

public enum DatabasePaginationMode
{
    Offset,
    Keyset,
}

public sealed record DatabaseColumnMetadata(
    string Name,
    string DeclaredType,
    bool IsSearchable,
    bool IsSortable,
    bool IsPrimaryKey);

public sealed record DatabaseTableQuery(
    int PageSize = 50,
    int Offset = 0,
    string? SearchText = null,
    string? SortColumn = null,
    DatabaseSortDirection SortDirection = DatabaseSortDirection.Ascending,
    DatabasePaginationMode PaginationMode = DatabasePaginationMode.Offset,
    object? AfterKey = null);

public sealed record DatabaseRow(IReadOnlyDictionary<string, object?> Values);

public sealed record DatabasePage(
    string TableName,
    IReadOnlyList<DatabaseRow> Rows,
    IReadOnlyList<DatabaseColumnMetadata> Columns,
    long TotalCount,
    int Offset,
    int PageSize,
    object? NextKey,
    bool HasMore,
    string SortColumn,
    DatabaseSortDirection SortDirection);

public interface IDatabaseTableReader
{
    Task<DatabasePage> ReadPageAsync(
        string tableName,
        DatabaseTableQuery query,
        CancellationToken cancellationToken = default);

    Task<DatabasePage> ReadPageAsync(
        string tableName,
        object? afterKey = null,
        int pageSize = 100,
        CancellationToken cancellationToken = default);
}

public sealed class DatabaseTableReader : IDatabaseTableReader
{
    private const int MaximumSearchLength = 200;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IDatabaseCatalog _catalog;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        DatabaseColumnMetadata[]> _columnCache = new(StringComparer.Ordinal);

    public DatabaseTableReader(
        ISqliteConnectionFactory connectionFactory,
        IDatabaseCatalog catalog)
    {
        _connectionFactory = connectionFactory;
        _catalog = catalog;
    }

    public Task<DatabasePage> ReadPageAsync(
        string tableName,
        object? afterKey = null,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var definition = _catalog.GetRequired(tableName);
        return ReadPageAsync(
            tableName,
            new DatabaseTableQuery(
                PageSize: pageSize,
                SortColumn: definition.PrimaryKey,
                PaginationMode: DatabasePaginationMode.Keyset,
                AfterKey: afterKey),
            cancellationToken);
    }

    public async Task<DatabasePage> ReadPageAsync(
        string tableName,
        DatabaseTableQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var definition = _catalog.GetRequired(tableName);
        ValidateQuery(query);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var columns = await ReadColumnsAsync(
            connection,
            transaction,
            definition.Name,
            cancellationToken).ConfigureAwait(false);
        var sortColumn = ResolveSortColumn(query.SortColumn, definition, columns);
        if (query.PaginationMode == DatabasePaginationMode.Keyset &&
            !StringComparer.Ordinal.Equals(sortColumn, definition.PrimaryKey))
        {
            throw new ArgumentException(
                "Keyset pagination is only supported for the validated primary-key column.",
                nameof(query));
        }

        var searchText = string.IsNullOrEmpty(query.SearchText)
            ? null
            : query.SearchText;
        var searchableColumns = columns.Where(item => item.IsSearchable).ToArray();
        if (searchText is not null && searchableColumns.Length == 0)
        {
            throw new InvalidOperationException(
                $"Table '{definition.Name}' has no searchable columns.");
        }

        var searchPredicate = BuildSearchPredicate(searchableColumns, searchText is not null);
        var totalCount = await CountAsync(
            connection,
            transaction,
            definition.Name,
            searchPredicate,
            searchText,
            cancellationToken).ConfigureAwait(false);
        var fetchedRows = await ReadRowsAsync(
            connection,
            transaction,
            definition,
            columns,
            query,
            sortColumn,
            searchPredicate,
            searchText,
            cancellationToken).ConfigureAwait(false);

        var fetchedExtraRow = fetchedRows.Count > query.PageSize;
        if (fetchedExtraRow)
        {
            fetchedRows.RemoveAt(fetchedRows.Count - 1);
        }

        var nextKey = fetchedRows.Count == 0
            ? null
            : fetchedRows[^1].Values[definition.PrimaryKey];
        var hasMore = query.PaginationMode == DatabasePaginationMode.Offset
            ? (long)query.Offset + fetchedRows.Count < totalCount
            : fetchedExtraRow;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DatabasePage(
            definition.Name,
            fetchedRows,
            columns,
            totalCount,
            query.PaginationMode == DatabasePaginationMode.Offset ? query.Offset : 0,
            query.PageSize,
            nextKey,
            hasMore,
            sortColumn,
            query.SortDirection);
    }

    private static void ValidateQuery(DatabaseTableQuery query)
    {
        if (query.PageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                query.PageSize,
                "Page size must be between 1 and 200.");
        }

        if (query.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                query.Offset,
                "Offset may not be negative.");
        }

        if (query.SearchText?.Length > MaximumSearchLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                query.SearchText.Length,
                $"Search text may contain at most {MaximumSearchLength} characters.");
        }

        if (!Enum.IsDefined(query.SortDirection))
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                query.SortDirection,
                "Unknown database sort direction.");
        }

        if (!Enum.IsDefined(query.PaginationMode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                query.PaginationMode,
                "Unknown database pagination mode.");
        }

        if (query.PaginationMode == DatabasePaginationMode.Offset &&
            query.AfterKey is not null)
        {
            throw new ArgumentException(
                "Offset pagination may not include a keyset cursor.",
                nameof(query));
        }

        if (query.PaginationMode == DatabasePaginationMode.Keyset &&
            query.Offset != 0)
        {
            throw new ArgumentException(
                "Keyset pagination may not include an offset.",
                nameof(query));
        }
    }

    private async Task<DatabaseColumnMetadata[]> ReadColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        CancellationToken cancellationToken)
    {
        if (_columnCache.TryGetValue(tableName, out var cached))
        {
            return cached;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info({Quote(tableName)});";
        var columns = new List<DatabaseColumnMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            var declaredType = reader.IsDBNull(2)
                ? string.Empty
                : reader.GetString(2);
            var isBlob = declaredType.Contains("BLOB", StringComparison.OrdinalIgnoreCase);
            var isPrimaryKey = reader.GetInt64(5) > 0;
            columns.Add(
                new DatabaseColumnMetadata(
                    name,
                    declaredType,
                    IsSearchable: !isBlob,
                    IsSortable: true,
                    IsPrimaryKey: isPrimaryKey));
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Catalog table '{tableName}' does not exist in the active schema.");
        }

        var result = columns.ToArray();
        return _columnCache.GetOrAdd(tableName, result);
    }

    private static string ResolveSortColumn(
        string? requestedColumn,
        DatabaseTableDefinition definition,
        DatabaseColumnMetadata[] columns)
    {
        var sortColumn = string.IsNullOrWhiteSpace(requestedColumn)
            ? definition.PrimaryKey
            : requestedColumn;
        var match = columns.SingleOrDefault(
            item => StringComparer.Ordinal.Equals(item.Name, sortColumn));
        if (match is null || !match.IsSortable)
        {
            throw new ArgumentException(
                $"Sort column '{sortColumn}' is not exposed by table '{definition.Name}'.",
                nameof(requestedColumn));
        }

        return match.Name;
    }

    private static string BuildSearchPredicate(
        DatabaseColumnMetadata[] searchableColumns,
        bool hasSearch)
    {
        if (!hasSearch)
        {
            return string.Empty;
        }

        return "(" +
               string.Join(
                   " OR ",
                   searchableColumns.Select(
                       item =>
                           $"CAST({Quote(item.Name)} AS TEXT) LIKE @search ESCAPE '\\' COLLATE NOCASE")) +
               ")";
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string searchPredicate,
        string? searchText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT COUNT(*)
             FROM {Quote(tableName)}
             {Where(searchPredicate)};
             """;
        AddSearchParameter(command, searchText);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<List<DatabaseRow>> ReadRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseTableDefinition definition,
        DatabaseColumnMetadata[] columns,
        DatabaseTableQuery query,
        string sortColumn,
        string searchPredicate,
        string? searchText,
        CancellationToken cancellationToken)
    {
        var predicates = new List<string>();
        if (searchPredicate.Length > 0)
        {
            predicates.Add(searchPredicate);
        }

        if (query.PaginationMode == DatabasePaginationMode.Keyset &&
            query.AfterKey is not null)
        {
            var comparison = query.SortDirection == DatabaseSortDirection.Ascending
                ? ">"
                : "<";
            predicates.Add($"{Quote(definition.PrimaryKey)} {comparison} @after_key");
        }

        var direction = query.SortDirection == DatabaseSortDirection.Ascending
            ? "ASC"
            : "DESC";
        var sortExpression = BuildOrderExpression(sortColumn, columns);
        var primaryKeyExpression = BuildOrderExpression(definition.PrimaryKey, columns);
        var stableOrder = StringComparer.Ordinal.Equals(sortColumn, definition.PrimaryKey)
            ? $"{sortExpression} {direction}"
            : $"{sortExpression} {direction}, {primaryKeyExpression} {direction}";
        var offsetClause = query.PaginationMode == DatabasePaginationMode.Offset
            ? "OFFSET @offset"
            : string.Empty;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $"""
             SELECT *
             FROM {Quote(definition.Name)}
             {Where(predicates)}
             ORDER BY {stableOrder}
             LIMIT @fetch_size
             {offsetClause};
             """;
        AddSearchParameter(command, searchText);
        if (query.PaginationMode == DatabasePaginationMode.Keyset &&
            query.AfterKey is not null)
        {
            command.Parameters.AddWithValue("@after_key", query.AfterKey);
        }

        command.Parameters.AddWithValue("@fetch_size", query.PageSize + 1);
        if (query.PaginationMode == DatabasePaginationMode.Offset)
        {
            command.Parameters.AddWithValue("@offset", query.Offset);
        }

        var rows = new List<DatabaseRow>(query.PageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new Dictionary<string, object?>(columns.Length, StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values.Add(
                    reader.GetName(index),
                    reader.IsDBNull(index) ? null : reader.GetValue(index));
            }

            rows.Add(new DatabaseRow(values));
        }

        return rows;
    }

    private static string BuildOrderExpression(
        string columnName,
        DatabaseColumnMetadata[] columns)
    {
        var column = columns.Single(
            item => StringComparer.Ordinal.Equals(item.Name, columnName));
        var quotedName = Quote(column.Name);
        return HasTextAffinity(column.DeclaredType)
            ? $"{quotedName} COLLATE NOCASE"
            : quotedName;
    }

    private static bool HasTextAffinity(string declaredType)
    {
        return declaredType.Contains("CHAR", StringComparison.OrdinalIgnoreCase) ||
               declaredType.Contains("CLOB", StringComparison.OrdinalIgnoreCase) ||
               declaredType.Contains("TEXT", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddSearchParameter(SqliteCommand command, string? searchText)
    {
        if (searchText is null)
        {
            return;
        }

        var escaped = searchText
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
        command.Parameters.AddWithValue("@search", $"%{escaped}%");
    }

    private static string Where(string predicate)
    {
        return predicate.Length == 0 ? string.Empty : $"WHERE {predicate}";
    }

    private static string Where(List<string> predicates)
    {
        return predicates.Count == 0
            ? string.Empty
            : $"WHERE {string.Join(" AND ", predicates)}";
    }

    private static string Quote(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
