using System.Globalization;
using Microsoft.Data.Sqlite;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using CoreDatabasePage = TwitchAI.Core.Models.DatabasePage;
using CoreDatabaseRow = TwitchAI.Core.Models.DatabaseRow;
using CoreDatabaseTableAccess = TwitchAI.Core.Models.DatabaseTableAccess;
using CoreDatabaseTableDescriptor = TwitchAI.Core.Models.DatabaseTableDescriptor;

namespace TwitchAI.Infrastructure.Persistence;

public sealed class CoreDatabaseTableCatalog : IDatabaseTableCatalog
{
    private readonly IDatabaseCatalog _catalog;
    private readonly IDatabaseInitializer _initializer;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly object _sync = new();
    private IReadOnlyList<CoreDatabaseTableDescriptor>? _cachedTables;

    public CoreDatabaseTableCatalog(
        IDatabaseCatalog catalog,
        IDatabaseInitializer initializer,
        ISqliteConnectionFactory connectionFactory)
    {
        _catalog = catalog;
        _initializer = initializer;
        _connectionFactory = connectionFactory;
    }

    public IReadOnlyList<CoreDatabaseTableDescriptor> GetTables()
    {
        if (_cachedTables is not null)
        {
            return _cachedTables;
        }

        lock (_sync)
        {
            _cachedTables ??= LoadTablesAsync().GetAwaiter().GetResult();
            return _cachedTables;
        }
    }

    public CoreDatabaseTableDescriptor GetTable(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return GetTables().FirstOrDefault(
                   item => StringComparer.Ordinal.Equals(item.Name, tableName))
               ?? throw new UnauthorizedAccessException(
                   $"Table '{tableName}' is not exposed by the protected database catalog.");
    }

    private async Task<IReadOnlyList<CoreDatabaseTableDescriptor>> LoadTablesAsync()
    {
        await _initializer.InitializeAsync().ConfigureAwait(false);
        await using var connection = await _connectionFactory.OpenConnectionAsync()
            .ConfigureAwait(false);
        var tables = new List<CoreDatabaseTableDescriptor>(_catalog.Tables.Count);
        foreach (var definition in _catalog.Tables)
        {
            tables.Add(await ReadDescriptorAsync(connection, definition).ConfigureAwait(false));
        }

        return tables;
    }

    private static async Task<CoreDatabaseTableDescriptor> ReadDescriptorAsync(
        SqliteConnection connection,
        DatabaseTableDefinition definition)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(definition.Name)});";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var columns = new List<DatabaseColumnDescriptor>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            var primaryKeyOrdinal = reader.GetInt32(5);
            columns.Add(new DatabaseColumnDescriptor(
                name,
                reader.GetString(2),
                IsNullable: reader.GetInt32(3) == 0 && primaryKeyOrdinal == 0,
                IsEditable: definition.EditableColumns.ContainsKey(name),
                IsIdentity: primaryKeyOrdinal > 0,
                Ordinal: reader.GetInt32(0)));
        }

        if (columns.Count == 0)
        {
            throw new InvalidOperationException(
                $"Protected database table '{definition.Name}' has not been initialized.");
        }

        return new CoreDatabaseTableDescriptor(
            definition.Name,
            DisplayName(definition.Name),
            MapAccess(definition.AccessClass),
            definition.PrimaryKey,
            columns);
    }

    private static CoreDatabaseTableAccess MapAccess(DatabaseTableAccessClass access)
    {
        return access switch
        {
            DatabaseTableAccessClass.UserEditable => CoreDatabaseTableAccess.Editable,
            DatabaseTableAccessClass.AppendOnly => CoreDatabaseTableAccess.AppendOnly,
            DatabaseTableAccessClass.ApplicationManaged => CoreDatabaseTableAccess.WorkflowOnly,
            DatabaseTableAccessClass.AuditReadOnly => CoreDatabaseTableAccess.SystemReadOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(access), access, null),
        };
    }

    private static string DisplayName(string tableName)
    {
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(
            tableName.Replace('_', ' '));
    }

    private static string Quote(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}

public sealed class CoreDatabaseEditorService : IDatabaseEditorService
{
    private const int MaximumPageSize = 200;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IDatabaseTableCatalog _catalog;
    private readonly IDatabaseTableEditor _editor;

    public CoreDatabaseEditorService(
        ISqliteConnectionFactory connectionFactory,
        IDatabaseTableCatalog catalog,
        IDatabaseTableEditor editor)
    {
        _connectionFactory = connectionFactory;
        _catalog = catalog;
        _editor = editor;
    }

