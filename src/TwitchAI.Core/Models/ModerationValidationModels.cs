namespace TwitchAI.Core.Models;

/// <summary>
/// Describes one semantic or structural problem in a local-model response.
/// A message ID is present when that decision can be repaired in isolation.
/// </summary>
public sealed record ModerationValidationIssue(
    string Code,
    string Message,
    string? MessageId = null);

/// <summary>
/// Carries both the usable part of an AI response and the exact repair scope.
/// Valid decisions remain available even when another message needs repair.
/// </summary>
public sealed record ModerationValidationReport(
    ValidatedModerationEnvelope? Envelope,
    IReadOnlyList<ValidatedModerationDecision> ValidDecisions,
    IReadOnlyList<ModerationValidationIssue> Issues,
    IReadOnlyList<string> RepairMessageIds,
    bool RequiresFullRepair)
{
    public bool IsValid => Envelope is not null && Issues.Count == 0;

    public IReadOnlyList<string> LocalFallbackMessageIds { get; init; } = [];
}
