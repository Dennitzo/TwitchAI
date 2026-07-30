using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.Authentication;
using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.Tests.Infrastructure;

public sealed class TwitchHelixChatMessageTests
{
    [Fact]
    public async Task SendChatMessageAsync_PostsExactIdentifiersAndMessageAndParsesSuccess()
    {
        var handler = new CapturingHandler
        {
            ResponseBody =
                """
                {
                  "data": [
                    {
                      "message_id": "outgoing-42",
                      "is_sent": true,
                      "drop_reason": null
                    }
                  ]
                }
                """,
        };
        var tokenProvider = new StubTokenProvider();
        using var httpClient = CreateHttpClient(handler);
        var client = CreateClient(httpClient, tokenProvider);

        var result = await client.SendChatMessageAsync(
            "broadcaster-1",
            "sender-9",
            "!event @viewer");

        Assert.Equal("outgoing-42", result.MessageId);
        Assert.True(result.IsSent);
        Assert.Null(result.DropCode);
        Assert.Null(result.DropMessage);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(
            "https://api.twitch.tv/helix/chat/messages",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("access-token", handler.Authorization?.Parameter);
        Assert.Equal("client-id", handler.ClientId);
        Assert.Equal(1, tokenProvider.GetCalls);
        Assert.Equal(0, tokenProvider.RefreshCalls);

        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var root = body.RootElement;
        Assert.Equal(
            ["broadcaster_id", "message", "sender_id"],
            root.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            "broadcaster-1",
            root.GetProperty("broadcaster_id").GetString());
        Assert.Equal("sender-9", root.GetProperty("sender_id").GetString());
        Assert.Equal("!event @viewer", root.GetProperty("message").GetString());
        Assert.False(root.TryGetProperty("reply_parent_message_id", out _));
    }

    [Fact]
    public async Task SendChatMessageAsync_WhenTwitchDoesNotSend_ParsesDropReason()
    {
        var handler = new CapturingHandler
        {
            ResponseBody =
                """
                {
                  "data": [
                    {
                      "message_id": "",
                      "is_sent": false,
                      "drop_reason": {
                        "code": "automod_held",
                        "message": "Message was held by AutoMod."
                      }
                    }
                  ]
                }
                """,
        };
        using var httpClient = CreateHttpClient(handler);
        var client = CreateClient(httpClient, new StubTokenProvider());

        var result = await client.SendChatMessageAsync(
            "broadcaster-1",
            "sender-9",
            "!event @viewer");

        Assert.False(result.IsSent);
        Assert.Equal(string.Empty, result.MessageId);
        Assert.Equal("automod_held", result.DropCode);
        Assert.Equal("Message was held by AutoMod.", result.DropMessage);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendChatMessageAsync_OnAmbiguousTransportFailure_DoesNotRetry()
    {
        var handler = new CapturingHandler
        {
            Exception = new HttpRequestException(
                "The connection was reset after the request upload."),
        };
        var tokenProvider = new StubTokenProvider();
        using var httpClient = CreateHttpClient(handler);
        var client = CreateClient(httpClient, tokenProvider);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.SendChatMessageAsync(
                "broadcaster-1",
                "sender-9",
                "!event @viewer"));

        Assert.Contains("reset", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, tokenProvider.GetCalls);
        Assert.Equal(0, tokenProvider.RefreshCalls);
    }

    [Fact]
    public async Task BanUserAsync_OmitsDurationAndPostsPermanentBan()
    {
        var handler = new CapturingHandler();
        var tokenProvider = new StubTokenProvider();
        using var httpClient = CreateHttpClient(handler);
        var client = CreateClient(httpClient, tokenProvider);

        await client.BanUserAsync(
            "broadcaster-1",
            "moderator-9",
            "viewer-42",
            "Schwere gezielte Beleidigung.");

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(
            "https://api.twitch.tv/helix/moderation/bans" +
            "?broadcaster_id=broadcaster-1&moderator_id=moderator-9",
            handler.RequestUri?.AbsoluteUri);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("access-token", handler.Authorization?.Parameter);
        Assert.Equal("client-id", handler.ClientId);
        Assert.Equal(1, tokenProvider.GetCalls);
        Assert.Equal(0, tokenProvider.RefreshCalls);

        using var body = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        var root = body.RootElement;
        Assert.Equal(["data"], root.EnumerateObject().Select(item => item.Name).ToArray());
        var data = root.GetProperty("data");
        Assert.Equal(
            ["reason", "user_id"],
            data.EnumerateObject()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal("viewer-42", data.GetProperty("user_id").GetString());
        Assert.Equal(
            "Schwere gezielte Beleidigung.",
            data.GetProperty("reason").GetString());
        Assert.False(data.TryGetProperty("duration", out _));
    }

    [Fact]
    public async Task BanUserAsync_OnAmbiguousTransportFailure_DoesNotRetry()
    {
        var handler = new CapturingHandler
        {
            Exception = new HttpRequestException(
                "The connection was reset after the ban request upload."),
        };
        var tokenProvider = new StubTokenProvider();
        using var httpClient = CreateHttpClient(handler);
        var client = CreateClient(httpClient, tokenProvider);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.BanUserAsync(
                "broadcaster-1",
                "moderator-9",
                "viewer-42",
                "Schwere gezielte Beleidigung."));

        Assert.Contains("reset", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, tokenProvider.GetCalls);
        Assert.Equal(0, tokenProvider.RefreshCalls);
    }

    private static TwitchHelixClient CreateClient(
        HttpClient httpClient,
        ITwitchAccessTokenProvider tokenProvider) =>
        new(
            httpClient,
            tokenProvider,
            new InfrastructureOptions
            {
                TwitchOAuth = new TwitchOAuthOptions
                {
                    ClientId = "client-id",
                },
            });

    private static HttpClient CreateHttpClient(HttpMessageHandler handler) =>
        new(handler)
        {
            BaseAddress = new Uri("https://api.twitch.tv/helix/"),
        };

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string ResponseBody { get; init; } = """{"data":[]}""";

        public Exception? Exception { get; init; }

        public int CallCount { get; private set; }

        public HttpMethod? Method { get; private set; }

        public Uri? RequestUri { get; private set; }

        public AuthenticationHeaderValue? Authorization { get; private set; }

        public string? ClientId { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Authorization = request.Headers.Authorization is { } authorization
                ? new AuthenticationHeaderValue(
                    authorization.Scheme,
                    authorization.Parameter)
                : null;
            ClientId = request.Headers.TryGetValues("Client-Id", out var values)
                ? values.Single()
                : null;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (Exception is not null)
            {
                throw Exception;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody),
            };
        }
    }

    private sealed class StubTokenProvider : ITwitchAccessTokenProvider
    {
        public int GetCalls { get; private set; }

        public int RefreshCalls { get; private set; }

        public Task<bool> HasStoredAuthorizationAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<string> GetAccessTokenAsync(
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult("access-token");
        }

        public Task<string> RefreshAccessTokenAsync(
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            return Task.FromResult("refreshed-token");
        }

        public Task<TwitchTokenValidation> ValidateStoredAuthorizationAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new TwitchTokenValidation(
                    "client-id",
                    "moderator",
                    "sender-9",
                    ["user:write:chat"],
                    3600));

        public Task StoreNewAuthorizationAsync(
            TwitchTokenSet tokenSet,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
