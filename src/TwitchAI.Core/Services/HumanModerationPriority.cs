using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public sealed record HumanPriorityDecision(
    bool IsAllow,
    ModerationRuleDefinition? Rule,
    ModerationActionKind Action,
    int? TimeoutSeconds,
    IReadOnlyList<string> TriggerPhrases,
    string? EvidenceKind,
    string Source);

public static class HumanModerationPriority
{
    private const string UnwantedIntimateApproach = "unwanted_intimate_approach";
    private const string UnwantedIntimateApproachPhrase = "ich liebe dich";

    public static HumanPriorityDecision? Resolve(
        string lowerText,
        EffectivePolicySnapshot policy,
        IReadOnlyList<BlacklistMatch> localMatches,
        IReadOnlyList<RetrievalExample> examples)
    {
        ArgumentNullException.ThrowIfNull(lowerText);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(localMatches);
        ArgumentNullException.ThrowIfNull(examples);

        var exact = examples.FirstOrDefault(example =>
            example.IsExactHumanDecision
            && StringComparer.Ordinal.Equals(example.LowerText, lowerText));
        if (exact is not null)
        {
            if (!exact.IsPositive || exact.Action == ModerationActionKind.None)
            {
                return new HumanPriorityDecision(
                    true, null, ModerationActionKind.None, null,
                    [], null, "exact_allow_learning");
            }

            var exactRule = policy.FindRule(exact.RuleCode);
            var gatedWithoutPhrase = exactRule is not null
                && ModerationRuleCodes.RequiresLocalPhraseMatch(exactRule.RuleCode)
                && !localMatches.Any(match => StringComparer.Ordinal.Equals(
                    match.RuleCode,
                    exactRule.RuleCode));
            if (exactRule is not null
                && policy.Contains(exactRule.PolicySet)
                && !gatedWithoutPhrase
                && ModerationActionRules.IsValid(
                    exact.Action,
                    exact.TimeoutSeconds))
            {
                return new HumanPriorityDecision(
                    false,
                    exactRule,
                    exact.Action,
                    exact.TimeoutSeconds,
                    MatchingPhrases(localMatches, exactRule.RuleCode),
                    ResolveExplicitEvidenceKind(
                        lowerText,
                        exactRule.RuleCode,
                        localMatches),
                    "exact_moderate_learning");
            }
        }

        var matchedRule = localMatches
            .Select(match => policy.FindRule(match.RuleCode))
            .Where(rule => rule is not null && policy.Contains(rule.PolicySet))
            .Cast<ModerationRuleDefinition>()
            .OrderBy(rule => rule.Priority)
            .ThenBy(rule => rule.RuleCode, StringComparer.Ordinal)
            .FirstOrDefault();
        if (matchedRule is null
            || matchedRule.DefaultAction == ModerationActionKind.None
            || !ModerationActionRules.IsValid(
                matchedRule.DefaultAction,
                matchedRule.DefaultTimeoutSeconds))
        {
            return null;
        }

        return new HumanPriorityDecision(
            false,
            matchedRule,
            matchedRule.DefaultAction,
            matchedRule.DefaultTimeoutSeconds,
            MatchingPhrases(localMatches, matchedRule.RuleCode),
            ResolveExplicitEvidenceKind(
                lowerText,
                matchedRule.RuleCode,
                localMatches),
            "manual_blacklist_phrase");
    }

    private static string[] MatchingPhrases(
        IReadOnlyList<BlacklistMatch> matches,
        string ruleCode) =>
        matches
            .Where(match => StringComparer.Ordinal.Equals(match.RuleCode, ruleCode))
            .Select(match => match.MatchedPhrase)
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            .Distinct(StringComparer.Ordinal)
            .Take(1)
            .ToArray();

    private static string? ResolveExplicitEvidenceKind(
        string lowerText,
        string ruleCode,
        IReadOnlyList<BlacklistMatch> localMatches) =>
        string.Equals(
            ruleCode,
            ModerationRuleCodes.PersonalAttack,
            StringComparison.Ordinal)
        && string.Equals(
            lowerText,
            UnwantedIntimateApproachPhrase,
            StringComparison.Ordinal)
        && localMatches.Any(match =>
            string.Equals(
                match.RuleCode,
                ruleCode,
                StringComparison.Ordinal)
            && string.Equals(
                match.MatchedPhrase,
                lowerText,
                StringComparison.Ordinal))
            ? UnwantedIntimateApproach
            : null;
}
