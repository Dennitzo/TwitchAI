using System.Net.Http.Json;
using System.Text.Json;
using Windows.Security.Credentials;

namespace TwitchAI.Infrastructure.Authentication;

public sealed record TwitchDeviceAuthorization(
    string DeviceCode,
    string UserCode,
    Uri VerificationUri,
    DateTimeOffset ExpiresAt,
    TimeSpan PollInterval,
    IReadOnlyList<string> Scopes);

public sealed record TwitchTokenSet(
    string AccessToken,
    string RefreshToken,
    string TokenType,
    IReadOnlyList<string> Scopes,
    DateTimeOffset ExpiresAt);

public sealed record TwitchTokenValidation(
    string ClientId,
    string Login,
    string UserId,
    IReadOnlyList<string> Scopes,
    int ExpiresInSeconds);

public interface ITwitchCredentialVault
{
    Task<TwitchTokenSet?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TwitchTokenSet tokenSet, CancellationToken cancellationToken = default);

    Task DeleteAsync(CancellationToken cancellationToken = default);
}

public sealed class WindowsPasswordVaultCredentialStore : ITwitchCredentialVault
{
    private const int ElementNotFoundHResult = unchecked((int)0x80070490);
    private readonly PasswordVault _vault = new();
    private readonly TwitchOAuthOptions _options;

    public WindowsPasswordVaultCredentialStore(InfrastructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.TwitchOAuth;
    }

    public Task<TwitchTokenSet?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var credential = _vault.Retrieve(_options.VaultResource, _options.VaultAccount);
            credential.RetrievePassword();
            TwitchTokenSet tokenSet;
            try
            {
                tokenSet = JsonSerializer.Deserialize<TwitchTokenSet>(
                        credential.Password)
                    ?? throw new InvalidDataException(
                        "The Twitch credential in PasswordVault is invalid.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    "The Twitch credential in PasswordVault contains malformed JSON.",
                    exception);
            }

            return Task.FromResult<TwitchTokenSet?>(tokenSet);
        }
        catch (Exception exception) when (exception.HResult == ElementNotFoundHResult)
        {
            return Task.FromResult<TwitchTokenSet?>(null);
        }
    }

    public Task SaveAsync(
        TwitchTokenSet tokenSet,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tokenSet);
        cancellationToken.ThrowIfCancellationRequested();
        RemoveExisting();
        _vault.Add(new PasswordCredential(
            _options.VaultResource,
            _options.VaultAccount,
            JsonSerializer.Serialize(tokenSet)));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveExisting();
        return Task.CompletedTask;
    }

    private void RemoveExisting()
    {
        try
        {
            var existing = _vault.Retrieve(_options.VaultResource, _options.VaultAccount);
            _vault.Remove(existing);
        }
        catch (Exception exception) when (exception.HResult == ElementNotFoundHResult)
        {
            // PasswordVault uses HRESULT_FROM_WIN32(ERROR_NOT_FOUND) for a missing credential.
        }
    }
}

public interface ITwitchOAuthClient
{
    Task<TwitchDeviceAuthorization> StartDeviceAuthorizationAsync(
        CancellationToken cancellationToken = default);

    Task<TwitchTokenSet> CompleteDeviceAuthorizationAsync(
        TwitchDeviceAuthorization authorization,
        CancellationToken cancellationToken = default);

    Task<TwitchTokenSet> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default);

    Task<TwitchTokenValidation> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken = default);
}

public sealed class TwitchOAuthClient : ITwitchOAuthClient
{
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";
    private readonly HttpClient _httpClient;
    private readonly TwitchOAuthOptions _options;

    public TwitchOAuthClient(HttpClient httpClient, InfrastructureOptions options)
    {
        _httpClient = httpClient;
        _options = options.TwitchOAuth;
    }

    public async Task<TwitchDeviceAuthorization> StartDeviceAuthorizationAsync(
        CancellationToken cancellationToken = default)
    {
        EnsureClientId();
        var scopes = CanonicalScopes();
        using var content = Form(
            ("client_id", _options.ClientId),
            ("scopes", string.Join(' ', scopes)));
        using var response = await _httpClient
            .PostAsync("device", content, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, document);
        var root = document.RootElement;

        return new TwitchDeviceAuthorization(
            RequiredString(root, "device_code"),
            RequiredString(root, "user_code"),
            new Uri(RequiredString(root, "verification_uri"), UriKind.Absolute),
            DateTimeOffset.UtcNow.AddSeconds(RequiredInt32(root, "expires_in")),
            TimeSpan.FromSeconds(Math.Max(1, RequiredInt32(root, "interval"))),
            scopes);
    }

    public async Task<TwitchTokenSet> CompleteDeviceAuthorizationAsync(
        TwitchDeviceAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        EnsureClientId();
        var pollInterval = authorization.PollInterval;

        while (DateTimeOffset.UtcNow < authorization.ExpiresAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var content = Form(
                ("client_id", _options.ClientId),
                ("scopes", string.Join(' ', authorization.Scopes)),
                ("device_code", authorization.DeviceCode),
                ("grant_type", DeviceGrantType));
            using var response = await _httpClient
                .PostAsync("token", content, cancellationToken)
                .ConfigureAwait(false);
            using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return ParseTokenSet(document.RootElement);
            }

            var message = OptionalString(document.RootElement, "message");
            if (StringComparer.OrdinalIgnoreCase.Equals(message, "authorization_pending"))
            {
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (StringComparer.OrdinalIgnoreCase.Equals(message, "slow_down"))
            {
                pollInterval += TimeSpan.FromSeconds(5);
                await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            EnsureSuccess(response, document);
        }

        throw new TimeoutException("The Twitch device authorization code expired.");
    }

    public async Task<TwitchTokenSet> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        EnsureClientId();
        using var content = Form(
            ("client_id", _options.ClientId),
            ("grant_type", "refresh_token"),
            ("refresh_token", refreshToken));
        using var response = await _httpClient
            .PostAsync("token", content, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, document);
        return ParseTokenSet(document.RootElement);
    }

