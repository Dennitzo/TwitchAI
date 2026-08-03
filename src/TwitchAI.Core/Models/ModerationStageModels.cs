namespace TwitchAI.Core.Models;

public enum ModerationPromptStage
{
    Combined,
    Classification,
    Confirmation,
}

public enum ModerationCandidateSource
{
    SemanticClassification,
    ExactModerateLearning,
    ManualBlacklistPhrase,
}

public sealed record ModerationClassificationRequest(
    ModerationRequestEnvelopeV3 Envelope,
    IReadOnlyList<string> ReplyOnlyMessageIds);

public sealed record ModerationClassificationDecision(
    string TemporaryMessageId,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    string? RuleCode,
    string? EvidenceKind,
    string? SensitiveDataType,
    double Confidence,
    string? ReplyTemplateId = null,
    double? ReplyConfidence = null);

public sealed record ModerationClassificationEnvelope(
    Guid BatchId,
    IReadOnlyList<ModerationClassificationDecision> Decisions);

public sealed record ModerationClassificationReport(
    ModerationClassificationEnvelope? Envelope,
    IReadOnlyList<ModerationClassificationDecision> ValidDecisions,
    IReadOnlyList<ModerationValidationIssue> Issues,
    IReadOnlyList<string> RepairMessageIds,
    bool RequiresFullRepair)
{
    public bool IsValid => Envelope is not null && Issues.Count == 0;
}

public sealed record ModerationConfirmationCandidate(
    string TemporaryMessageId,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    PolicySetCode PolicySet,
    string RuleCode,
    string? EvidenceKind,
    string? SensitiveDataType,
    IReadOnlyList<string> TriggerPhrases,
    double Confidence,
    ModerationCandidateSource Source);

public sealed record ModerationConfirmationRequest(
    ModerationRequestEnvelopeV3 Envelope,
    IReadOnlyList<ModerationConfirmationCandidate> Candidates);

public sealed record ModerationConfirmationReport(
    bool IsEnvelopeValid,
    IReadOnlyList<ValidatedModerationDecision> ConfirmedDecisions,
    IReadOnlyList<string> RejectedMessageIds,
    IReadOnlyList<ModerationValidationIssue> Issues);
