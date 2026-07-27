namespace TwitchAI.Core.Models;

public enum DatabaseTableAccess
{
    Editable,
    WorkflowOnly,
    AppendOnly,
    SystemReadOnly,
}

public sealed record DatabaseColumnDescriptor(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsEditable,
    bool IsIdentity,
    int Ordinal);

public sealed record DatabaseTableDescriptor(
    string Name,
    string DisplayName,
    DatabaseTableAccess Access,
    string PrimaryKey,
    IReadOnlyList<DatabaseColumnDescriptor> Columns);

public sealed record DatabaseRow(
    IReadOnlyDictionary<string, object?> Values,
    long? RowVersion);

public sealed record DatabasePage(
    IReadOnlyList<DatabaseRow> Rows,
    int Offset,
    int PageSize,
    long TotalCount);

public sealed record DatabaseRowDraft(
    string TableName,
    object? PrimaryKeyValue,
    long? ExpectedRowVersion,
    IReadOnlyDictionary<string, object?> ChangedValues,
    bool? ArchiveState);

public sealed record DatabaseChangeSet(
    IReadOnlyList<DatabaseRowDraft> Drafts,
    string ChangeReason);

public sealed record DatabaseCommitResult(
    int ChangedRows,
    IReadOnlyList<string> InvalidatedPolicyFingerprints);
