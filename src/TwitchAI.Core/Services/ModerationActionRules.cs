using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public static class ModerationActionRules
{
    public const int PurgeTimeoutSeconds = 2;

    public const int LightTimeoutSeconds = 120;

    public const int DefaultTimeoutSeconds = 600;

    public const int SevereTimeoutSeconds = 86_400;

    public const int MaximumTimeoutSeconds = 1_209_600;

    public static bool IsValid(
        ModerationActionKind action,
        int? timeoutSeconds)
    {
        return action switch
        {
            ModerationActionKind.None
                or ModerationActionKind.Delete
                or ModerationActionKind.Ban =>
                timeoutSeconds is null,
            ModerationActionKind.Timeout =>
                timeoutSeconds is >= 1 and <= MaximumTimeoutSeconds,
            _ => false,
        };
    }

    public static bool IsValidAiSuggestion(
        ModerationActionKind action,
        int? timeoutSeconds)
    {
        return IsValid(action, timeoutSeconds)
            && (action != ModerationActionKind.Timeout
                || timeoutSeconds is
                    PurgeTimeoutSeconds
                    or LightTimeoutSeconds
                    or DefaultTimeoutSeconds
                    or SevereTimeoutSeconds);
    }

    public static (
        ModerationActionKind Action,
        int? TimeoutSeconds) NormalizeAiSuggestionForRule(
            ModerationActionKind action,
            int? timeoutSeconds,
            string? ruleCode)
    {
        if (action == ModerationActionKind.None)
        {
            return (action, timeoutSeconds);
        }

        if (string.Equals(
                ruleCode,
                ModerationRuleCodes.Advertising,
                StringComparison.Ordinal))
        {
            return (ModerationActionKind.Ban, null);
        }

        if (string.Equals(
                ruleCode,
                ModerationRuleCodes.Begging,
                StringComparison.Ordinal))
        {
            return (ModerationActionKind.Timeout, SevereTimeoutSeconds);
        }

        if (action != ModerationActionKind.Delete
            || string.Equals(
                ruleCode,
                ModerationRuleCodes.OtherAbuse,
                StringComparison.Ordinal))
        {
            return (action, timeoutSeconds);
        }

        var normalizedTimeout = ruleCode switch
        {
            ModerationRuleCodes.Spam =>
                LightTimeoutSeconds,
            ModerationRuleCodes.Disruption =>
                PurgeTimeoutSeconds,
            _ => DefaultTimeoutSeconds,
        };
        return (ModerationActionKind.Timeout, normalizedTimeout);
    }

    public static bool IsValidAiRuleAction(
        ModerationActionKind action,
        string? ruleCode)
    {
        if (action != ModerationActionKind.Ban)
        {
            return true;
        }

        // These rule groups can justify deletion or a proportionate timeout,
        // but never a permanent ban on their own. A human reviewer may still
        // deliberately override the AI proposal in the review UI.
        return ruleCode is not (
            ModerationRuleCodes.OtherAbuse
            or ModerationRuleCodes.Begging
            or ModerationRuleCodes.Backseatgaming
            or ModerationRuleCodes.Spam
            or ModerationRuleCodes.Disruption);
    }
}
