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
        // AI output is deliberately not escalated or silently rewritten. Invalid
        // rule/action combinations are repaired by the validator or become none.
        return (action, timeoutSeconds);
    }

    public static bool IsValidAiRuleAction(
        ModerationActionKind action,
        string? ruleCode)
    {
        return action != ModerationActionKind.Ban
            || ruleCode is ModerationRuleCodes.PersonalData
                or ModerationRuleCodes.Harm
                or ModerationRuleCodes.Hate
                or ModerationRuleCodes.PersonalAttack
                or ModerationRuleCodes.SelfPromotion
                or ModerationRuleCodes.BotScam;
    }

    public static bool IsValidAiRuleSuggestion(
        ModerationActionKind action,
        int? timeoutSeconds,
        string? ruleCode)
    {
        if (!IsValidAiSuggestion(action, timeoutSeconds)
            || !IsValidAiRuleAction(action, ruleCode))
        {
            return false;
        }

        return ruleCode switch
        {
            ModerationRuleCodes.PersonalData or ModerationRuleCodes.Harm =>
                action == ModerationActionKind.Ban
                || action == ModerationActionKind.Timeout
                    && timeoutSeconds == SevereTimeoutSeconds,
            ModerationRuleCodes.Hate or ModerationRuleCodes.PersonalAttack =>
                action == ModerationActionKind.Ban
                || action == ModerationActionKind.Timeout
                    && timeoutSeconds is DefaultTimeoutSeconds or SevereTimeoutSeconds,
            ModerationRuleCodes.SexualContent =>
                action == ModerationActionKind.Timeout
                && timeoutSeconds is DefaultTimeoutSeconds or SevereTimeoutSeconds,
            ModerationRuleCodes.Begging =>
                action == ModerationActionKind.Timeout
                && timeoutSeconds == SevereTimeoutSeconds,
            ModerationRuleCodes.Spam =>
                action == ModerationActionKind.Delete
                || action == ModerationActionKind.Timeout
                    && timeoutSeconds is PurgeTimeoutSeconds or LightTimeoutSeconds,
            ModerationRuleCodes.Backseatgaming =>
                action == ModerationActionKind.Timeout
                && timeoutSeconds == DefaultTimeoutSeconds,
            ModerationRuleCodes.SelfPromotion =>
                action == ModerationActionKind.Ban
                || action == ModerationActionKind.Timeout
                    && timeoutSeconds == SevereTimeoutSeconds,
            ModerationRuleCodes.BotScam => action == ModerationActionKind.Ban,
            ModerationRuleCodes.AutoModHeld =>
                action == ModerationActionKind.Timeout
                && timeoutSeconds == DefaultTimeoutSeconds,
            _ => false,
        };
    }
}
