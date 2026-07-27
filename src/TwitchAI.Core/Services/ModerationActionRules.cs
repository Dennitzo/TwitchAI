using TwitchAI.Core.Models;

namespace TwitchAI.Core.Services;

public static class ModerationActionRules
{
    public const int DefaultTimeoutSeconds = 600;

    public const int MaximumTimeoutSeconds = 1_209_600;

    public static bool IsValid(
        ModerationActionKind action,
        int? timeoutSeconds)
    {
        return action switch
        {
            ModerationActionKind.None or ModerationActionKind.Delete =>
                timeoutSeconds is null,
            ModerationActionKind.Timeout =>
                timeoutSeconds is >= 1 and <= MaximumTimeoutSeconds,
            _ => false,
        };
    }
}
