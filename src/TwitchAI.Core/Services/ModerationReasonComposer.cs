using System.Text.RegularExpressions;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed record ModerationReasonComposition(
    string ViolationReason,
    string Guidance,
    string Reason);

public static partial class ModerationReasonComposer
{
    public const int MaximumViolationReasonLength = 72;
    public const int MaximumGuidanceLength = 45;

    public static readonly string StructuredAiReasonInstruction =
        "For timeout and ban, return two separate German fields: violationReason and guidance. "
        + "violationReason must follow all violation-reason rules below. guidance must be a short, polite, clear instruction for future behavior. "
        + "Vary its natural wording; do not copy a stock phrase. guidance must not name or address any person, username, @mention, target role, message excerpt, trigger phrase, rule code, action, duration, punishment, or threat. "
        + "Do not use du, dich, dir, dein, ihr, euch, Sie, or Ihnen. Do not refer to 'die Nachricht'. "
        + $"violationReason is at most {MaximumViolationReasonLength} characters and guidance is at most {MaximumGuidanceLength} characters. "
        + "For delete, return empty violationReason and empty guidance.";

    public static readonly string StructuredRequiredAiReasonInstruction =
        StructuredAiReasonInstruction
            .Replace(
                "For timeout and ban, return two separate German fields",
                "For every selected moderation action, return two non-empty separate German fields",
                StringComparison.Ordinal)
            .Replace(
                "For delete, return empty violationReason and empty guidance.",
                "Delete also requires a concrete violationReason and guidance for human review.",
                StringComparison.Ordinal);

    public static bool TryComposeAiReason(
        string? violationReason,
        string? guidance,
        ModerationActionKind action,
        ModerationRuleDefinition? rule,
        string? sourceText,
        IReadOnlyList<string>? triggerPhrases,
        out ModerationReasonComposition composition,
        out string errorCode)
    {
        composition = new(string.Empty, string.Empty, string.Empty);
        errorCode = string.Empty;
        if (action is ModerationActionKind.None or ModerationActionKind.Delete)
        {
            if (!string.IsNullOrWhiteSpace(violationReason)
                || !string.IsNullOrWhiteSpace(guidance))
            {
                errorCode = "reason_not_allowed";
                return false;
            }

            return true;
        }

        if (action is not (ModerationActionKind.Timeout or ModerationActionKind.Ban))
        {
            errorCode = "reason_action_invalid";
            return false;
        }

        if (!ModerationReasonNormalizer.TryNormalizeContextualAiReason(
                violationReason,
                rule,
                sourceText,
                out var normalizedViolation))
        {
            errorCode = "violation_reason_invalid";
            return false;
        }

        normalizedViolation = EnsurePeriod(normalizedViolation);
        if (normalizedViolation.Length > MaximumViolationReasonLength)
        {
            errorCode = "violation_reason_invalid";
            return false;
        }

        var normalizedGuidance = EnsurePeriod(NormalizePart(guidance));
        if (!IsValidGuidance(normalizedGuidance, sourceText, triggerPhrases))
        {
            errorCode = "guidance_invalid";
            return false;
        }

        if (ModerationReasonNormalizer.ContainsTriggerPhraseExcerpt(
                normalizedViolation,
                triggerPhrases))
        {
            errorCode = "reason_contains_trigger_phrase";
            return false;
        }

        var combined = $"{normalizedViolation} {normalizedGuidance}";
        if (combined.Length > ModerationReasonNormalizer.MaximumLength)
        {
            errorCode = "reason_too_long";
            return false;
        }

        composition = new(
            normalizedViolation,
            normalizedGuidance,
            combined);
        return true;
    }

    public static bool TryComposeReasonOnly(
        string? reason,
        ModerationActionKind action,
        ModerationRuleDefinition? rule,
        IReadOnlyList<string>? triggerPhrases,
        out ModerationReasonComposition composition,
        out string errorCode)
    {
        return TryComposeReasonOnly(
            reason,
            action,
            rule,
            sourceText: null,
            triggerPhrases,
            out composition,
            out errorCode);
    }

