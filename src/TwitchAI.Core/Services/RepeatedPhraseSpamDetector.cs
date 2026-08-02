namespace TwitchAI.Core.Services;

public static class RepeatedPhraseSpamDetector
{
    public static bool IsSpam(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var tokens = text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var minimum = Models.ModerationRuleCodes.MinimumRepeatedPhraseSpamCount;
        if (tokens.Length < minimum * 2)
        {
            return false;
        }

        var maximumPhraseLength = tokens.Length / minimum;
        for (var phraseLength = 2; phraseLength <= maximumPhraseLength; phraseLength++)
        {
            for (var start = 0; start + (phraseLength * minimum) <= tokens.Length; start++)
            {
                var repeated = true;
                for (var occurrence = 1; occurrence < minimum && repeated; occurrence++)
                {
                    for (var offset = 0; offset < phraseLength; offset++)
                    {
                        if (!string.Equals(
                                tokens[start + offset],
                                tokens[start + (occurrence * phraseLength) + offset],
                                StringComparison.OrdinalIgnoreCase))
                        {
                            repeated = false;
                            break;
                        }
                    }
                }

                if (repeated)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
