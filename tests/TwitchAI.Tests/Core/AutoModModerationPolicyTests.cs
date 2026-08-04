using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.Tests.Core;

public sealed class AutoModModerationPolicyTests
{
    [Fact]
    public void EnsureFallbackRule_AddsExactlyOneRuleWithoutChangingRevision()
    {
        var policy = new EffectivePolicySnapshot(
            [new PolicySetRevision(PolicySetCode.General, 7)],
            "general:7");

        var enriched = AutoModModerationPolicy.EnsureFallbackRule(policy);
        var repeated = AutoModModerationPolicy.EnsureFallbackRule(enriched);

        Assert.Equal(policy.RevisionFingerprint, enriched.RevisionFingerprint);
        var fallback = Assert.Single(enriched.ActiveRules);
        Assert.Equal(ModerationRuleCodes.AutoModHeld, fallback.RuleCode);
        Assert.False(fallback.AllowTriggerLearning);
        Assert.False(ModerationRuleCodes.AllowsTriggerLearning(fallback.RuleCode));
        Assert.Same(enriched, repeated);
    }
}
