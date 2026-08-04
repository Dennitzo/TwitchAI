using System.Text;
using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public static class ModerationSpamSignalDetector
{
    public static bool HasDeterministicSignal(
        string? text,
        int twitchEmoteCount) =>
        IsEmoteFlood(twitchEmoteCount)
        || IsRepeatedPhraseFlood(text)
        || IsSymbolFlood(text);

    public static bool IsEmoteFlood(int twitchEmoteCount) =>
        twitchEmoteCount >= ModerationRuleCodes.MinimumEmoteSpamCount;

    public static bool IsRepeatedPhraseFlood(string? text) =>
        RepeatedPhraseSpamDetector.IsSpam(text);

    public static bool IsSymbolFlood(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var nonWhitespaceCount = 0;
        var symbolCount = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                continue;
            }

            nonWhitespaceCount++;
            if (Rune.IsPunctuation(rune) || Rune.IsSymbol(rune))
            {
                symbolCount++;
            }
        }

        return symbolCount >= ModerationRuleCodes.MinimumSymbolSpamCount
            && nonWhitespaceCount > 0
            && (double)symbolCount / nonWhitespaceCount
                >= ModerationRuleCodes.MinimumSymbolSpamRatio;
    }
}
