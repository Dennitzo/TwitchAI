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
    public const string JustChattingCategoryId = "509658";
    public const string IrlCategoryId = "509672";
}
