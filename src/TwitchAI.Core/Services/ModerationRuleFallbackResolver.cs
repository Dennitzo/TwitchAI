using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public static class ModerationRuleFallbackResolver
{
    public static ModerationRuleDefinition? Resolve(
        EffectivePolicySnapshot activePolicy,
        PolicySetCode? policySet,
        string? requestedRuleCode = null,
        IEnumerable<string>? localMatchCodes = null)
    {
        ArgumentNullException.ThrowIfNull(activePolicy);
        if (policySet is not { } selectedPolicySet
            || !Enum.IsDefined(selectedPolicySet))
        {
            return null;
        }

        var candidates = activePolicy.ActiveRules
            .Where(rule => rule.PolicySet == selectedPolicySet)
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.RuleCode, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(requestedRuleCode))
        {
            var requested = candidates.FirstOrDefault(
                rule => string.Equals(
                    rule.RuleCode,
                    requestedRuleCode,
                    StringComparison.Ordinal));
            if (requested is not null)
            {
                return requested;
            }
        }

        if (localMatchCodes is not null)
        {
            var matches = localMatchCodes
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .ToHashSet(StringComparer.Ordinal);
            var matchedRule = candidates.FirstOrDefault(
                rule => matches.Contains(rule.RuleCode));
            if (matchedRule is not null)
            {
                return matchedRule;
            }
        }

        var baseRuleCode = selectedPolicySet == PolicySetCode.Backseatgaming
            ? ModerationRuleCodes.Backseatgaming
            : ModerationRuleCodes.OtherAbuse;
        return candidates.FirstOrDefault(
                rule => string.Equals(
                    rule.RuleCode,
                    baseRuleCode,
                    StringComparison.Ordinal))
            ?? candidates[0];
    }
}
