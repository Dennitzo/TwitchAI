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
            .Where(rule => rule.IsActive
                && policy.Contains(rule.PolicySet)
                && rule.RuleNormalizedPhrase.Length > 0)
            .Select(rule => TryMatch(
                text.RuleNormalizedText,
                rule.RuleNormalizedPhrase,
                out var score,
                out var matchKind)
                ? new BlacklistMatch(
                    rule.PhraseId,
                    rule.RuleCode,
                    rule.PolicySet,
                    rule.RuleNormalizedPhrase)
                {
                    Score = score,
                    MatchKind = matchKind,
                }
                : null)
            .Where(match => match is not null)
            .OrderByDescending(match => match!.Score)
            .ThenByDescending(match => match!.MatchedPhrase.Length)
            .Take(3)
            .Select(match => match!)
            .ToArray();
    }

    private static bool TryMatch(
        string text,
        string phrase,
        out double score,
        out string matchKind)
    {
        score = 0d;
        matchKind = "manual_phrase";
        if (ContainsWholePhrase(text, phrase))
        {
            score = 1d;
            return true;
        }

        var textTokens = Tokenize(text);
        var phraseTokens = Tokenize(phrase);
        if (phraseTokens.Count == 0 || textTokens.Count < phraseTokens.Count)
        {
            return false;
        }

        var best = 0d;
        for (var start = 0; start <= textTokens.Count - phraseTokens.Count; start++)
        {
            var total = 0d;
            var minimumTokenScore = 1d;
            for (var offset = 0; offset < phraseTokens.Count; offset++)
            {
                var tokenScore = TokenSimilarity(
                    phraseTokens[offset],
                    textTokens[start + offset]);
                total += tokenScore;
                minimumTokenScore = Math.Min(minimumTokenScore, tokenScore);
            }

            if (minimumTokenScore >= 0.72d)
            {
                best = Math.Max(best, total / phraseTokens.Count);
            }
        }

        var minimum = phraseTokens.Count == 1 ? 1d : 0.86d;
        if (best < minimum)
        {
            return false;
        }

        score = best * 0.92d;
        matchKind = "fuzzy_phrase";
        return true;
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

    private static List<string> Tokenize(string value)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                current.Append(char.ToLowerInvariant(character));
            }
            else if (current.Length > 0)
            {
                result.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static double TokenSimilarity(string left, string right)
    {
        if (StringComparer.Ordinal.Equals(left, right))
        {
            return 1d;
        }

        var distance = LevenshteinDistance(left, right);
        return 1d - (double)distance / Math.Max(left.Length, right.Length);
    }

    private static int LevenshteinDistance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[^1];
    }
}
