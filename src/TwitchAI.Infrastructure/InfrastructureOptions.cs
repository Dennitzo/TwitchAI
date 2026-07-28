namespace TwitchAI.Infrastructure;

public sealed class InfrastructureOptions
{
    public string DatabasePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TwitchAI",
        "twitchai.db");

    public TwitchOAuthOptions TwitchOAuth { get; set; } = new();

    public TwitchApiOptions TwitchApi { get; set; } = new();

    public LmStudioOptions LmStudio { get; set; } = new();
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

    public string ReaderUserId { get; set; } = string.Empty;

    public string ModeratorUserId { get; set; } = string.Empty;
}

public sealed class LmStudioOptions
{
    public Uri BaseAddress { get; set; } = new("http://127.0.0.1:1234/");

    public string Model { get; set; } = "openai/gpt-oss-20b";

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(2);

    public int MaximumOutputTokens { get; set; } = 2048;

    public double Temperature { get; set; }

    public string VaultResource { get; set; } = "TwitchAI.LmStudio";

    public string VaultAccount { get; set; } = "local-api-token";
}
