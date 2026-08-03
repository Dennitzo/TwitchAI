using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

/// <summary>
/// Compatibility surface for historical staged callers. Human examples and
/// manual phrases no longer produce local decisions or category constraints;
/// the live AI receives them only as non-binding context.
/// </summary>
public sealed record HumanPriorityDecision(
    bool IsAllow,
    ModerationRuleDefinition? Rule,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    IReadOnlyList<string> TriggerPhrases,
    string? EvidenceKind,
    string Source);

public static class HumanModerationPriority
{
    public static HumanPriorityDecision? Resolve(
        string lowerText,
        EffectivePolicySnapshot policy,
        IReadOnlyList<BlacklistMatch> localMatches,
        IReadOnlyList<RetrievalExample> examples)
    {
        ArgumentNullException.ThrowIfNull(lowerText);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(localMatches);
        ArgumentNullException.ThrowIfNull(examples);
        return null;
    }

    public static EffectiveLearningConsensus? ResolveConsensus(
        IReadOnlyList<RetrievalExample> examples)
    {
        ArgumentNullException.ThrowIfNull(examples);
        return null;
    }
}
