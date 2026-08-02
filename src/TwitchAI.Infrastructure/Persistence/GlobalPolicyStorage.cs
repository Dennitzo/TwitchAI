using TwitchAI.Core.Models;

namespace TwitchAI.Infrastructure.Persistence;

/// <summary>
/// Stable storage identifiers for moderation configuration shared by every
/// connected Twitch channel. Channel ids remain attached to messages, cases,
/// feedback, and action attempts for provenance only.
/// </summary>
public static class GlobalPolicyStorage
{
    public const string BroadcasterId = "*";
    public const string GeneralRuleCode = "G_PERSONAL_ATTACK";
    public const string BackseatgamingRuleCode = "BACKSEATGAMING";
    public const string RetiredStreamerInstructionRuleCode =
        "G_STREAMER_INSTRUCTION";
    public const string JustChattingCategoryId = "509658";
    public const string IrlCategoryId = "509672";

    private static readonly HashSet<string> CurrentRuleCodes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ModerationRuleCodes.PersonalData,
            ModerationRuleCodes.Harm,
            ModerationRuleCodes.Hate,
            ModerationRuleCodes.SexualContent,
            ModerationRuleCodes.PersonalAttack,
            ModerationRuleCodes.Begging,
            ModerationRuleCodes.Spam,
            ModerationRuleCodes.Backseatgaming,
            ModerationRuleCodes.SelfPromotion,
            ModerationRuleCodes.BotScam,
        };

    public static bool IsCurrentRuleCode(string? ruleCode) =>
        !string.IsNullOrWhiteSpace(ruleCode)
        && CurrentRuleCodes.Contains(ruleCode);

    public static bool IsRetiredRuleCode(string? ruleCode) =>
        !IsCurrentRuleCode(ruleCode);
}