    public static bool TryComposeReasonOnly(
        string? reason,
        ModerationActionKind action,
        ModerationRuleDefinition? rule,
        string? sourceText,
        IReadOnlyList<string>? triggerPhrases,
        out ModerationReasonComposition composition,
        out string errorCode)
    {
        composition = new(string.Empty, string.Empty, string.Empty);
        errorCode = string.Empty;
        if (action is ModerationActionKind.None)
        {
            errorCode = "reason_action_invalid";
            return false;
        }

        if (!ModerationReasonNormalizer.TryNormalizeContextualAiReason(
                reason,
                rule,
                sourceText,
                out var normalized))
        {
            errorCode = "reason_invalid";
            return false;
        }

        normalized = EnsurePeriod(normalized);
        if (string.Equals(
                rule?.RuleCode,
                ModerationRuleCodes.Backseatgaming,
                StringComparison.OrdinalIgnoreCase)
            && !normalized.Contains("Backseatgaming", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "reason_category_missing";
            return false;
        }

        if (normalized.Length > ModerationReasonNormalizer.MaximumLength
            || ModerationReasonNormalizer.ContainsTriggerPhraseExcerpt(
                normalized,
                triggerPhrases))
        {
            errorCode = "reason_invalid";
            return false;
        }

        composition = new(normalized, string.Empty, normalized);
        return true;
    }

    private static bool IsValidGuidance(
        string value,
        string? sourceText,
        IReadOnlyList<string>? triggerPhrases)
    {
        if (value.Length is 0 or > MaximumGuidanceLength
            || value.Any(char.IsControl)
            || value.Contains('@', StringComparison.Ordinal)
            || QuotationMark().IsMatch(value)
            || DirectAddress().IsMatch(value)
            || DirectImperative().IsMatch(value)
            || PersonOrTarget().IsMatch(value)
            || RuleActionOrPunishment().IsMatch(value)
            || MessageReference().IsMatch(value)
            || EnglishGuidance().Count(value) >= 2
            || !PoliteOrImpersonal().IsMatch(value)
            || !FutureBehavior().IsMatch(value)
            || HasMoreThanOneSentence(value)
            || ContainsMentionedName(value, sourceText)
            || ModerationReasonNormalizer.ContainsTriggerPhraseExcerpt(
                value,
                triggerPhrases))
        {
            return false;
        }

        return true;
    }

    private static string NormalizePart(string? value) =>
        Whitespace().Replace(value?.Trim() ?? string.Empty, " ");

    private static string EnsurePeriod(string value) =>
        value.TrimEnd('.', '!', '?', ';', ':') + ".";

    private static bool HasMoreThanOneSentence(string value)
    {
        var trimmed = value.TrimEnd('.', '!', '?');
        return trimmed.IndexOfAny(['.', '!', '?']) >= 0;
    }

    private static bool ContainsMentionedName(string guidance, string? sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText))
        {
            return false;
        }

        foreach (Match match in Mention().Matches(sourceText))
        {
            var name = match.Groups[1].Value;
            if (name.Length > 0
                && Regex.IsMatch(
                    guidance,
                    $@"\b{Regex.Escape(name)}\b",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                return true;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("['\"`„“‚‘«»]")]
    private static partial Regex QuotationMark();

    [GeneratedRegex(
        @"\b(du|dich|dir|dein(?:e|er|es|em|en)?|ihr|ihre|ihnen|sie|euch|euer(?:e|er|es|em|en)?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DirectAddress();

    [GeneratedRegex(
        @"\b(unterlasse|vermeide|beachte|achte|bleib|bleibe|verzichte|befolge|wahre|verwende|formuliere)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex DirectImperative();

    [GeneratedRegex(
        @"\b(nutzer|streamer|chatter|zuschauer|person|username|autor|verfasser|mitmenschen|betroffene\w*|ander(?:e|en|er|es|em)?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PersonOrTarget();

    [GeneratedRegex(
        @"\b(timeout|ban|bann?en|sperr\w*|lösch\w*|strafe|verwarn\w*|konsequenz\w*|androh\w*|wiederholung|sonst|sekunden|minuten|stunden|tage|[gb]_[a-z_]+|backseatgaming)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex RuleActionOrPunishment();

    [GeneratedRegex(
        @"\b(die|diese)\s+nachricht\b|\bder\s+(beitrag|kommentar)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MessageReference();

    [GeneratedRegex(
        @"\b(the|this|that|please|future|avoid|stop|follow|rules|message|user|streamer|warning|next|time)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex EnglishGuidance();

    [GeneratedRegex(
        @"\b(bitte|künftig|zukünftig|fortan|chatregeln?|respektvoll|rücksichtsvoll)\b|\bist\b.+\bzu\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex PoliteOrImpersonal();

    [GeneratedRegex(
        @"\b(künftig|zukünftig|fortan|unterlass\w*|vermeid\w*|beacht\w*|acht\w*|bleib\w*|verzicht\w*|zurückhalt\w*|respekt\w*|befolg\w*|wahr\w*|verwend\w*|formulier\w*)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex FutureBehavior();

    [GeneratedRegex(@"@([\p{L}\p{N}_]{1,25})")]
    private static partial Regex Mention();
}
