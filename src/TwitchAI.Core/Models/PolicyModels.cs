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

public enum ModerationRuleEvaluationMode
{
    SemanticAi,
    DeterministicPhrase,
    HumanOnly,
}

public static class ModerationRuleCodes
{
    public const int MinimumEmoteSpamCount = 10;
    public const int MinimumRepeatedPhraseSpamCount = 5;
    public const int MinimumSymbolSpamCount = 20;
    public const double MinimumSymbolSpamRatio = 0.70d;

    public const string OtherAbuse = "G_OTHER_ABUSE";
    public const string GeneralPhrase = OtherAbuse;
    public const string Insult = "G_INSULT";
    public const string PersonalAttack = "G_PERSONAL_ATTACK";
    public const string SexualContent = "G_SEXUAL_CONTENT";
    public const string Hate = "G_HATE";
    public const string Harm = "G_HARM";
    public const string PersonalData = "G_PERSONAL_DATA";
    public const string Advertising = "G_ADVERTISING";
    public const string Begging = "G_BEGGING";
    public const string Spam = "G_SPAM";
    public const string Disruption = "G_DISRUPTION";
    public const string Backseatgaming = "BACKSEATGAMING";
    public const string LocalBlacklist = "LOCAL_BLACKLIST";
    public const string SelfPromotion = "G_SELF_PROMOTION";
    public const string BotScam = "G_BOT_SCAM";

    public const string PersonalAttackPromptInstruction =
        "Nur bei identifizierbarer Zielperson und direkter, unmissverständlicher persönlicher Beschimpfung, Herabsetzung, Einschüchterung oder unerwünschter intimer/persönlicher Annäherung. Manuelle Phrasen sind zusätzlicher Kontext, aber keine automatische Entscheidung. Kritik an Handlung, Spiel, Inhalt, Entscheidung oder Leistung, zielloser Fluch, neutrale Anweisung, freundschaftliche Neckerei, Selbstbezug sowie Zitat/Bericht ohne direkte Annäherung sind none. Wiederholte Belästigung nur annehmen, wenn der übergebene Kontext die Wiederholung belegt.";

    public const string SexualContentPromptInstruction =
        "Nur bei sexueller oder intimer Bewertung, Objektifizierung oder Zuschreibung über eine Person, sexueller Aufforderung oder Anmache, eindeutig expliziter sexueller Beschreibung oder klarer Abwertung beziehungsweise feindseliger Generalisierung wegen Frau-/Mannsein oder Geschlechterrolle. Neutrale Körper-, Kleidungs-, Fitness-, Medizin-, Beziehungs- oder Aufklärungsbezüge, bloße Genre-/Adult-Content-Nennung, neutrale oder positive Geschlechtsnennung und mehrdeutige Begriffe sind none. Abwertung wegen sexueller Orientierung oder Transidentität gehört G_HATE.";

    public const string HatePromptInstruction =
        "Nur wenn eine Person oder Gruppe wegen ethnischer Zugehörigkeit, Hautfarbe, Nationalität, Religion, Behinderung, sexueller Orientierung, Trans-/nichtbinärer Identität oder eines vergleichbaren geschützten Merkmals klar abgewertet, entmenschlicht, ausgeschlossen oder feindselig verallgemeinert wird. Neutrale oder positive Erwähnung, Selbstbezeichnung, Aussehensbeschreibung, Name, Mehrdeutigkeit und bloßes Zitat oder Bericht sind none. Frauen- oder männerfeindliche Aussagen gehören G_SEXUAL_CONTENT.";

    public const string HarmPromptInstruction =
        "Nur wenn die Nachricht selbst einer realen Zielperson zukünftige körperliche Gewalt androht, zu realer Gewalt auffordert oder sie ausdrücklich befürwortet oder eine identifizierbare Person direkt zu Selbstverletzung beziehungsweise Suizid auffordert. Gewaltwörter, Unfall- oder Gefahrenbeschreibungen, Warnungen, Vergangenheit, Spiel oder Fiktion, Metaphern, Übertreibungen, Scherze, Hypothesen, Fragen, Selbstoffenbarung und Unterstützung ohne Aufforderung sind none.";

    public const string PersonalDataPromptInstruction =
        "Nur konkrete nichtöffentliche persönliche Daten, eine gezielte Anfrage nach privaten Identifikatoren oder die Androhung ihrer Veröffentlichung. Allgemeine Ortsfragen, öffentliche Informationen und freiwillige Selbstauskunft sind none; keine personenbezogene Triggerphrase ausgeben oder lernen.";

