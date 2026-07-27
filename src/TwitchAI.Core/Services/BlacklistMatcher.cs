using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed class BlacklistMatcher : IBlacklistMatcher
{
    public IReadOnlyList<BlacklistMatch> Match(
        ProcessedChatText text,
        EffectivePolicySnapshot policy,
        IReadOnlyList<PhraseRule> rules)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(rules);

        return rules
            .Where(rule =>
                rule.IsActive
                && policy.Contains(rule.PolicySet)
                && rule.RuleNormalizedPhrase.Length > 0
                && text.RuleNormalizedText.Contains(
                    rule.RuleNormalizedPhrase,
                    StringComparison.Ordinal))
            .Select(rule => new BlacklistMatch(
                rule.PhraseId,
                rule.RuleCode,
                rule.PolicySet,
                rule.RuleNormalizedPhrase))
            .ToArray();
    }
}
