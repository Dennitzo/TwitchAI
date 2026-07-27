namespace TwitchAI.Core.Models;

public enum WhitelistEntryKind
{
    ExactMessage,
    UserLogin,
}

public sealed record ActiveWhitelistSnapshot(
    IReadOnlySet<string> ExactLowerMessages,
    IReadOnlySet<string> LowerUserLogins);
