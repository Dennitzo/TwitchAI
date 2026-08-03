using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

/// <summary>
/// Turns an allowed human example into contextual evidence only when the current
/// message shares a meaningful contiguous phrase with it. The result is a hint
/// for the model and never a local allow decision.
/// </summary>
public static class AllowLearningPhraseMatcher
{
    public static bool TryMatch(
        string messageLower,
        RetrievalExample example,
        out EffectiveAllowMatch match)
    {
        ArgumentNullException.ThrowIfNull(messageLower);
        ArgumentNullException.ThrowIfNull(example);
        match = null!;
        if (example.IsPositive || string.IsNullOrWhiteSpace(example.LowerText))
        {
            return false;
        }

        var message = Tokenize(messageLower);
        var allowed = Tokenize(example.LowerText);
        if (message.Count == 0 || allowed.Count == 0)
        {
            return false;
        }

        if (string.Equals(
                messageLower.Trim().ToLowerInvariant(),
                example.LowerText.Trim().ToLowerInvariant(),
                StringComparison.Ordinal))
        {
            match = new EffectiveAllowMatch(
                example.LowerText,
                1d,
                IsExact: true,
                example.Source,
                messageLower.Trim(),
                example.RuleCode,
                example.PolicySet,
                example.EffectiveAtUtc);
            return true;
        }

        var bestMessageIndex = -1;
        var bestLength = 0;
        for (var messageIndex = 0; messageIndex < message.Count; messageIndex++)
        {
            for (var allowedIndex = 0; allowedIndex < allowed.Count; allowedIndex++)
            {
                var length = 0;
                while (messageIndex + length < message.Count
                    && allowedIndex + length < allowed.Count
                    && string.Equals(
                        message[messageIndex + length].Value,
                        allowed[allowedIndex + length].Value,
                        StringComparison.Ordinal))
                {
                    length++;
                }

                if (length > bestLength)
                {
                    bestLength = length;
                    bestMessageIndex = messageIndex;
                }
            }
        }

        if (bestLength == 0)
        {
            return false;
        }

        var singleToken = message[bestMessageIndex].Value;
        if (bestLength == 1
            && (singleToken.Length < 5 || example.SimilarityScore < 0.55d))
        {
            return false;
        }

        var first = message[bestMessageIndex];
        var last = message[bestMessageIndex + bestLength - 1];
        var matchedPhrase = messageLower[first.Start..last.End];
        var coverage = (double)bestLength / Math.Min(message.Count, allowed.Count);
        var score = Math.Clamp(
            (example.SimilarityScore * 0.65d) + (coverage * 0.35d),
            0d,
            1d);
        match = new EffectiveAllowMatch(
            example.LowerText,
            score,
            IsExact: false,
            example.Source,
            matchedPhrase,
            example.RuleCode,
            example.PolicySet,
            example.EffectiveAtUtc);
        return true;
    }

    private static List<Token> Tokenize(string text)
    {
        var result = new List<Token>();
        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            var isToken = index < text.Length && char.IsLetterOrDigit(text[index]);
            if (isToken && start < 0)
            {
                start = index;
            }
            else if (!isToken && start >= 0)
            {
                result.Add(new Token(
                    text[start..index].ToLowerInvariant(),
                    start,
                    index));
                start = -1;
            }
        }

        return result;
    }

    private sealed record Token(string Value, int Start, int End);
}
