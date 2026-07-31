using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TwitchAI.Infrastructure.Authentication;

namespace TwitchAI.Infrastructure.Twitch;

public sealed record TwitchChannelInfo(
    string BroadcasterId,
    string BroadcasterLogin,
    string BroadcasterName,
    string? CategoryId,
    string? CategoryName,
    string Title,
    string Language);

public sealed record TwitchGameInfo(
    string Id,
    string Name,
    string BoxArtUrl);

public sealed record TwitchBannedUserInfo(
    string UserId,
    string UserLogin,
    string UserName,
    DateTimeOffset? ExpiresAt);

public sealed record TwitchModeratedChannel(
    string BroadcasterId,
    string BroadcasterLogin,
    string BroadcasterName);

public sealed record TwitchUserInfo(
    string Id,
    string Login,
    string DisplayName);

public sealed record TwitchSendChatMessageResult(
    string MessageId,
    bool IsSent,
    string? DropCode,
    string? DropMessage);

public sealed record TwitchEventSubSubscriptionRequest(
    string Type,
    string Version,
    IReadOnlyDictionary<string, string> Condition,
    string SessionId);

public sealed class TwitchHelixException : HttpRequestException
{
    public TwitchHelixException(
        string message,
        HttpStatusCode statusCode,
        string? responseBody)
        : base(message, null, statusCode)
    {
        ResponseBody = responseBody;
    }

    public string? ResponseBody { get; }
}

public interface ITwitchHelixClient
{
    Task<TwitchUserInfo> GetCurrentUserAsync(CancellationToken cancellationToken = default);

    Task<TwitchUserInfo?> GetUserByLoginAsync(
        string login,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<TwitchUserInfo?>(null);

    Task<TwitchChannelInfo?> GetChannelAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TwitchGameInfo>> GetGamesAsync(
        IReadOnlyCollection<string> categoryIds,
        CancellationToken cancellationToken = default);

    Task<TwitchGameInfo?> GetGameByNameAsync(
        string categoryName,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TwitchModeratedChannel>> GetModeratedChannelsAsync(
        string userId,
        CancellationToken cancellationToken = default);

    Task<TwitchBannedUserInfo?> GetBannedUserAsync(
        string broadcasterId,
        string userId,
        CancellationToken cancellationToken = default);

    Task DeleteMessageAsync(
        string broadcasterId,
        string moderatorId,
        string messageId,
        CancellationToken cancellationToken = default);

    Task TimeoutUserAsync(
        string broadcasterId,
        string moderatorId,
        string userId,
        int durationSeconds,
        string reason,
        CancellationToken cancellationToken = default);

    Task BanUserAsync(
        string broadcasterId,
        string moderatorId,
        string userId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<TwitchSendChatMessageResult> SendChatMessageAsync(
        string broadcasterId,
        string senderId,
        string message,
        string? replyParentMessageId = null,
        CancellationToken cancellationToken = default);

    Task CreateEventSubSubscriptionAsync(
        TwitchEventSubSubscriptionRequest subscription,
        CancellationToken cancellationToken = default);
}

public sealed class TwitchHelixClient : ITwitchHelixClient
{
    private readonly HttpClient _httpClient;
    private readonly ITwitchAccessTokenProvider _tokenProvider;
    private readonly TwitchOAuthOptions _oauthOptions;

    public TwitchHelixClient(
        HttpClient httpClient,
        ITwitchAccessTokenProvider tokenProvider,
        InfrastructureOptions options)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _oauthOptions = options.TwitchOAuth;
    }

    public async Task<TwitchUserInfo> GetCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, "users"),
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var item = FirstDataItem(document.RootElement)
            ?? throw new InvalidOperationException("The Twitch token has no associated user.");
        return new TwitchUserInfo(
            RequiredString(item, "id"),
            RequiredString(item, "login"),
            RequiredString(item, "display_name"));
    }

