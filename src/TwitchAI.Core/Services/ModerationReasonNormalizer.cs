using System.Text.RegularExpressions;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public static partial class ModerationReasonNormalizer
{
    public const int MaximumLength = 120;

    public const string ContextualAiReasonInstruction =
        "Write one new message-specific reason in natural German for every timeout or ban (max 72 characters); English or another language is invalid. "
        + "Prefer a concise neutral noun phrase that directly names the violation, for example 'Unaufgefordertes Backseatgaming an den Streamer.'. "
        + "Never start with 'Die Nachricht' or refer to the message as the acting subject. If you use a complete sentence, use neutral third-person past tense. "
        + "Never include @mentions or a username tagged in the current message, with or without @; use 'eines Nutzers oder des Streamers' instead. "
        + "Never address the chatter with du, dich, dir, dein, ihr, euch, or euer. "
        + "Semantically summarize the concrete act so the reason works without the category title. "
        + "Never quote or copy triggerPhrases or any message excerpt; keep evidence only in triggerPhrases. "
        + "Never copy or paraphrase a generic rule description. For example, do not write 'Beleidigung, Belästigung oder unangebrachte persönliche Annäherung an einen Nutzer oder Streamer.'. "
        + "Never include a machine rule code or category ID such as G_SEXUAL_CONTENT. "
        + "Do not mention a timeout, ban, deletion, confidence, recommendation, or uncertainty.";

    private static readonly HashSet<string> GenericAiReasons =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Belästigung oder Beleidigung eines Nutzers oder Streamers.",
            "Beleidigung, Belästigung oder unangebrachte persönliche Annäherung an einen Nutzer oder Streamer.",
            "Direkte Beleidigung eines Nutzers oder Streamers.",
            "Gezielte Belästigung eines Nutzers oder Streamers.",
            "Unangebrachte persönliche Annäherung.",
            "Hassrede oder menschenfeindliche Äußerung.",
            "Hassrede oder diskriminierende Aussage.",
            "Sexueller oder sexistischer Inhalt gegen einen Nutzer oder Streamer.",
            "Sexualisierende Aussage über eine Person.",
            "Sexuell expliziter Inhalt.",
            "Drohung oder Aufforderung zu Gewalt oder Selbstverletzung.",
            "Gewaltandrohung gegen eine Person.",
            "Gewaltverherrlichender Inhalt.",
            "Veröffentlichung persönlicher Daten.",
            "Aufforderung zu Selbstverletzung.",
            "Wiederholter Spam.",
            "Nachricht wurde von AutoMod als Spam erkannt.",
            "Text ohne Kontext.",
            "Betrugs- oder Phishingversuch.",
            "Gezielte Störung des Chats.",
            "Unaufgeforderte Werbung oder Eigenwerbung.",
            "Betteln nach Subs, Geschenken oder Giveaways.",
            "Sonstiger Verstoß gegen die Chatregeln.",
            "Verstoß gegen die allgemeinen Chatregeln.",
            "Unaufgeforderter Spielhinweis oder Spoiler.",
            "Treffer in der manuellen Bot-/Scam-Blacklist.",
            "Unaufgeforderte Eigenwerbung für einen eigenen Kanal oder Stream.",
            "Automatisierte oder betrügerische Werbung im Chat.",
        };

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

    public static bool TryNormalizeContextualAiReason(
        string? reason,
        ModerationRuleDefinition? rule,
        out string normalizedReason)
    {
        return TryNormalizeContextualAiReason(
            reason,
            rule,
            sourceText: null,
            out normalizedReason);
    }

    public static bool TryNormalizeContextualAiReason(
        string? reason,
        ModerationRuleDefinition? rule,
        string? sourceText,
        out string normalizedReason)
    {
        normalizedReason = string.Empty;
        if (string.IsNullOrWhiteSpace(reason)
            || reason.Any(char.IsControl))
        {
            return false;
        }

        var value = CollapseWhitespace().Replace(reason.Trim(), " ");
        if (value.Length > MaximumLength
            || !LooksGermanReason(value)
            || value.Contains('@', StringComparison.Ordinal)
            || ContainsMentionedUsername(value, sourceText)
            || MachineRuleCode().IsMatch(value)
            || UsesDirectSecondPerson(value)
            || StartsWithMessageReference(value)
            || UsesPresentTense(value)
            || ContainsQuotationMark(value))
        {
            return false;
        }

        value = IncludeStreamerAsPossibleTarget(value);
        var firstSentenceEnd = value.IndexOfAny(['.', '!', '?']);
        if (firstSentenceEnd >= 0)
        {
            value = value[..(firstSentenceEnd + 1)];
        }

        var configuredReason = ValidConfiguredReason(rule?.PublicReason);
        if (value.Length is 0 or > MaximumLength
            || GenericAiReasons.Any(reason => SameReason(value, reason))
            || configuredReason is not null
                && SameReason(value, configuredReason))
        {
            return false;
        }

        normalizedReason = value;
        return true;
    }

    private static bool ContainsMentionedUsername(
        string reason,
        string? sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return false;
        }

        foreach (Match match in Mention().Matches(sourceText))
        {
            var username = match.Groups[1].Value;
            if (username.Length > 0
                && Regex.IsMatch(
                    reason,
                    $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(username)}(?![\p{{L}}\p{{N}}_])",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsTriggerPhraseExcerpt(
        string? reason,
        IEnumerable<string>? triggerPhrases)
    {
        if (string.IsNullOrWhiteSpace(reason) || triggerPhrases is null)
        {
            return false;
        }

        var reasonTokens = Tokenize(reason);
        if (reasonTokens.Count < 2)
        {
            return false;
        }

        foreach (var triggerPhrase in triggerPhrases)
        {
            var triggerTokens = Tokenize(triggerPhrase);
            var sequenceLength = triggerTokens.Count switch
            {
                0 => 0,
                1 => 1,
                _ => 2,
            };
            if (sequenceLength == 0)
            {
                continue;
            }

            for (var triggerStart = 0;
                 triggerStart <= triggerTokens.Count - sequenceLength;
                 triggerStart++)
            {
                for (var reasonStart = 0;
                     reasonStart <= reasonTokens.Count - sequenceLength;
                     reasonStart++)
                {
                    var matches = true;
                    for (var offset = 0; offset < sequenceLength; offset++)
                    {
                        if (!string.Equals(
                                reasonTokens[reasonStart + offset],
                                triggerTokens[triggerStart + offset],
                                StringComparison.Ordinal))
                        {
                            matches = false;
                            break;
                        }
                    }

                    if (matches)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool SameReason(string left, string right) =>
        string.Equals(
            left.Trim().TrimEnd('.', '!', '?'),
            right.Trim().TrimEnd('.', '!', '?'),
            StringComparison.OrdinalIgnoreCase);

    private static bool UsesDirectSecondPerson(string value) =>
        DirectSecondPerson().IsMatch(value);

    private static bool StartsWithMessageReference(string value) =>
        value.StartsWith("Die Nachricht", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("Diese Nachricht", StringComparison.OrdinalIgnoreCase);

    private static bool UsesPresentTense(string value) =>
        PresentTense().IsMatch(value);

    private static bool UsesRequiredPastTense(string value) =>
        RequiredPastTense().IsMatch(value);

    private static bool ContainsQuotationMark(string value) =>
        value.IndexOfAny(['\'', '"', '`', '„', '“', '‚', '‘', '«', '»']) >= 0;

    private static List<string> Tokenize(string? value)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return result;
        }

        var token = new System.Text.StringBuilder();
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
            }
            else if (token.Length > 0)
            {
                result.Add(token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            result.Add(token.ToString());
        }

        return result;
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
        if ((!LooksGermanReason(value)
                || value.Contains('@', StringComparison.Ordinal)
                || MachineRuleCode().IsMatch(value))
            && !string.IsNullOrWhiteSpace(ruleCode))
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
        if (!LooksGermanReason(value)
            || value.Contains('@', StringComparison.Ordinal)
            || MachineRuleCode().IsMatch(value))
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

    private static bool LooksGermanReason(string value) =>
        !LooksEnglish(value.ToLowerInvariant())
        && !StrongEnglishReasonWord().IsMatch(value)
        && (GermanLanguageSignal().IsMatch(value)
            || GermanModerationWord().IsMatch(value));

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
            || !LooksGermanReason(value)
            || value.Contains('@', StringComparison.Ordinal)
            || MachineRuleCode().IsMatch(value))
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
        value = StreamerPossessiveTarget().Replace(
            value,
            "eines Nutzers oder des Streamers");
        value = UserPossessiveTarget().Replace(
            value,
            "eines Nutzers oder des Streamers");
        return UserTarget().Replace(
            value,
            match => char.IsLower(match.Value[0])
                ? "nutzer oder streamer"
                : "Nutzer oder Streamer");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();

    [GeneratedRegex(@"@([\p{L}\p{N}_]{1,25})")]
    private static partial Regex Mention();

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}_])(?:G|B|LOCAL|TWITCH)_[A-Z0-9_]+(?![\p{L}\p{N}_])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MachineRuleCode();

    [GeneratedRegex(
        @"\b(the|this|that|with|against|under|should|user|message|policy|disallowed|removed|remove|delete|harassment|profanity|directed|instruction|directive|order|violation|abuse|targeted|conduct|threat|racist|sexist|severe|strong|insult|extreme|banned|bannable|advertising|begging|warning)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StrongEnglishReasonWord();

    [GeneratedRegex(
        @"[äöüß]|\b(der|die|das|den|dem|des|ein|eine|einer|eines|einem|einen|und|oder|an|auf|aus|bei|beim|durch|für|gegen|im|in|mit|nach|ohne|über|unter|von|vor|wegen|zu|zum|zur|als|wurde|wurden|hat|haben|nicht|kein|keine|keiner|keines)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GermanLanguageSignal();

    [GeneratedRegex(
        @"\b(beleidig|belästig|herabsetz|abwert|bedroh|droh|gewalt|hass|diskrimin|sexuell|sexist|rassist|queerfeind|behindertenfeind|persönlich|daten|veröffentlich|aufforder|annäher|einschüchter|entmensch|verunglimpf|spam|flood|wiederhol|wortfolge|zeichen|stör|werb|eigenwerb|betrug|betrüger|bettel|gebettel|backseatgaming|spielhinweis|spoiler|anweisung|scam|phishing|chatregel|respektlos|unangemess|unaufgefordert|inhalt|nutzer|streamer|chat|regelverstoß|verstoß|grund)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GermanModerationWord();

    [GeneratedRegex(
        @"\b(du|dich|dir|dein|deine|deiner|deines|deinem|deinen|deins|euch|euer|eure|eurer|eures|eurem|euren|euers)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DirectSecondPerson();

    [GeneratedRegex(
        @"\b(wird|werden)\b|\b(die\s+nachricht|die\s+aussage|der\s+beitrag|der\s+kommentar)\s+(beleidigt|belästigt|bedroht|enthält|fordert|veröffentlicht|bewirbt|bettelt|spammt|verwendet|äußert|richtet|nennt|macht|drängt|kündigt)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PresentTense();

    [GeneratedRegex(@"\b(hat|haben|wurde|wurden)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RequiredPastTense();

    [GeneratedRegex(
        @"\b(?:eines|des)\s+Nutzers(?:\s+oder\s+(?:des\s+)?Streamers)?\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex UserPossessiveTarget();

    [GeneratedRegex(
        @"(?<!oder\s)\bdes\s+Streamers\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex StreamerPossessiveTarget();

    [GeneratedRegex(
        @"\bNutzer\b(?!\s+oder\s+Streamer\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex UserTarget();

    [GeneratedRegex(@"\b(the|this|that|with|against|under|should|content|user|streamer|message|policy|disallowed|removed|remove|delete|timeout|harassment|profanity|directed|instruction|directive|order|violation|abuse|hate|speech|backseat|spoiler|personal|data|threat|sexual|racist|sexist|severe|strong|insult|insults|extreme|ban|banned|bannable|advertising|begging|scam|phishing)\b")]
    private static partial Regex EnglishWords();

    [GeneratedRegex(@"^\s*(harassment|profanity|abuse|hate speech|backseat|backseatgaming|spoiler|personal data|threat|sexual content|racist|sexist|severe insult|strong insult|insult|extreme abuse|advertising|begging|instruction|directive|order|scam|phishing|spam|violation)\s*[.!?]?\s*$")]
    private static partial Regex SingleEnglishModerationReason();
}
