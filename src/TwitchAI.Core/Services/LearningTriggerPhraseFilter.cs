namespace TwitchAI.Core.Services;

/// <summary>
/// Keeps optional learning evidence contextual. The complete chat message is the
/// learning example; trigger phrases are only supporting metadata and must never
/// collapse that example to an isolated word.
/// </summary>
public static class LearningTriggerPhraseFilter
{
    public const int MinimumLexicalTokens = 2;

    public static bool IsContextual(string? phrase) =>
        CountLexicalTokens(phrase) >= MinimumLexicalTokens;

    public static int CountLexicalTokens(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        var count = 0;
        var insideToken = false;
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (!insideToken)
                {
                    count++;
                    insideToken = true;
                }
            }
            else if (character == '_' && insideToken)
            {
                // Twitch login names may contain underscores. Treat the entire
                // login as one lexical token instead of manufacturing context.
            }
            else
            {
                insideToken = false;
            }
        }

        return count;
    }
}
