using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

/// <summary>
/// Adds a dedicated fallback rule only while a Twitch AutoMod-held message is
/// being evaluated. The persisted channel policy and its revision stay intact.
/// </summary>
public static class AutoModModerationPolicy
{
    public static ModerationRuleDefinition FallbackRule { get; } = new(
        ModerationRuleCodes.AutoModHeld,
        PolicySetCode.General,
        "Twitch AutoMod",
        ModerationRuleCodes.AutoModHeldPromptInstruction,
        ModerationActionKind.Timeout,
        ModerationActionRules.DefaultTimeoutSeconds,
        "Twitch-AutoMod-Treffer.",
        Priority: int.MaxValue,
        AllowTriggerLearning: false);

    public static EffectivePolicySnapshot EnsureFallbackRule(
        EffectivePolicySnapshot policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.FindRule(ModerationRuleCodes.AutoModHeld) is not null)
        {
            return policy;
        }

        return policy with
        {
            ActiveRules = [.. policy.ActiveRules, FallbackRule],
        };
    }
}
