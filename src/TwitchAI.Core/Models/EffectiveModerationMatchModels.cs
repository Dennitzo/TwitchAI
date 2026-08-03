namespace TwitchAI.Core.Models;

public abstract record EffectiveModerationMatch(
    string Text,
    double Score,
    bool IsExact);

public sealed record EffectiveAllowMatch(
    string Text,
    double Score,
    bool IsExact,
    string Source,
    string MatchedPhrase = "",
    string? RuleCode = null,
    PolicySetCode? PolicySet = null,
    DateTimeOffset? EffectiveAtUtc = null)
    : EffectiveModerationMatch(Text, Score, IsExact);

public sealed record EffectivePhraseMatch(
    long PhraseId,
    string RuleCode,
    PolicySetCode PolicySet,
    string Phrase,
    double Score,
    string Source)
    : EffectiveModerationMatch(Phrase, Score, IsExact: false);

public sealed record EffectiveLearningMatch(
    RetrievalExample Example,
    double Score)
    : EffectiveModerationMatch(
        Example.LowerText,
        Score,
        Example.IsExactHumanDecision);

public enum ModerationDecisionConstraintKind
{
    Free,
    ModerateRule,
    LearnedModeration,
}

public sealed record ModerationDecisionConstraint(
    ModerationDecisionConstraintKind Kind,
    string? RuleCode,
    ModerationActionKind? Action,
    int? TimeoutSeconds,
    string Source)
{
    public static ModerationDecisionConstraint Free { get; } =
        new(ModerationDecisionConstraintKind.Free, null, null, null, "free");
}

public sealed record EffectiveLearningConsensus(
    string Decision,
    string? RuleCode,
    double WeightedScore,
    double Margin,
    int ExampleCount);
