using System.Net;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.Authentication;
using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.App.Tests.ViewModels;

public sealed class SettingsViewModelTwitchRestoreTests
{
    [Fact]
    public async Task Restore_WhenRefreshNeedsClientSecret_AllowsFreshDeviceFlow()
    {
        const string clientId = "confidential-client-id";
        var options = new InfrastructureOptions
        {
            TwitchOAuth = new TwitchOAuthOptions
            {
                ClientId = clientId,
            },
        };
        var storage = new MemoryLocalAppStorage(new Dictionary<string, string>
        {
            ["TwitchClientId"] = clientId,
        });
        var tokenProvider = new ConfigurationFailureTokenProvider();
        using var viewModel = new SettingsViewModel(
            new AppState(),
            options,
            null!,
            tokenProvider,
            null!,
            new TwitchConnectionReadiness(),
            null!,
            null!,
            null!,
            null!,
            null!,
            storage,
            new ModerationQueueRuntimeSettings(),
            new TwitchRuntimeModeSettings(),
            null!);

        var first = await viewModel.RestoreTwitchSessionAsync();

        Assert.Equal(TwitchSessionRestoreOutcome.ReauthorizationRequired, first.Outcome);
        Assert.True(first.MayStartDeviceAuthorization);
        Assert.Contains("neue Freigabe", first.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, tokenProvider.HasStoredAuthorizationCalls);
        Assert.Equal(1, tokenProvider.ValidateCalls);

        var second = await viewModel.RestoreTwitchSessionAsync();

        Assert.Equal(TwitchSessionRestoreOutcome.ReauthorizationRequired, second.Outcome);
        Assert.True(second.MayStartDeviceAuthorization);
        Assert.Equal(2, tokenProvider.HasStoredAuthorizationCalls);
        Assert.Equal(2, tokenProvider.ValidateCalls);
    }

    private sealed class ConfigurationFailureTokenProvider : ITwitchAccessTokenProvider
    {
        public int HasStoredAuthorizationCalls { get; private set; }

        public int ValidateCalls { get; private set; }

        public Task<bool> HasStoredAuthorizationAsync(
            CancellationToken cancellationToken = default)
        {
            HasStoredAuthorizationCalls++;
            return Task.FromResult(true);
        }

        public Task<string> GetAccessTokenAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> RefreshAccessTokenAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TwitchTokenValidation> ValidateStoredAuthorizationAsync(
            CancellationToken cancellationToken = default)
        {
            ValidateCalls++;
            return Task.FromException<TwitchTokenValidation>(
                new HttpRequestException(
                    "missing client secret",
                    null,
                    HttpStatusCode.BadRequest));
        }

        public Task StoreNewAuthorizationAsync(
            TwitchTokenSet tokenSet,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MemoryLocalAppStorage(
        IReadOnlyDictionary<string, string> initialValues)
        : ILocalAppStorage
    {
        private readonly Dictionary<string, string> _values = new(
            initialValues,
            StringComparer.Ordinal);

        public string LocalFolderPath => string.Empty;

        public string? GetString(string key) => _values.GetValueOrDefault(key);

        public void SetString(string key, string value)
        {
            _values[key] = value;
        }

        public void SetStrings(IReadOnlyDictionary<string, string> values)
        {
            foreach (var (key, value) in values)
            {
                _values[key] = value;
            }
        }
    }
}