    public async Task<TwitchUserInfo?> GetUserByLoginAsync(
        string login,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);
        using var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"users?login={Escape(login)}"),
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var item = FirstDataItem(document.RootElement);
        return item is null
            ? null
            : new TwitchUserInfo(
                RequiredString(item.Value, "id"),
                RequiredString(item.Value, "login"),
                RequiredString(item.Value, "display_name"));
    }

    public async Task<TwitchChannelInfo?> GetChannelAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);
        using var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"channels?broadcaster_id={Escape(broadcasterId)}"),
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var item = FirstDataItem(document.RootElement);
        if (item is null)
        {
            return null;
        }

        return new TwitchChannelInfo(
            RequiredString(item.Value, "broadcaster_id"),
            RequiredString(item.Value, "broadcaster_login"),
            RequiredString(item.Value, "broadcaster_name"),
            NullIfEmpty(OptionalString(item.Value, "game_id")),
            NullIfEmpty(OptionalString(item.Value, "game_name")),
            OptionalString(item.Value, "title") ?? string.Empty,
            OptionalString(item.Value, "broadcaster_language") ?? string.Empty);
    }

    public async Task<IReadOnlyList<TwitchGameInfo>> GetGamesAsync(
        IReadOnlyCollection<string> categoryIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(categoryIds);
        var ids = categoryIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        if (ids.Length > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(categoryIds),
                categoryIds.Count,
                "Twitch Get Games accepts at most 100 IDs.");
        }

        var query = string.Join("&", ids.Select(id => $"id={Escape(id)}"));
        using var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"games?{query}"),
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);

        return EnumerateData(document.RootElement)
            .Select(item => new TwitchGameInfo(
                RequiredString(item, "id"),
                RequiredString(item, "name"),
                OptionalString(item, "box_art_url") ?? string.Empty))
            .ToArray();
    }

    public async Task<TwitchGameInfo?> GetGameByNameAsync(
        string categoryName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        using var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"games?name={Escape(categoryName)}"),
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var item = FirstDataItem(document.RootElement);
        return item is null
            ? null
            : new TwitchGameInfo(
                RequiredString(item.Value, "id"),
                RequiredString(item.Value, "name"),
                OptionalString(item.Value, "box_art_url") ?? string.Empty);
    }

    public async Task<IReadOnlyList<TwitchModeratedChannel>> GetModeratedChannelsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        using var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"moderation/channels?user_id={Escape(userId)}&first=100"),
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);

        return EnumerateData(document.RootElement)
            .Select(item => new TwitchModeratedChannel(
                RequiredString(item, "broadcaster_id"),
                RequiredString(item, "broadcaster_login"),
                RequiredString(item, "broadcaster_name")))
            .ToArray();
    }

    public async Task<TwitchBannedUserInfo?> GetBannedUserAsync(
        string broadcasterId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var path =
            $"moderation/banned?broadcaster_id={Escape(broadcasterId)}&user_id={Escape(userId)}";
        using var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Get, path),
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var item = FirstDataItem(document.RootElement);
        if (item is null)
        {
            return null;
        }

        var expiresAtText = OptionalString(item.Value, "expires_at");
        return new TwitchBannedUserInfo(
            RequiredString(item.Value, "user_id"),
            RequiredString(item.Value, "user_login"),
            RequiredString(item.Value, "user_name"),
            string.IsNullOrWhiteSpace(expiresAtText)
                ? null
                : DateTimeOffset.Parse(
                    expiresAtText,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind));
    }

    public async Task DeleteMessageAsync(
        string broadcasterId,
        string moderatorId,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(moderatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        var path =
            $"moderation/chat?broadcaster_id={Escape(broadcasterId)}" +
            $"&moderator_id={Escape(moderatorId)}&message_id={Escape(messageId)}";

        // Deliberately no automatic retry for a mutating moderation request.
        using var response = await SendAuthenticatedAsync(
            () => new HttpRequestMessage(HttpMethod.Delete, path),
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task TimeoutUserAsync(
        string broadcasterId,
        string moderatorId,
        string userId,
        int durationSeconds,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(moderatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(reason);
        if (durationSeconds is < 1 or > 1_209_600)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationSeconds),
                durationSeconds,
                "Twitch timeout duration must be between 1 and 1,209,600 seconds.");
        }

        if (reason.Length > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason.Length,
                "Twitch moderation reasons may contain at most 500 characters.");
        }

        var path =
            $"moderation/bans?broadcaster_id={Escape(broadcasterId)}" +
            $"&moderator_id={Escape(moderatorId)}";

        // Deliberately no automatic retry for a mutating moderation request.
        using var response = await SendAuthenticatedAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, path);
                request.Content = JsonContent.Create(new
                {
                    data = new
                    {
                        user_id = userId,
                        duration = durationSeconds,
                        reason,
                    },
                });
                return request;
            },
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task BanUserAsync(
        string broadcasterId,
        string moderatorId,
        string userId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(moderatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentNullException.ThrowIfNull(reason);
        if (reason.Length > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason.Length,
                "Twitch moderation reasons may contain at most 500 characters.");
        }

        var path =
            $"moderation/bans?broadcaster_id={Escape(broadcasterId)}" +
            $"&moderator_id={Escape(moderatorId)}";

        // Omitting duration makes this a permanent ban instead of a timeout.
        // Deliberately no automatic retry for a mutating moderation request.
        using var response = await SendAuthenticatedAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, path);
                request.Content = JsonContent.Create(new
                {
                    data = new
                    {
                        user_id = userId,
                        reason,
                    },
                });
                return request;
            },
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TwitchSendChatMessageResult> SendChatMessageAsync(
        string broadcasterId,
        string senderId,
        string message,
        string? replyParentMessageId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(message) || message.Length > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                message.Length,
                "Twitch chat messages must contain between 1 and 500 characters.");
        }

        // A chat send is mutating. It is never retried after an ambiguous
        // transport failure; SendAuthenticatedAsync only repeats a definite
        // 401 response after refreshing the token.
        using var response = await SendAuthenticatedAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "chat/messages");
                var body = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["broadcaster_id"] = broadcasterId,
                    ["sender_id"] = senderId,
                    ["message"] = message,
                };
                if (!string.IsNullOrWhiteSpace(replyParentMessageId))
                {
                    body["reply_parent_message_id"] = replyParentMessageId;
                }

                request.Content = JsonContent.Create(body);
                return request;
            },
            cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessJsonAsync(response, cancellationToken)
            .ConfigureAwait(false);
        var item = FirstDataItem(document.RootElement)
            ?? throw new InvalidDataException(
                "Twitch Send Chat Message returned no result.");
        var isSent = item.TryGetProperty("is_sent", out var isSentProperty)
            && isSentProperty.ValueKind is JsonValueKind.True or JsonValueKind.False
            && isSentProperty.GetBoolean();
        string? dropCode = null;
        string? dropMessage = null;
        if (item.TryGetProperty("drop_reason", out var dropReason)
            && dropReason.ValueKind == JsonValueKind.Object)
        {
            dropCode = OptionalString(dropReason, "code");
            dropMessage = OptionalString(dropReason, "message");
        }

        return new TwitchSendChatMessageResult(
            OptionalString(item, "message_id") ?? string.Empty,
            isSent,
            dropCode,
            dropMessage);
    }

    public async Task CreateEventSubSubscriptionAsync(
        TwitchEventSubSubscriptionRequest subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        using var response = await SendAuthenticatedAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "eventsub/subscriptions");
                request.Content = JsonContent.Create(new
                {
                    type = subscription.Type,
                    version = subscription.Version,
                    condition = subscription.Condition,
                    transport = new
                    {
                        method = "websocket",
                        session_id = subscription.SessionId,
                    },
                });
                return request;
            },
            cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken)
            .ConfigureAwait(false);
        var response = await SendOnceAsync(requestFactory(), token, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        response.Dispose();
        token = await _tokenProvider.RefreshAccessTokenAsync(cancellationToken)
            .ConfigureAwait(false);
        return await SendOnceAsync(requestFactory(), token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage request,
        string accessToken,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Client-Id", _oauthOptions.ClientId);
        try
        {
            return await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task<JsonDocument> ReadSuccessJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            return await response.Content
                .ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("Twitch Helix returned an empty JSON document.");
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException(
                "Twitch Helix returned malformed JSON.",
                exception,
                response.StatusCode);
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = TryGetApiMessage(body)
            ?? response.ReasonPhrase
            ?? "Twitch Helix request failed.";
        throw new TwitchHelixException(message, response.StatusCode, body);
    }

    private static string? TryGetApiMessage(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return OptionalString(document.RootElement, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? FirstDataItem(JsonElement root)
    {
        return EnumerateData(root).Cast<JsonElement?>().FirstOrDefault();
    }

    private static JsonElement[] EnumerateData(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Twitch Helix response is missing its data array.");
        }

        return data.EnumerateArray().ToArray();
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        return OptionalString(parent, name)
            ?? throw new InvalidDataException($"Twitch Helix response is missing '{name}'.");
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string Escape(string value)
    {
        return Uri.EscapeDataString(value);
    }
}
