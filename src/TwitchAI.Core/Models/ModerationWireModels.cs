using System.Text.Json;

namespace TwitchAI.Core.Models;

/// <summary>
/// The compact, model-facing moderation request. The full domain envelope remains
/// available locally through <see cref="CompiledModerationPrompt.Envelope"/>.
/// </summary>
public sealed record ModerationWireRequestV6(
    string EnvelopeVersion,
    Guid BatchId,
    string Category,
    IReadOnlyList<ModerationWireRuleV6> ActiveRules,
    IReadOnlyList<ModerationWireMessageV6> Messages,
    IReadOnlyList<ModerationWireExampleV6>? LearningExamples = null,
    IReadOnlyList<ModerationWireReplyTemplateV6>? ReplyTemplates = null);

public sealed record ModerationWireRuleV6(
    string RuleCode,
    string PolicySet,
    int Priority,
    string Criteria,
    IReadOnlyList<string> EvidenceKinds);

public sealed record ModerationWireMessageV6(
    string MessageId,
    string Text,
    IReadOnlyList<string>? LocalMatchCodes = null,
    IReadOnlyList<ModerationWirePhraseMatchV1>? LocalPhraseMatches = null,
    IReadOnlyList<string>? ExcludedRuleCodes = null,
    int? TwitchEmoteCount = null,
    bool? IsTwitchEmoteOnly = null,
    bool? HasRepeatedPhraseSpam = null,
    IReadOnlyList<string>? ReplyCandidateIds = null,
    IReadOnlyList<ModerationWireLearningMatchV1>? AllowLearningMatches = null,
    IReadOnlyList<ModerationWirePhraseMatchV1>? EffectivePhraseMatches = null,
    IReadOnlyList<ModerationWireLearningMatchV1>? EffectiveLearningMatches = null,
    ModerationWireDecisionConstraintV3? DecisionConstraint = null,
    EffectiveLearningConsensus? LearningConsensus = null,
    bool? IsAutoModHeld = null,
    string? AutoModReason = null,
    string? AutoModCategory = null,
    int? AutoModLevel = null);

public sealed record ModerationWireDecisionConstraintV3(
    string Mode,
    string? RuleCode,
    string? Action,
    int? TimeoutSeconds,
    string Source);

public sealed record ModerationWirePhraseMatchV1(
    long PhraseId,
    string RuleCode,
    string Phrase,
    double Score = 1d,
    string MatchKind = "manual_phrase");

public sealed record ModerationWireExampleV6(
    string Text,
    string Decision,
    string PolicySet,
    string? RuleCode = null,
    string? Action = null,
    int? TimeoutSeconds = null,
    string? Reason = null,
    bool? ExactHumanDecision = null,
    string? MatchedPhrase = null);

public sealed record ModerationWireLearningMatchV1(
    string Text,
    string Decision,
    string PolicySet,
    string? RuleCode,
    string? Action,
    int? TimeoutSeconds,
    string? Reason,
    double Score,
    bool Exact,
    string Source = "learning",
    DateTimeOffset? EffectiveAtUtc = null,
    string? MatchedPhrase = null);

public sealed record ModerationWireReplyTemplateV6(
    string TemplateId,
    string Example);

public sealed record LearningTestWireRequestV1(
    string EnvelopeVersion,
    Guid TestId,
    IReadOnlyList<ModerationWireRuleV6> ActiveRules,
    LearningTestWireMessageV1 Message,
    IReadOnlyList<ModerationWireExampleV6>? LearningExamples = null);

public sealed record LearningTestWireMessageV1(
    string Text,
    IReadOnlyList<string>? LocalMatchCodes = null,
    IReadOnlyList<ModerationWirePhraseMatchV1>? LocalPhraseMatches = null);

public sealed record ModerationPromptCharacterCounts(
    int System,
    int Rules,
    int Messages,
    int Examples,
    int Templates,
    int Schema);

/// <summary>
/// The exact prompt artifacts sent to LM Studio. Keeping the response format as
/// a cloned JsonElement avoids rebuilding the dynamic schema in the HTTP client.
/// </summary>
public sealed record CompiledModerationPrompt(
    ModerationRequestEnvelopeV3 Envelope,
    string ContractVersion,
    string SystemPrompt,
    string UserPrompt,
    JsonElement ResponseFormat,
    string PolicyHash,
    IReadOnlyList<string> MessageIds,
    IReadOnlyList<string> ActiveRuleCodes,
    IReadOnlyList<string> ReplyTemplateIds,
    ModerationPromptCharacterCounts PromptCharacters)
{
    public ModerationPromptStage Stage { get; init; } = ModerationPromptStage.Combined;

    public string ResponseContractVersion { get; init; } =
        "twitchai.moderation.response.v3";

    public string? CorrelationId { get; init; }

    public IReadOnlyList<string> ReplyOnlyMessageIds { get; init; } = [];
}

public sealed record ModerationClassificationWireRequestV1(
    string EnvelopeVersion,
    Guid BatchId,
    string Category,
    IReadOnlyList<ModerationWireRuleV6> ActiveRules,
    IReadOnlyList<ModerationWireMessageV6> Messages,
    IReadOnlyList<ModerationWireExampleV6>? LearningExamples = null,
    IReadOnlyList<ModerationWireReplyTemplateV6>? ReplyTemplates = null);

public sealed record ModerationConfirmationWireRequestV1(
    string EnvelopeVersion,
    Guid BatchId,
    string Category,
    IReadOnlyList<ModerationWireRuleV6> ActiveRules,
    IReadOnlyList<ModerationConfirmationWireCandidateV1> Candidates,
    IReadOnlyList<ModerationWireExampleV6>? AllowExamples = null);

public sealed record ModerationConfirmationWireCandidateV1(
    string MessageId,
    string Text,
    string Action,
    int? TimeoutSeconds,
    string RuleCode,
    string? InitialEvidenceKind = null,
    string? InitialSensitiveDataType = null,
    IReadOnlyList<ModerationWirePhraseMatchV1>? LocalPhraseMatches = null);

public sealed record ModerationWireResponseV6(
    string EnvelopeVersion,
    Guid BatchId,
    IReadOnlyList<string> NoActionMessageIds,
    IReadOnlyList<ModerationWireDecisionV6> Moderations,
    IReadOnlyList<ModerationWireReplyV6>? Replies = null);

public sealed record ModerationWireDecisionV6(
    string MessageId,
    string Action,
    int? TimeoutSeconds,
    string RuleCode,
    string Reason,
    IReadOnlyList<string> TriggerPhrases,
    double Confidence,
    string EvidenceKind,
    string? SensitiveDataType = null);

public sealed record ModerationWireDecisionV9(
    string Action,
    int? TimeoutSeconds,
    string RuleCode,
    string ViolationReason,
    string Guidance,
    IReadOnlyList<string> TriggerPhrases,
    double Confidence,
    string EvidenceKind,
    string? SensitiveDataType = null);

public sealed record ModerationWireReplyV6(
    string MessageId,
    string ReplyTemplateId,
    double Confidence);
