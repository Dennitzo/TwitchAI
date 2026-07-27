namespace TwitchAI.Core.Models;

public enum PolicySetCode
{
    General,
    Backseatgaming,
}

public enum PhraseManualOverride
{
    Auto,
    Active,
    Inactive,
}

public static class ModerationRuleCodes
{
    public const string OtherAbuse = "G_OTHER_ABUSE";
    public const string GeneralPhrase = OtherAbuse;
    public const string PersonalData = "G_PERSONAL_DATA";
    public const string Backseatgaming = "BACKSEATGAMING";
    public const string BackseatgamingPromptInstruction =
        "BACKSEATGAMING nur bei klarer, unaufgeforderter Spielinformation: konkrete Handlungsanweisung, Missions-/Rätsellösung oder Spoiler zu kommendem Ereignis/versteckter Gefahr. Sonst none: einzelne Wörter/Fragmente (z. B. zorro, nichts), allgemeine Reaktionen, Fragen, Scherze, Meinungen, Zustimmung/Ablehnung, Streamer-/Chat-Meta oder Text ohne klaren spielbezogenen Handlungs-/Informationsgehalt; Zitat, Verneinung und Kontext beachten.";

    public static bool AllowsTriggerLearning(string? ruleCode) =>
        !string.Equals(ruleCode, PersonalData, StringComparison.Ordinal);
}

public sealed record ModerationRuleDefinition(
    string RuleCode,
    PolicySetCode PolicySet,
    string Title,
    string PromptInstruction,
    ModerationActionKind DefaultAction,
    int? DefaultTimeoutSeconds,
    string PublicReason,
    int Priority = 100,
    bool AllowTriggerLearning = true);

public sealed record PolicySetRevision(PolicySetCode Code, long Revision);

public sealed record EffectivePolicySnapshot(
    IReadOnlyList<PolicySetRevision> ActivePolicySets,
    string RevisionFingerprint)
{
    public IReadOnlyList<ModerationRuleDefinition> ActiveRules { get; init; } = [];

    public bool Contains(PolicySetCode code) => ActivePolicySets.Any(item => item.Code == code);

    public ModerationRuleDefinition? FindRule(string? ruleCode) =>
        string.IsNullOrWhiteSpace(ruleCode)
            ? null
            : ActiveRules.FirstOrDefault(
                item => string.Equals(
                    item.RuleCode,
                    ruleCode,
                    StringComparison.Ordinal));
}

public sealed record PhraseRule(
    long PhraseId,
    string RuleCode,
    PolicySetCode PolicySet,
    string RuleNormalizedPhrase,
    PhraseManualOverride ManualOverride,
    int PositiveConfirmations,
    int NegativeConfirmations,
    bool IsArchived)
{
    public double PositiveRatio =>
        PositiveConfirmations + NegativeConfirmations == 0
            ? 0
            : (double)PositiveConfirmations / (PositiveConfirmations + NegativeConfirmations);

    public bool IsActive =>
        !IsArchived
        && ManualOverride switch
        {
            PhraseManualOverride.Active => true,
            PhraseManualOverride.Inactive => false,
            _ => PositiveConfirmations >= 1 && PositiveRatio >= 0.60,
        };
}

public sealed record BlacklistMatch(
    long PhraseId,
    string RuleCode,
    PolicySetCode PolicySet,
    string MatchedPhrase);
