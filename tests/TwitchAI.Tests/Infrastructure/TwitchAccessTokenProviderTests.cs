using System.Net;
using System.Text.Json;
using TwitchAI.Infrastructure.Authentication;

namespace TwitchAI.Tests.Infrastructure;

public sealed class TwitchAccessTokenProviderTests
{
    [Fact]
    public async Task ValidateStoredAuthorization_WhenAccessTokenIsValid_ReusesVaultSession()
    {
        var vault = new FakeCredentialVault(CreateToken("stored-access", "stored-refresh"));
        var oauth = new FakeOAuthClient();
        using var sut = new TwitchAccessTokenProvider(vault, oauth);

        var validation = await sut.ValidateStoredAuthorizationAsync();

        Assert.Equal("moderator-login", validation.Login);
        Assert.Equal(["stored-access"], oauth.ValidatedAccessTokens);
        Assert.Equal(0, oauth.RefreshCalls);
        Assert.Equal(0, vault.SaveCalls);
    }

    [Fact]
    public async Task ValidateStoredAuthorization_WhenAccessTokenWasRevoked_RefreshesAndPersistsSession()
    {
        var vault = new FakeCredentialVault(CreateToken("revoked-access", "stored-refresh"));
        var oauth = new FakeOAuthClient
        {
            RejectedAccessToken = "revoked-access",
            RefreshedToken = CreateToken("fresh-access", "fresh-refresh"),
        };
        using var sut = new TwitchAccessTokenProvider(vault, oauth);

        var validation = await sut.ValidateStoredAuthorizationAsync();

        Assert.Equal("moderator-login", validation.Login);
        Assert.Equal(["revoked-access", "fresh-access"], oauth.ValidatedAccessTokens);
        Assert.Equal(1, oauth.RefreshCalls);
        Assert.Equal(1, vault.SaveCalls);
        Assert.Equal("fresh-access", vault.StoredToken?.AccessToken);
        Assert.Equal("fresh-refresh", vault.StoredToken?.RefreshToken);
    }

    [Fact]
    public async Task ValidateStoredAuthorization_WhenTwitchIsTemporarilyUnavailable_DoesNotReplaceCredentials()
    {
        var originalToken = CreateToken("stored-access", "stored-refresh");
        var vault = new FakeCredentialVault(originalToken);
        var oauth = new FakeOAuthClient
        {
            ValidationException = new HttpRequestException(
                "network unavailable",
                inner: null,
                statusCode: null),
        };
        using var sut = new TwitchAccessTokenProvider(vault, oauth);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.ValidateStoredAuthorizationAsync());

        Assert.Equal(0, oauth.RefreshCalls);
        Assert.Equal(0, vault.SaveCalls);
        Assert.Same(originalToken, vault.StoredToken);
    }

    [Fact]
    public async Task StoredAuthorization_WhenVaultIsEmpty_ReportsAuthorizationRequired()
    {
        var vault = new FakeCredentialVault(token: null);
        var oauth = new FakeOAuthClient();
        using var sut = new TwitchAccessTokenProvider(vault, oauth);

        Assert.False(await sut.HasStoredAuthorizationAsync());
        await Assert.ThrowsAsync<TwitchAuthorizationRequiredException>(
            () => sut.ValidateStoredAuthorizationAsync());

        Assert.Equal(0, oauth.RefreshCalls);
        Assert.Empty(oauth.ValidatedAccessTokens);
    }

    [Fact]
    public async Task HasStoredAuthorization_WhenVaultCredentialIsCorrupt_PropagatesInvalidDataException()
    {
        var jsonException = new JsonException("malformed credential payload");
        var expectedException = new InvalidDataException(
            "The Twitch credential in PasswordVault contains malformed JSON.",
            jsonException);
        var vault = new FakeCredentialVault(token: null)
        {
            LoadException = expectedException,
        };
        var oauth = new FakeOAuthClient();
        using var sut = new TwitchAccessTokenProvider(vault, oauth);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => sut.HasStoredAuthorizationAsync());

        Assert.Same(expectedException, exception);
        Assert.Same(jsonException, exception.InnerException);
        Assert.Equal(0, oauth.RefreshCalls);
        Assert.Empty(oauth.ValidatedAccessTokens);
    }

    [Fact]
    public async Task GetAccessToken_WhenRefreshRequiresClientSecret_PreservesHttpFailure()
    {
        var expiredToken = CreateToken("expired-access", "stored-refresh") with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        var vault = new FakeCredentialVault(expiredToken);
        var expected = new HttpRequestException(
            "missing client secret",
            null,
            HttpStatusCode.BadRequest);
        var oauth = new FakeOAuthClient
        {
            RefreshException = expected,
        };
        using var sut = new TwitchAccessTokenProvider(vault, oauth);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => sut.GetAccessTokenAsync());

        Assert.Same(expected, exception);
        Assert.Equal(1, oauth.RefreshCalls);
        Assert.Equal(0, vault.SaveCalls);
        Assert.Same(expiredToken, vault.StoredToken);
    }

    private static TwitchTokenSet CreateToken(string accessToken, string refreshToken)
    {
        return new TwitchTokenSet(
            accessToken,
            refreshToken,
            "bearer",
            ["user:read:chat"],
            DateTimeOffset.UtcNow.AddHours(1));
    }

    private sealed class FakeCredentialVault(TwitchTokenSet? token)
        : ITwitchCredentialVault
    {
        public TwitchTokenSet? StoredToken { get; private set; } = token;

        public Exception? LoadException { get; init; }

        public int SaveCalls { get; private set; }

        public Task<TwitchTokenSet?> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            if (LoadException is not null)
            {
                return Task.FromException<TwitchTokenSet?>(LoadException);
            }

            return Task.FromResult(StoredToken);
        }

        public Task SaveAsync(
            TwitchTokenSet tokenSet,
            CancellationToken cancellationToken = default)
        {
            StoredToken = tokenSet;
            SaveCalls++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            StoredToken = null;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOAuthClient : ITwitchOAuthClient
    {
        public string? RejectedAccessToken { get; init; }

        public TwitchTokenSet RefreshedToken { get; init; } =
            CreateToken("fresh-access", "fresh-refresh");

        public Exception? ValidationException { get; init; }

        public Exception? RefreshException { get; init; }

        public List<string> ValidatedAccessTokens { get; } = [];

        public int RefreshCalls { get; private set; }

        public Task<TwitchDeviceAuthorization> StartDeviceAuthorizationAsync(
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<TwitchTokenSet> CompleteDeviceAuthorizationAsync(
            TwitchDeviceAuthorization authorization,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<TwitchTokenSet> RefreshAsync(
            string refreshToken,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            if (RefreshException is not null)
            {
                return Task.FromException<TwitchTokenSet>(RefreshException);
            }

            return Task.FromResult(RefreshedToken);
        }

        public Task<TwitchTokenValidation> ValidateAsync(
            string accessToken,
            CancellationToken cancellationToken = default)
        {
            ValidatedAccessTokens.Add(accessToken);
            if (ValidationException is not null)
            {
                return Task.FromException<TwitchTokenValidation>(
                    ValidationException);
            }

            if (StringComparer.Ordinal.Equals(accessToken, RejectedAccessToken))
            {
                return Task.FromException<TwitchTokenValidation>(
                    new HttpRequestException(
                        "invalid access token",
                        inner: null,
                        HttpStatusCode.Unauthorized));
            }

            return Task.FromResult(
                new TwitchTokenValidation(
                    "client-id",
                    "moderator-login",
                    "moderator-id",
                    ["user:read:chat"],
                    3600));
        }
    }
}