    public async Task<TwitchTokenValidation> ValidateAsync(
        string accessToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "validate");
        request.Headers.Authorization = new("OAuth", accessToken);
        using var response = await _httpClient
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        using var document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, document);
        var root = document.RootElement;

        return new TwitchTokenValidation(
            RequiredString(root, "client_id"),
            RequiredString(root, "login"),
            RequiredString(root, "user_id"),
            ReadScopes(root, "scopes"),
            RequiredInt32(root, "expires_in"));
    }

    private static TwitchTokenSet ParseTokenSet(JsonElement root)
    {
        return new TwitchTokenSet(
            RequiredString(root, "access_token"),
            RequiredString(root, "refresh_token"),
            OptionalString(root, "token_type") ?? "bearer",
            ReadScopes(root, "scope"),
            DateTimeOffset.UtcNow.AddSeconds(RequiredInt32(root, "expires_in")));
    }

    private string[] CanonicalScopes()
    {
        return _options.Scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray();
    }

    private void EnsureClientId()
    {
        if (string.IsNullOrWhiteSpace(_options.ClientId))
        {
            throw new InvalidOperationException(
                "Twitch OAuth ClientId is not configured. Register a Twitch public client first.");
        }
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] values)
    {
        return new FormUrlEncodedContent(values.Select(item =>
            new KeyValuePair<string, string>(item.Key, item.Value)));
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content
                .ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidDataException("Twitch returned an empty JSON document.");
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException(
                $"Twitch returned malformed JSON with HTTP {(int)response.StatusCode}.",
                exception,
                response.StatusCode);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, JsonDocument document)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var message = OptionalString(document.RootElement, "message")
            ?? response.ReasonPhrase
            ?? "Twitch authentication request failed.";
        throw new HttpRequestException(message, null, response.StatusCode);
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        return OptionalString(parent, name)
            ?? throw new InvalidDataException($"Twitch response is missing '{name}'.");
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static int RequiredInt32(JsonElement parent, string name)
    {
        return parent.TryGetProperty(name, out var property) &&
               property.TryGetInt32(out var value)
            ? value
            : throw new InvalidDataException($"Twitch response is missing numeric '{name}'.");
    }

    private static string[] ReadScopes(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var property))
        {
            return [];
        }

        if (property.ValueKind == JsonValueKind.Array)
        {
            return property
                .EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray();
        }

        return property.ValueKind == JsonValueKind.String
            ? (property.GetString() ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
    }
}

public interface ITwitchAccessTokenProvider
{
    Task<bool> HasStoredAuthorizationAsync(
        CancellationToken cancellationToken = default);

    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    Task<string> RefreshAccessTokenAsync(CancellationToken cancellationToken = default);

    Task<TwitchTokenValidation> ValidateStoredAuthorizationAsync(
        CancellationToken cancellationToken = default);

    Task StoreNewAuthorizationAsync(
        TwitchTokenSet tokenSet,
        CancellationToken cancellationToken = default);
}

public sealed class TwitchAuthorizationRequiredException : InvalidOperationException
{
    public TwitchAuthorizationRequiredException()
        : base("Twitch is not authorized.")
    {
    }
}

public sealed class TwitchAccessTokenProvider : ITwitchAccessTokenProvider, IDisposable
{
    private readonly ITwitchCredentialVault _vault;
    private readonly ITwitchOAuthClient _oauthClient;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private TwitchTokenSet? _cached;
    private bool _disposed;

    public TwitchAccessTokenProvider(
        ITwitchCredentialVault vault,
        ITwitchOAuthClient oauthClient)
    {
        _vault = vault;
        _oauthClient = oauthClient;
    }

    public async Task<bool> HasStoredAuthorizationAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cached is not null)
        {
            return true;
        }

        _cached = await _vault.LoadAsync(cancellationToken).ConfigureAwait(false);
        return _cached is not null;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var token = _cached ??= await _vault.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            throw new TwitchAuthorizationRequiredException();
        }

        return token.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2)
            ? token.AccessToken
            : await RefreshAccessTokenAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RefreshAccessTokenAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var token = _cached ??= await _vault.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (token is null)
            {
                throw new TwitchAuthorizationRequiredException();
            }

            var refreshed = await _oauthClient
                .RefreshAsync(token.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
            await _vault.SaveAsync(refreshed, cancellationToken).ConfigureAwait(false);
            _cached = refreshed;
            return refreshed.AccessToken;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task<TwitchTokenValidation> ValidateStoredAuthorizationAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var accessToken = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _oauthClient
                .ValidateAsync(accessToken, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Access tokens may be revoked before their recorded expiry. A valid
            // refresh token restores the persisted session without another
            // interactive Device Code authorization.
            accessToken = await RefreshAccessTokenAsync(cancellationToken)
                .ConfigureAwait(false);
            return await _oauthClient
                .ValidateAsync(accessToken, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task StoreNewAuthorizationAsync(
        TwitchTokenSet tokenSet,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(tokenSet);
        await _vault.SaveAsync(tokenSet, cancellationToken).ConfigureAwait(false);
        _cached = tokenSet;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _refreshGate.Dispose();
        _disposed = true;
    }
}
