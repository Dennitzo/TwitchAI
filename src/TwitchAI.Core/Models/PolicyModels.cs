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
    public const int MinimumEmoteSpamCount = 20;

    public const string OtherAbuse = "G_OTHER_ABUSE";
    public const string GeneralPhrase = OtherAbuse;
    public const string Insult = "G_INSULT";
    public const string PersonalData = "G_PERSONAL_DATA";
    public const string Advertising = "G_ADVERTISING";
    public const string Begging = "G_BEGGING";
    public const string Spam = "G_SPAM";
    public const string Disruption = "G_DISRUPTION";
    public const string Backseatgaming = "BACKSEATGAMING";
    private const string InsultClassificationPromptInstruction =
        "Nur wenn eine Person eindeutig direkt adressiert und unmissverständlich beschimpft/herabgesetzt wird. None: zielloser Fluch, neutrale Anrede/Anweisung, Situations-/Spiel-/Handlungs-/Leistungskritik, Neckerei/Witz/Sarkasmus, Selbstbezug, Zitat, Verneinung, Mehrdeutigkeit. Username/@, lokaler Treffer oder Learning reichen nie. Schwere Einzelbeschimpfung nur bei klarer Direktanrede; im Zweifel none.";
    public const string InsultPromptInstruction =
        InsultClassificationPromptInstruction
        + " Trefferphrase: genau der kleinste zusammenh\u00e4ngende Lowercase-Beleg mit Zielperson und eigentlicher Herabsetzung; Username/@, Pronomen, F\u00fclltext oder Einzelwort allein nie. Ohne solchen Beleg none.";
    public const string SpamPromptInstruction =
        "G_SPAM nur bei mindestens 20 Emotes in einer Nachricht, bei deutlich wiederholtem Text-/Phrasen-Flooding oder bei massiven wiederholten Sonderzeichenfolgen. Ein bis neunzehn Emotes allein sowie normale Wiederholungen, Satzzeichen und Reaktionen sind erlaubt; wenige Emotes niemals als Textspam oder Chatstörung umdeuten. Qualifizierendes Emote-, Text- und Sonderzeichen-Flooding G_SPAM statt G_DISRUPTION zuordnen.";
    public const string SpamClassifierGuidance =
        """
        G_SPAM has these hard criteria:
        - Emote spam requires at least 20 emote occurrences in one message. One through nineteen emotes alone are none.
        - Do not relabel one through nineteen repeated emote names as text spam or disruption.
        - Text spam means clearly excessive repetition or flooding of text, words, or short phrases.
        - Special-character spam means long or dense repeated symbol/punctuation sequences that flood chat; ordinary punctuation is none.
        Classify qualifying emote, text, and special-character flooding as G_SPAM, not G_DISRUPTION.
        These criteria override local phrase matches and learning examples. When twitchEmoteCount is supplied, treat it as authoritative for native Twitch emotes.
        """;
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
