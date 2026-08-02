using System.Text.RegularExpressions;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public static partial class ModerationReasonNormalizer
{
    public const int MaximumLength = 120;

    public const string ContextualAiReasonInstruction =
        "Write one short German sentence up to 120 characters that explains the concrete violation in this exact message under the selected rule. "
        + "Refer to the message context instead of merely repeating the category title or a generic policy reason. "
        + "Do not mention a rule code, timeout, ban, deletion, confidence, recommendation, or uncertainty.";

    public static bool IsSelfNegating(string? reason)
    {
        var value = CollapseWhitespace().Replace(reason?.Trim() ?? string.Empty, " ")
            .ToLowerInvariant();
        return value.Contains("kein verstoß", StringComparison.Ordinal)
            || value.Contains("keine regelverletzung", StringComparison.Ordinal)
            || value.Contains("kein klarer", StringComparison.Ordinal)
            || value.Contains("nicht eindeutig", StringComparison.Ordinal)
            || value.Contains("unklar", StringComparison.Ordinal)
            || value.Contains("keine moderation", StringComparison.Ordinal);
    }

    public static string Normalize(string? reason, PolicySetCode? policySet)
    {
        return Normalize(reason, ruleCode: null, policySet);
    }

    public static string Normalize(
        string? reason,
        string? ruleCode,
        PolicySetCode? policySet)
    {
        return NormalizeCore(reason, ruleCode, policySet, configuredPublicReason: null);
    }

    public static string Normalize(
        string? reason,
        ModerationRuleDefinition? rule,
        PolicySetCode? policySet)
    {
        return NormalizeCore(
            reason,
            rule?.RuleCode,
            policySet,
            rule?.PublicReason);
    }

    private static string NormalizeCore(
        string? reason,
        string? ruleCode,
        PolicySetCode? policySet,
        string? configuredPublicReason)
    {
        var value = CollapseWhitespace().Replace(reason?.Trim() ?? string.Empty, " ");
        if (value.Length == 0)
        {
            return Fallback(ruleCode, policySet, configuredPublicReason);
        }

        var lower = value.ToLowerInvariant();
        if (LooksEnglish(lower) && !string.IsNullOrWhiteSpace(ruleCode))
        {
            return Fallback(ruleCode, policySet, configuredPublicReason);
        }

        if (lower.Contains("harassment", StringComparison.Ordinal)
            || lower.Contains("profanity directed", StringComparison.Ordinal))
        {
            return "Belästigung oder Beleidigung eines Nutzers oder Streamers.";
        }

        if (lower.Contains("hate speech", StringComparison.Ordinal))
        {
            return "Hassrede oder menschenfeindliche Äußerung.";
        }

        if (lower.Contains("spam", StringComparison.Ordinal))
        {
            return "Wiederholter Spam.";
        }

        if (lower.Contains("backseat", StringComparison.Ordinal)
            || lower.Contains("spoiler", StringComparison.Ordinal))
        {
            return "Unaufgeforderter Spielhinweis oder Spoiler.";
        }

        // English model output is replaced instead of being displayed or sent to
        // Twitch. German output keeps only its first, violation-focused sentence.
        if (LooksEnglish(lower))
        {
            return Fallback(ruleCode, policySet, configuredPublicReason);
        }

        value = IncludeStreamerAsPossibleTarget(value);
        if (value.Length > MaximumLength)
        {
            return Fallback(ruleCode, policySet, configuredPublicReason);
        }

        var firstSentenceEnd = value.IndexOfAny(['.', '!', '?']);
        if (firstSentenceEnd >= 0)
        {
            value = value[..(firstSentenceEnd + 1)];
        }

        return value;
    }

    private static bool LooksEnglish(string value) =>
        EnglishWords().Count(value) >= 2
        || SingleEnglishModerationReason().IsMatch(value);

    private static string Fallback(
        string? ruleCode,
        PolicySetCode? policySet,
        string? configuredPublicReason = null)
    {
        return ValidConfiguredReason(configuredPublicReason) ?? ruleCode switch
        {
            ModerationRuleCodes.SexualContent =>
                "Sexueller oder sexistischer Inhalt gegen einen Nutzer oder Streamer.",
            ModerationRuleCodes.PersonalAttack =>
                "Beleidigung, Belästigung oder unangebrachte persönliche Annäherung an einen Nutzer oder Streamer.",
            ModerationRuleCodes.Hate => "Hassrede oder diskriminierende Aussage.",
            ModerationRuleCodes.Harm =>
                "Drohung oder Aufforderung zu Gewalt oder Selbstverletzung.",
            "G_SEXUALIZATION" => "Sexualisierende Aussage über eine Person.",
            "G_SEXUAL_EXPLICIT" => "Sexuell expliziter Inhalt.",
            "G_INSULT" => "Direkte Beleidigung eines Nutzers oder Streamers.",
            "G_HARASSMENT" =>
                "Gezielte Belästigung eines Nutzers oder Streamers.",
            "G_RACISM" => "Rassistische oder ethnisch abwertende Aussage.",
            "G_SEXISM" => "Sexistische oder frauenfeindliche Aussage.",
            "G_LGBTQ_HATE" => "Queerfeindliche Aussage.",
            "G_ABLEISM" => "Behindertenfeindliche Aussage.",
            "G_HATE_OTHER" => "Menschenfeindliche Hassrede.",
            "G_THREAT" => "Gewaltandrohung gegen eine Person.",
            "G_VIOLENCE" => "Gewaltverherrlichender Inhalt.",
            ModerationRuleCodes.PersonalData => "Veröffentlichung persönlicher Daten.",
            "G_SELF_HARM_INCITEMENT" => "Aufforderung zu Selbstverletzung.",
            "G_SPAM" => "Wiederholter Spam.",
            "G_SCAM" => "Betrugs- oder Phishingversuch.",
            "G_DISRUPTION" => "Gezielte Störung des Chats.",
            ModerationRuleCodes.Advertising =>
                "Unaufgeforderte Werbung oder Eigenwerbung.",
            ModerationRuleCodes.Begging =>
                "Betteln nach Subs, Geschenken oder Giveaways.",
            "G_OTHER_ABUSE" => "Sonstiger Verstoß gegen die Chatregeln.",
            ModerationRuleCodes.Backseatgaming =>
                "Unaufgeforderter Spielhinweis oder Spoiler.",
            ModerationRuleCodes.LocalBlacklist =>
                "Treffer in der manuellen Bot-/Scam-Blacklist.",
            ModerationRuleCodes.SelfPromotion =>
                "Unaufgeforderte Eigenwerbung für einen eigenen Kanal oder Stream.",
            ModerationRuleCodes.BotScam =>
                "Automatisierte oder betrügerische Werbung im Chat.",
            _ when policySet == PolicySetCode.Backseatgaming =>
                "Unaufgeforderter Spielhinweis oder Spoiler.",
            _ => "Verstoß gegen die allgemeinen Chatregeln.",
        };
    }

    private static string? ValidConfiguredReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)
            || reason.Any(char.IsControl))
        {
            return null;
        }

        var value = IncludeStreamerAsPossibleTarget(
            CollapseWhitespace().Replace(reason.Trim(), " "));
        if (value.Length is 0 or > MaximumLength
            || LooksEnglish(value.ToLowerInvariant()))
        {
            return null;
        }

        var firstSentenceEnd = value.IndexOfAny(['.', '!', '?']);
        return firstSentenceEnd >= 0
            ? value[..(firstSentenceEnd + 1)]
            : value;
    }

    private static string IncludeStreamerAsPossibleTarget(string value)
    {
        value = UserPossessiveTarget().Replace(
            value,
            match => char.IsLower(match.Value[0])
                ? "nutzers oder streamers"
                : "Nutzers oder Streamers");
        return UserTarget().Replace(
            value,
            match => char.IsLower(match.Value[0])
                ? "nutzer oder streamer"
                : "Nutzer oder Streamer");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();

    [GeneratedRegex(
        @"\bNutzers\b(?!\s+oder\s+Streamers\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UserPossessiveTarget();

    [GeneratedRegex(
        @"\bNutzer\b(?!\s+oder\s+Streamer\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UserTarget();

    [GeneratedRegex(@"\b(the|this|that|with|against|under|should|content|user|streamer|message|policy|disallowed|removed|remove|delete|timeout|harassment|profanity|directed|instruction|directive|order|violation|abuse|hate|speech|backseat|spoiler|personal|data|threat|sexual|racist|sexist|severe|strong|insult|insults|extreme|ban|banned|bannable|advertising|begging|scam|phishing)\b")]
    private static partial Regex EnglishWords();

    [GeneratedRegex(@"^\s*(harassment|profanity|abuse|hate speech|backseat|backseatgaming|spoiler|personal data|threat|sexual content|racist|sexist|severe insult|strong insult|insult|extreme abuse|advertising|begging|instruction|directive|order|scam|phishing|spam|violation)\s*[.!?]?\s*$")]
    private static partial Regex SingleEnglishModerationReason();
}