    public const string BeggingPromptInstruction =
        "Nur eine ausdrückliche Bitte oder Forderung nach einem kostenlosen Sub, Geld, Geschenk oder einem weiteren Giveaway für einen erkennbaren Begünstigten. Dank, Wünsche, Preisangaben, hypothetische Aussagen und normale Empfehlungen sind none.";
    private const string InsultClassificationPromptInstruction =
        "Nur wenn eine Person eindeutig direkt adressiert und unmissverständlich beschimpft/herabgesetzt wird. None: zielloser Fluch, neutrale Anrede/Anweisung, Situations-/Spiel-/Handlungs-/Leistungskritik, Neckerei/Witz/Sarkasmus, Selbstbezug, Zitat, Verneinung, Mehrdeutigkeit. Username/@, lokaler Treffer oder Learning reichen nie. Schwere Einzelbeschimpfung nur bei klarer Direktanrede; im Zweifel none.";
    public const string InsultPromptInstruction =
        InsultClassificationPromptInstruction
        + " Trefferphrase: genau der kleinste zusammenh\u00e4ngende Lowercase-Beleg mit Zielperson und eigentlicher Herabsetzung; Username/@, Pronomen, F\u00fclltext oder Einzelwort allein nie. Ohne solchen Beleg none.";
    public const string SpamPromptInstruction =
        "G_SPAM nur bei lokal belegtem Flooding: mindestens 10 Emotes in einer Nachricht; eine direkt wiederholte Wortfolge oder kurze Textphrase mindestens fünfmal in derselben Nachricht; oder mindestens 20 Sonderzeichen bei 70 Prozent Symbolanteil. Ein bis neun Emotes, normale Wiederholungen, Satzzeichen, Reaktionen, belanglose und themenfremde Kommentare sind none. Nachrichten verschiedener User niemals zusammenzählen.";
    public const string SpamClassifierGuidance =
        """
        G_SPAM has these hard criteria:
        - Emote spam requires at least 10 emote occurrences in one message. One through nine emotes alone are none.
        - Do not relabel one through nine repeated emote names as text spam or disruption.
        - Text spam includes a non-emote word sequence or short phrase repeated directly at least five times in one message. When hasRepeatedPhraseSpam is true, classify G_SPAM.
        - Special-character spam means long or dense repeated symbol/punctuation sequences that flood chat; ordinary punctuation is none.
        Classify qualifying emote, text, and special-character flooding as G_SPAM, not G_DISRUPTION.
        These criteria override local phrase matches and learning examples. When twitchEmoteCount is supplied, treat it as authoritative for native Twitch emotes.
        """;
    public const string BackseatgamingPromptInstruction =
        "BACKSEATGAMING: unaufgeforderte konkrete Spielhilfe an den Streamer, etwa Befehl/Empfehlung, Route, nächster Schritt, Build, Item, Inventar, Ressource, Kampf/Taktik, Lösung, Spoiler oder versteckte Gefahr. Auch kurze, englische oder fehlerhafte Befehle zählen bei klarem Spielbezug. Eine Aussage ohne Imperativ zählt, wenn sie unmittelbar verrät, was zu tun oder zu beachten ist. Manuelle Phrasen sind Kontextsignale und werden zusammen mit Sprechakt und Spielkontext bewertet. None: echte Informationsfrage, Zitat, Verneinung, Selbstbeschreibung, Drittbericht, allgemeine Diskussion/Meinung ohne konkrete Hilfe oder isoliertes mehrdeutiges Fragment.";

    public const string SelfPromotionPromptInstruction =
        "Nur klare unaufgeforderte Werbung für den eigenen Kanal, Stream, Content oder die eigene Reichweite. Manuelle Phrasen sind Kontextsignale, aber keine Voraussetzung und kein automatisches Urteil. Ein Link, Channelname, Zitat, eine Warnung oder eine mehrdeutige Verwendung ohne tatsächliche Eigenwerbung ist none. Gewöhnliche klare Eigenwerbung erfordert 86400 Sekunden Timeout; Ban nur bei aggressiver, massenhafter, automatisiert wirkender oder kommerziell-bösartiger Bewerbung.";
    public const string BotScamPromptInstruction =
        "Nur klar automatisierte, betrügerische oder kommerzielle Werbung, insbesondere Viewer-/Follower-/Sub-Manipulation oder Phishing. Manuelle Phrasen sind Kontextsignale, aber keine Voraussetzung und kein automatisches Urteil. URL, Channelname, Zitat, Bericht oder Warnung allein ist none. Bei eindeutig bestätigtem Bot-/Scam-Kontext Ban, bei Unsicherheit none.";
    public static bool AllowsTriggerLearning(string? ruleCode) =>
        !string.Equals(ruleCode, PersonalData, StringComparison.Ordinal);

    public static bool IsSemanticAiRule(string? ruleCode) => ruleCode is
        PersonalData or Harm or Hate or SexualContent or PersonalAttack
        or Begging or Spam or Backseatgaming or SelfPromotion or BotScam;

    public static bool RequiresLocalPhraseMatch(string? ruleCode) => false;
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
    bool AllowTriggerLearning = true)
{
    public ModerationRuleEvaluationMode EvaluationMode { get; init; } =
        ModerationRuleEvaluationMode.SemanticAi;
}

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
    string MatchedPhrase)
{
    public double Score { get; init; } = 1d;

    public string MatchKind { get; init; } = "manual_phrase";
}
