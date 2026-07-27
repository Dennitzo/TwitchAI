using System.Text.RegularExpressions;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public static partial class ModerationReasonNormalizer
{
    public const int MaximumLength = 120;

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
            return "Belästigung oder Beleidigung gegen einen Nutzer.";
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
        EnglishWords().Count(value) >= 2;

    private static string Fallback(
        string? ruleCode,
        PolicySetCode? policySet,
        string? configuredPublicReason = null)
    {
        return ValidConfiguredReason(configuredPublicReason) ?? ruleCode switch
        {
            "G_SEXUALIZATION" => "Sexualisierende Aussage über eine Person.",
            "G_SEXUAL_EXPLICIT" => "Sexuell expliziter Inhalt.",
            "G_INSULT" => "Direkte Beleidigung gegen einen Nutzer.",
            "G_HARASSMENT" => "Gezielte Belästigung eines Nutzers.",
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
            "G_OTHER_ABUSE" => "Sonstiger Verstoß gegen die Chatregeln.",
            ModerationRuleCodes.Backseatgaming =>
                "Unaufgeforderter Spielhinweis oder Spoiler.",
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

        var value = CollapseWhitespace().Replace(reason.Trim(), " ");
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

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();

    [GeneratedRegex(@"\b(the|this|that|with|against|under|should|content|user|message|policy|disallowed|removed|remove|delete|timeout|harassment|profanity|directed|violation|abuse|hate|speech|backseat|spoiler|personal|data|threat|sexual|racist|sexist)\b")]
    private static partial Regex EnglishWords();
}
