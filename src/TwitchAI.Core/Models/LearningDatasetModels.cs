namespace TwitchAI.Core.Models;

public enum LearningSourceKind
{
    TwitchModeration,
    ManualTest,
}

public enum LearningDecision
{
    Moderate,
    Allow,
}

public readonly record struct LearningSourceKey(
    LearningSourceKind Kind,
    long SourceId)
{
    public string DisplayId => Kind switch
    {
        LearningSourceKind.TwitchModeration => $"T-{SourceId}",
        LearningSourceKind.ManualTest => $"L-{SourceId}",
        _ => SourceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}

public sealed record LearningDatasetQuery(
    LearningDecision? Decision,
    PolicySetCode? PolicySet,
    string? SearchText,
    string? SortColumn,
    bool Descending,
    int Offset,
    int PageSize);

public sealed record LearningDatasetRow(
    LearningSourceKey Source,
    string DisplayId,
    string OriginalText,
    string LowerText,
    LearningDecision Decision,
    PolicySetCode PolicySet,
    string RuleCode,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    string Reason,
    string SourceLabel,
    DateTimeOffset OccurredAtUtc,
    long RowVersion);

public sealed record LearningDatasetDraft(
    string OriginalText,
    LearningDecision Decision,
    PolicySetCode PolicySet,
    string RuleCode,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    string Reason);

public sealed record LearningDatasetPage(
    IReadOnlyList<LearningDatasetRow> Rows,
    int TotalCount);