    public async ValueTask<CoreDatabasePage> QueryAsync(
        string tableName,
        string? searchText,
        string? sortColumn,
        bool descending,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var table = _catalog.GetTable(tableName);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                $"Page size must be between 1 and {MaximumPageSize}.");
        }

        var sort = string.IsNullOrWhiteSpace(sortColumn)
            ? table.PrimaryKey
            : sortColumn;
        if (!table.Columns.Any(item => StringComparer.Ordinal.Equals(item.Name, sort)))
        {
            throw new ArgumentException(
                $"Sort column '{sort}' does not exist in protected table '{table.Name}'.",
                nameof(sortColumn));
        }

        var searchableColumns = table.Columns
            .Where(item => IsSearchable(item.DataType))
            .Select(item => item.Name)
            .ToArray();
        var hasSearch = !string.IsNullOrWhiteSpace(searchText) && searchableColumns.Length > 0;
        var whereClause = hasSearch
            ? "WHERE (" + string.Join(
                " OR ",
                searchableColumns.Select(
                    column => $"CAST({Quote(column)} AS TEXT) LIKE @search ESCAPE '\\'")) + ")"
            : string.Empty;

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var total = await CountAsync(
            connection,
            table,
            whereClause,
            hasSearch ? EscapeLike(searchText!) : null,
            cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT *
             FROM {Quote(table.Name)}
             {whereClause}
             ORDER BY {Quote(sort)} {(descending ? "DESC" : "ASC")},
                      {Quote(table.PrimaryKey)} ASC
             LIMIT @page_size OFFSET @offset;
             """;
        if (hasSearch)
        {
            command.Parameters.AddWithValue("@search", $"%{EscapeLike(searchText!)}%");
        }

        command.Parameters.AddWithValue("@page_size", pageSize);
        command.Parameters.AddWithValue("@offset", offset);

        var rows = new List<CoreDatabaseRow>(pageSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var values = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++)
            {
                values[reader.GetName(index)] =
                    reader.IsDBNull(index) ? null : reader.GetValue(index);
            }

            rows.Add(new CoreDatabaseRow(
                values,
                values.TryGetValue("row_version", out var rowVersion) &&
                rowVersion is not null
                    ? Convert.ToInt64(rowVersion, CultureInfo.InvariantCulture)
                    : null));
        }

        return new CoreDatabasePage(rows, offset, pageSize, total);
    }

    public async ValueTask<DatabaseCommitResult> CommitAsync(
        DatabaseChangeSet changeSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ArgumentException.ThrowIfNullOrWhiteSpace(changeSet.ChangeReason);

        var staged = changeSet.Drafts.Select(MapDraft).ToArray();
        var result = await _editor.ApplyAsync(
            staged,
            changeSet.ChangeReason,
            cancellationToken).ConfigureAwait(false);
        var invalidations = result.PolicyChanges
            .Select(ToInvalidationToken)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new DatabaseCommitResult(result.Changes.Count, invalidations);
    }

    private StagedDatabaseChange MapDraft(DatabaseRowDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var table = _catalog.GetTable(draft.TableName);
        long? entityId = draft.PrimaryKeyValue is null
            ? null
            : Convert.ToInt64(draft.PrimaryKeyValue, CultureInfo.InvariantCulture);
        var operation = entityId is null
            ? StagedDatabaseOperation.Insert
            : draft.ArchiveState switch
            {
                true => StagedDatabaseOperation.Archive,
                false => StagedDatabaseOperation.Restore,
                null => StagedDatabaseOperation.Update,
            };

        if (operation == StagedDatabaseOperation.Insert &&
            draft.ExpectedRowVersion is not null)
        {
            throw new ArgumentException(
                $"Insert draft for '{table.Name}' may not contain a row version.",
                nameof(draft));
        }

        if (operation == StagedDatabaseOperation.Insert &&
            draft.ArchiveState is not null)
        {
            throw new ArgumentException(
                $"Insert draft for '{table.Name}' may not contain an archive state.",
                nameof(draft));
        }

        return new StagedDatabaseChange(
            table.Name,
            operation,
            entityId,
            draft.ExpectedRowVersion,
            draft.ChangedValues);
    }

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        CoreDatabaseTableDescriptor table,
        string whereClause,
        string? escapedSearch,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM {Quote(table.Name)} {whereClause};";
        if (escapedSearch is not null)
        {
            command.Parameters.AddWithValue("@search", $"%{escapedSearch}%");
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static bool IsSearchable(string dataType)
    {
        return dataType.Contains("TEXT", StringComparison.OrdinalIgnoreCase) ||
               dataType.Contains("CHAR", StringComparison.OrdinalIgnoreCase) ||
               dataType.Contains("CLOB", StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeLike(string text)
    {
        return text
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static string ToInvalidationToken(PolicyChangeSet change)
    {
        return string.Join(
            ":",
            change.BroadcasterId,
            change.ScopeKind?.ToString() ?? "all",
            change.CategoryId ?? "*",
            change.NewRevision?.ToString(CultureInfo.InvariantCulture) ?? "*",
            change.AllowlistChanged ? "whitelist" : "policy");
    }

    private static string Quote(string identifier)
    {
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
