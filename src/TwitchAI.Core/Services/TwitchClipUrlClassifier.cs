using System.Text.RegularExpressions;

namespace TwitchAI.Core.Services;

public static partial class TwitchClipUrlClassifier
{
    public static bool IsStandaloneAllowedTwitchClipUrl(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return StandaloneTwitchClipUrl().IsMatch(text);
    }

    [GeneratedRegex(
        "^\\s*https://(?:(?:www\\.)?twitch\\.tv/[a-z0-9_]+/clip(?:/[a-z0-9_-]+)?/?|clips\\.twitch\\.tv/[a-z0-9_-]+/?)(?:[?#][^\\s<>\\\"']*)?\\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex StandaloneTwitchClipUrl();
}
