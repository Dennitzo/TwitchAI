namespace TwitchAI.Core.Models;

public enum LearningVerdict
{
    Allow,
    Moderate,
}

public sealed record LearningTestRequest(string OriginalText);

public sealed record LearningTestRequestEnvelopeV1(
    string EnvelopeVersion,
    Guid TestId,
    IReadOnlyList<PolicySetRevision> ActivePolicySets,
    IReadOnlyList<ModerationRuleDefinition> ActiveRules,
    IReadOnlyList<RetrievalExample> LearningExamples,
    LearningTestMessageV1 Message);

public sealed record LearningTestMessageV1(
    string LowerText,
    IReadOnlyList<string> LocalMatchCodes);

public sealed record LearningTestDecisionCandidateV1(
    string Verdict,
    string Action,
    int? TimeoutSeconds,
    string PolicySet,
    string? RuleCode,
    string Reason,
    IReadOnlyList<string> TriggerPhrases,
    double Confidence);

public sealed record LearningTestResponseEnvelopeV1(
    string EnvelopeVersion,
    Guid TestId,
    LearningTestDecisionCandidateV1 Decision);

public sealed record LearningTestResult(
    Guid TestId,
    string OriginalText,
    string LowerText,
    LearningVerdict Verdict,
    ModerationActionKind ProposedAction,
    int? ProposedTimeoutSeconds,
    PolicySetCode ProposedPolicySet,
    string ProposedRuleCode,
    string ProposedReason,
    IReadOnlyList<string> ProposedTriggerPhrases,
    double Confidence,
    IReadOnlyList<string> LocalMatchCodes,
    long AiLatencyMilliseconds,
    string PolicyRevisionFingerprint,
    IReadOnlyList<ModerationRuleDefinition> ActiveRules,
    string ModelId,
    int? PromptTokens,
    int? CompletionTokens,
    int? TotalTokens,
    DateTimeOffset AnalyzedAtUtc,
    bool UsedRuleFallback)
{
    public long LearningContextRevision { get; init; }
}

public sealed record LearningReviewItem(
    LearningTestResult TestResult,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    PolicySetCode PolicySet,
    string RuleCode,
    string Reason,
    IReadOnlyList<string> SelectedTriggerPhrases);

public enum LearningFeedbackDecision
{
    ConfirmedViolation,
    MarkedAllowed,
    Aborted,
}

public sealed record LearningFeedbackResult(
    long? ManualExampleId,
    bool Saved,
    LearningFeedbackDecision Decision,
    string Message);
