using System.Net;
using System.Text;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.Authentication;

namespace TwitchAI.Tests.Infrastructure;

public sealed class TwitchOAuthClientTests
{
    [Fact]
    public async Task StartDeviceAuthorization_ReturnsExactTwitchLinkAndUserCode()
    {
        using var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/oauth2/device", request.RequestUri?.AbsolutePath);
            var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("client_id=twitch-client-id", form, StringComparison.Ordinal);
            Assert.Contains("scopes=user%3Aread%3Achat", form, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Json(
                    """
                    {
                      "device_code": "device-token",
                      "user_code": "ABCD1234",
                      "verification_uri": "https://www.twitch.tv/activate?public=true&device-code=ABCD1234",
                      "expires_in": 1800,
                      "interval": 5
                    }
                    """),
            };
        });
        using var httpClient = CreateHttpClient(handler);
        var sut = CreateClient(httpClient);

        var authorization = await sut.StartDeviceAuthorizationAsync();

        Assert.Equal("device-token", authorization.DeviceCode);
        Assert.Equal("ABCD1234", authorization.UserCode);
        Assert.Equal(
            "https://www.twitch.tv/activate?public=true&device-code=ABCD1234",
            authorization.VerificationUri.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(5), authorization.PollInterval);
    }

    [Fact]
    public async Task StartDeviceAuthorization_WhenTwitchReportsMissingClientSecret_PreservesHttpFailure()
    {
        using var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("/oauth2/device", request.RequestUri?.AbsolutePath);
            return MissingClientSecretResponse();
        });
        using var httpClient = CreateHttpClient(handler);
        var sut = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.StartDeviceAuthorizationAsync());

        AssertMissingClientSecretFailure(exception);
    }

    [Fact]
    public async Task CompleteDeviceAuthorization_WhenTwitchReportsMissingClientSecret_PreservesHttpFailure()
    {
        using var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("/oauth2/token", request.RequestUri?.AbsolutePath);
            return MissingClientSecretResponse();
        });
        using var httpClient = CreateHttpClient(handler);
        var sut = CreateClient(httpClient);
        var authorization = new TwitchDeviceAuthorization(
            "device-code",
            "user-code",
            new Uri("https://www.twitch.tv/activate"),
            DateTimeOffset.UtcNow.AddMinutes(5),
            TimeSpan.FromSeconds(1),
            ["user:read:chat"]);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.CompleteDeviceAuthorizationAsync(authorization));

        AssertMissingClientSecretFailure(exception);
    }

    [Fact]
    public async Task Refresh_WhenTwitchReportsMissingClientSecret_PreservesHttpFailure()
    {
        using var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal("/oauth2/token", request.RequestUri?.AbsolutePath);
            return MissingClientSecretResponse();
        });
        using var httpClient = CreateHttpClient(handler);
        var sut = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.RefreshAsync("refresh-token"));

        AssertMissingClientSecretFailure(exception);
    }

    [Fact]
    public async Task StartDeviceAuthorization_ForOtherTwitchFailure_PreservesHttpFailure()
    {
        using var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = Json("{\"status\":400,\"message\":\"invalid client id\"}"),
            });
        using var httpClient = CreateHttpClient(handler);
        var sut = CreateClient(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.StartDeviceAuthorizationAsync());

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("invalid client id", exception.Message);
    }

    private static TwitchOAuthClient CreateClient(HttpClient httpClient)
    {
        return new TwitchOAuthClient(
            httpClient,
            new InfrastructureOptions
            {
                TwitchOAuth = new TwitchOAuthOptions
                {
                    ClientId = "twitch-client-id",
                    Scopes = ["user:read:chat"],
                },
            });
    }

    private static HttpClient CreateHttpClient(HttpMessageHandler handler)
    {
        return new HttpClient(handler)
        {
            BaseAddress = new Uri("https://id.twitch.tv/oauth2/"),
        };
    }

    private static HttpResponseMessage MissingClientSecretResponse()
    {
        return new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = Json("{\"status\":400,\"message\":\"missing client secret\"}"),
        };
    }

    private static StringContent Json(string value)
    {
        return new StringContent(value, Encoding.UTF8, "application/json");
    }

    private static void AssertMissingClientSecretFailure(
        HttpRequestException exception)
    {
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("missing client secret", exception.Message);
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responseFactory(request));
        }
    }
}
