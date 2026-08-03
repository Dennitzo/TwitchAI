using System.Security.Cryptography;
using System.Text;

namespace TwitchAI.Core.Models;

public enum AiValidationAttemptKind
{
    Initial,
    Repair,
    Confirmation,
    LocalFallback,
}

public enum AiValidationWorkloadKind
{
    LiveModeration,
    LearningTest,
}

public enum AiValidationOutcome
{
    Valid,
    Repaired,
    Fallback,
    Rejected,
}

/// <summary>
/// Privacy-preserving metadata for an AI response. The raw response is hashed
/// in memory and is never retained by the diagnostics model or store.
/// </summary>
public sealed record AiValidationResponseFingerprint(
    int CharacterCount,
    string? Sha256)
{
    public static AiValidationResponseFingerprint None { get; } = new(0, null);

    public static AiValidationResponseFingerprint FromRawResponse(string rawResponse)
    {
        ArgumentNullException.ThrowIfNull(rawResponse);
        return new AiValidationResponseFingerprint(
            rawResponse.Length,
            Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(rawResponse))));
    }
}

/// <summary>
/// Append-only diagnostics for one validation stage. This type intentionally
/// contains neither chat content, model output nor human-readable error text.
/// </summary>
public sealed record AiValidationAttempt(
    string CorrelationId,
    string? ParentCorrelationId,
    AiValidationAttemptKind AttemptKind,
    AiValidationWorkloadKind WorkloadKind,
    string ContractVersion,
    string SchemaVersion,
    int MessageCount,
    string PolicyHash,
    IReadOnlyList<string> ValidationErrorCodes,
    int AffectedMessageCount,
    ModerationPromptCharacterCounts PromptCharacters,
    AiValidationResponseFingerprint Response,
    AiValidationOutcome Outcome,
    DateTimeOffset RecordedAtUtc);
