namespace TwitchAI.Infrastructure.Persistence;

public sealed record ChatMessageRecord(
    string TwitchMessageId,
    string BroadcasterId,
    string ChatterUserId,
    string ChatterLoginOriginal,
    string ChatterNameOriginal,
    string MessageTextOriginal,
    string MessageTextNormalized,
    int NormalizationVersion,
    string NormalizedSha256,
    string? CategoryId,
    string? CategoryNameSnapshot,
    DateTimeOffset ReceivedAt,
    string? EventSubNotificationId = null,
    string? LowerText = null,
    string? OriginalSha256 = null,
    string? IdentityFingerprint = null,
    long ChannelContextRevision = 1,
    bool CategoryIsVerified = true,
    long IngressSequence = 0,
    string? SharedSourceChannelId = null,
    string BadgesJson = "{}");

public sealed record TwitchCategoryRecord(
    string CategoryId,
    string DisplayName,
    DateTimeOffset LastSeenAt,
    DateTimeOffset? LastVerifiedAt);

public enum PolicyScopeKind
{
    General,
    Category,
}

public enum ModerationRuleAction
{
    Delete,
    Timeout,
    Ban,
}

public enum RuleEvaluationMode
{
    SemanticAi,
    DeterministicPhrase,
    HumanOnly,
}

public sealed record PolicyScopeRecord(
    long ScopeId,
    string BroadcasterId,
    PolicyScopeKind ScopeKind,
    string? CategoryId,
    long Revision,
    bool Enabled);

public sealed record BlacklistRuleRecord(
    long RuleId,
    long ScopeId,
    string RuleCode,
    string Title,
    string PromptInstruction,
    ModerationRuleAction DefaultAction,
    int? DefaultTimeoutSeconds,
    string PublicReason,
    int Priority,
    bool Enabled,
    long RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    RuleEvaluationMode EvaluationMode = RuleEvaluationMode.SemanticAi);

public sealed record BlacklistPhraseRecord(
    long PhraseId,
    long RuleId,
    string Phrase,
    string NormalizedPhrase,
    bool Enabled,
    long RowVersion,
    DateTimeOffset? ArchivedAt);

public enum AllowlistSubjectKind
{
    UserId,
    UserLogin,
    ExactMessage,
}

public sealed record AllowlistEntryRecord(
    long AllowlistId,
    string BroadcasterId,
    AllowlistSubjectKind SubjectKind,
    string SubjectValue,
    string NormalizedValue,
    bool Enabled,
    long RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt);

public sealed record EffectivePolicySnapshot(
    string BroadcasterId,
    string? CategoryId,
    long GeneralRevision,
    long CategoryRevision,
    string PolicyHash,
    string PromptFragment,
    IReadOnlyList<BlacklistRuleRecord> Rules,
    IReadOnlyList<BlacklistPhraseRecord> Phrases);

public enum ModerationCaseStatus
{
    Pending,
    Executed,
    Discarded,
    Allowlisted,
    ExternallyDeleted,
    Failed,
    StalePolicy,
    SkippedOverload,
}

public sealed record ModerationCaseRecord(
    long CaseId,
    string TwitchMessageId,
    string BroadcasterId,
    string ChatterUserId,
    string ChatterNameSnapshot,
    string MessageTextOriginal,
    string MessageTextNormalized,
    string? CategoryId,
    string? CategoryNameSnapshot,
    string EffectivePolicyHash,
    string ModelId,
    string EnvelopeJson,
    string ProposedAction,
    int? ProposedTimeoutSeconds,
    string? ViolationRuleCode,
    double? Confidence,
    ModerationCaseStatus Status,
    long RowVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

public sealed record WhitelistMatch(
    bool IsMatch,
    long? AllowlistId,
    AllowlistSubjectKind? SubjectKind);

public enum FeedbackSourceKind
{
    OperatorExecute,
    OperatorDiscard,
    ExternalDelete,
}

public sealed record LearningExampleRecord(
    long FeedbackId,
    FeedbackSourceKind SourceKind,
    string LabelAction,
    string? RuleCode,
    string? CategoryId,
    string MessageNormalized,
    DateTimeOffset ConfirmedAt);
