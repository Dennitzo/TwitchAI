namespace TwitchAI.Infrastructure;

public sealed class InfrastructureOptions
{
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TwitchAI",
        "twitchai.db");

    public TwitchOAuthOptions TwitchOAuth { get; set; } = new();

    public TwitchApiOptions TwitchApi { get; set; } = new();

    public LocalAiOptions LocalAi { get; set; } = new();
}

public sealed class TwitchOAuthOptions
{
    public string ClientId { get; set; } = string.Empty;

    public string VaultResource { get; set; } = "TwitchAI.TwitchOAuth";

    public string VaultAccount { get; set; } = "default";

    public Uri IdentityBaseAddress { get; set; } = new("https://id.twitch.tv/oauth2/");

    public string[] Scopes { get; set; } =
    [
        "user:read:chat",
        "user:write:chat",
        "user:read:moderated_channels",
        "moderator:manage:chat_messages",
        "moderator:manage:banned_users",
        "moderator:manage:automod",
    ];
}

public sealed class TwitchApiOptions
{
    public Uri HelixBaseAddress { get; set; } = new("https://api.twitch.tv/helix/");

    public Uri EventSubWebSocketUri { get; set; } = new("wss://eventsub.wss.twitch.tv/ws");

    public TimeSpan MaximumReconnectDelay { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ProcessedEventRetention { get; set; } = TimeSpan.FromDays(7);

    public TimeSpan CategoryReconciliationInterval { get; set; } = TimeSpan.FromMinutes(5);

    public string BroadcasterId { get; set; } = string.Empty;

    public string[] BroadcasterIds { get; set; } = [];

    public string ReaderUserId { get; set; } = string.Empty;

    public string ModeratorUserId { get; set; } = string.Empty;

    public IReadOnlyList<string> GetBroadcasterIds()
    {
        var configured = BroadcasterIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return configured.Length > 0
            ? configured
            : string.IsNullOrWhiteSpace(BroadcasterId)
                ? []
                : [BroadcasterId.Trim()];
    }

    public void SetBroadcasterIds(IEnumerable<string> broadcasterIds)
    {
        ArgumentNullException.ThrowIfNull(broadcasterIds);
        BroadcasterIds = broadcasterIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        BroadcasterId = BroadcasterIds.Length == 0
            ? string.Empty
            : BroadcasterIds[0];
    }

    public bool ContainsBroadcaster(string broadcasterId) =>
        GetBroadcasterIds().Contains(broadcasterId, StringComparer.Ordinal);
}

public sealed class LocalAiOptions
{
    public Uri BaseAddress { get; set; } = new("http://127.0.0.1:8180/");

    public string Model { get; set; } = string.Empty;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public int MaximumOutputTokens { get; set; } = 2048;

    public double Temperature { get; set; }

    /// <summary>
    /// Sends the compact keyed v7 moderation contract. The v6 property name is
    /// retained for settings-file compatibility. Disable only for a temporary
    /// rollback to the legacy full-envelope prompt.
    /// </summary>
    public bool UseCompactModerationContractV6 { get; set; } = true;

    public string ReasoningEffort { get; set; } = "auto";
}
