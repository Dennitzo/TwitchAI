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
                && ContainsWholePhrase(
                    text.RuleNormalizedText,
                    rule.RuleNormalizedPhrase))
            .Select(rule => new BlacklistMatch(
                rule.PhraseId,
                rule.RuleCode,
                rule.PolicySet,
                rule.RuleNormalizedPhrase))
            .ToArray();
    }

    private static bool ContainsWholePhrase(string text, string phrase)
    {
        var searchStart = 0;
        while (searchStart <= text.Length - phrase.Length)
        {
            var index = text.IndexOf(phrase, searchStart, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            var leftBoundary = index == 0
                || !IsTokenCharacter(phrase[0])
                || !IsTokenCharacter(text[index - 1]);
            var end = index + phrase.Length;
            var rightBoundary = end == text.Length
                || !IsTokenCharacter(phrase[^1])
                || !IsTokenCharacter(text[end]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            searchStart = index + 1;
        }

        return false;
    }

    private static bool IsTokenCharacter(char value) =>
        char.IsLetterOrDigit(value) || value == '_';
}
