using System.Collections.Concurrent;
using System.Net;
using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.Authentication;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Pipeline;
using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.App.Tests.ViewModels;

public sealed class SettingsViewModelTwitchRestoreTests
{
    [Fact]
    public void Channels_AreNormalizedWhenLoadedAndSaved()
    {
        var options = new InfrastructureOptions();
        var storage = new MemoryLocalAppStorage(new Dictionary<string, string>
        {
            ["ChannelLogin"] = " ChannelOne, #CHANNELTWO, channelone ",
        });
        using var viewModel = new SettingsViewModel(
            new AppState(),
            options,
            null!,
            null!,
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
            null!,
            null!);

        Assert.Equal("channelone, channeltwo", viewModel.ChannelLogin);

        viewModel.ChannelLogin = " ChannelThree, @CHANNELFOUR, channelthree ";
        viewModel.Save();

        Assert.Equal("channelthree, channelfour", viewModel.ChannelLogin);
        Assert.Equal(
            "channelthree, channelfour",
            storage.GetString("ChannelLogin"));
    }

    [Fact]
    public void LmStudioModel_WhenSelected_IsAppliedAndPersistedImmediately()
    {
        var options = new InfrastructureOptions();
        var storage = new MemoryLocalAppStorage(new Dictionary<string, string>
        {
            ["LmStudioModel"] = "openai/gpt-oss-20b",
        });
        using var viewModel = new SettingsViewModel(
            new AppState(),
            options,
            null!,
            null!,
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
            null!,
            null!);

        viewModel.LmStudioModel = "openai/gpt-oss-120b";

        Assert.Equal("openai/gpt-oss-120b", options.LmStudio.Model);
        Assert.Equal(
            "openai/gpt-oss-120b",
            storage.GetString("LmStudioModel"));
        Assert.Equal(
            "LM-Studio-Modell automatisch gespeichert",
            viewModel.SaveStatus);
    }

    [Fact]
    public void CheckTwitch_WithMultipleChannels_AppliesObservableStateOnCapturedContext()
    {
        PumpingSynchronizationContext.Run(
            async () =>
            {
                var expectedThreadId = Environment.CurrentManagedThreadId;
                var databaseDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "TwitchAI.App.Tests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(databaseDirectory);

                try
                {
                    const string clientId = "public-client-id";
                    var options = new InfrastructureOptions
                    {
                        DatabasePath = Path.Combine(databaseDirectory, "settings.db"),
                        TwitchOAuth = new TwitchOAuthOptions
                        {
                            ClientId = clientId,
                        },
                    };
                    var connectionFactory = new SqliteConnectionFactory(options);
                    await CreateCategoryTableAsync(connectionFactory);
                    using var writeCoordinator = new SqliteWriteCoordinator();
                    var state = new AppState();
                    var storage = new MemoryLocalAppStorage(
                        new Dictionary<string, string>
                        {
                            ["TwitchClientId"] = clientId,
                            ["ChannelLogin"] = "channelone, channeltwo",
                        });
                    using var viewModel = new SettingsViewModel(
                        state,
                        options,
                        null!,
                        new AsynchronousValidTokenProvider(
                            clientId,
                            options.TwitchOAuth.Scopes),
                        new CompleteAsynchronousMultiChannelHelixClient(),
                        new TwitchConnectionReadiness(),
                        null!,
                        null!,
                        connectionFactory,
                        writeCoordinator,
                        new AsynchronousPolicyStore(),
                        storage,
                        new ModerationQueueRuntimeSettings(),
                        new TwitchRuntimeModeSettings(),
                        new TrackingCoordinator(isRunning: false),
                        null!);

                    var wrongThreadNotifications = new ConcurrentQueue<string>();
                    var viewModelNotifications = 0;
                    var stateNotifications = 0;
                    viewModel.PropertyChanged += (_, eventArgs) =>
                    {
                        Interlocked.Increment(ref viewModelNotifications);
                        if (Environment.CurrentManagedThreadId != expectedThreadId)
                        {
                            wrongThreadNotifications.Enqueue(
                                $"SettingsViewModel.{eventArgs.PropertyName}");
                        }
                    };
                    state.PropertyChanged += (_, eventArgs) =>
                    {
                        Interlocked.Increment(ref stateNotifications);
                        if (Environment.CurrentManagedThreadId != expectedThreadId)
                        {
                            wrongThreadNotifications.Enqueue(
                                $"AppState.{eventArgs.PropertyName}");
                        }
                    };

                    await viewModel.CheckTwitchAsync();

                    Assert.True(viewModelNotifications > 0);
                    Assert.True(stateNotifications > 0);
                    Assert.Empty(wrongThreadNotifications);
                    Assert.Equal(["channel-1", "channel-2"], options.TwitchApi.GetBroadcasterIds());
                    Assert.Equal("channelone, channeltwo", viewModel.ChannelLogin);
                    Assert.True(state.IsTwitchConnectionReady);
                    Assert.Equal("Twitch verbunden · 2 Kanäle", state.TwitchStatus);
                }
                finally
                {
                    SqliteConnection.ClearAllPools();
                    Directory.Delete(databaseDirectory, recursive: true);
                }
            });
    }

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
            null!,
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

    [Fact]
    public async Task CheckTwitch_WhenAChannelCannotBePrepared_KeepsActiveConfiguration()
    {
        const string clientId = "public-client-id";
        var options = new InfrastructureOptions
        {
            TwitchOAuth = new TwitchOAuthOptions
            {
                ClientId = clientId,
            },
        };
        options.TwitchApi.ReaderUserId = "old-reader";
        options.TwitchApi.ModeratorUserId = "old-moderator";
        options.TwitchApi.SetBroadcasterIds(["old-channel"]);
        var storage = new MemoryLocalAppStorage(new Dictionary<string, string>
        {
            ["TwitchClientId"] = clientId,
            ["ChannelLogin"] = "oldlogin",
            ["BroadcasterId"] = "old-channel",
            ["BroadcasterIds"] = "old-channel",
            ["ReaderUserId"] = "old-reader",
            ["ModeratorUserId"] = "old-moderator",
        });
        var state = new AppState
        {
            ChannelDisplayName = "Old channel",
            CategoryName = "Old category",
            ActivePolicies = "Nur General",
            TwitchStatus = "Twitch verbunden",
            IsTwitchConnectionReady = true,
            IsAnalysisRunning = true,
        };
        var coordinator = new TrackingCoordinator(isRunning: true);
        using var viewModel = new SettingsViewModel(
            state,
            options,
            null!,
            new ValidTokenProvider(clientId, options.TwitchOAuth.Scopes),
            new IncompleteMultiChannelHelixClient(),
            new TwitchConnectionReadiness(),
            null!,
            null!,
            null!,
            null!,
            null!,
            storage,
            new ModerationQueueRuntimeSettings(),
            new TwitchRuntimeModeSettings(),
            coordinator,
            null!)
        {
            ChannelLogin = "newone, newtwo",
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.CheckTwitchAsync());

        Assert.Contains("newtwo", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(coordinator.IsRunning);
        Assert.Equal(0, coordinator.StopCalls);
        Assert.Equal(0, coordinator.StartCalls);
        Assert.True(state.IsAnalysisRunning);
        Assert.True(state.IsTwitchConnectionReady);
        Assert.Equal("Old channel", state.ChannelDisplayName);
        Assert.Equal("Old category", state.CategoryName);
        Assert.Equal("Twitch verbunden", state.TwitchStatus);
        Assert.Equal("old-reader", options.TwitchApi.ReaderUserId);
        Assert.Equal("old-moderator", options.TwitchApi.ModeratorUserId);
        Assert.Equal(["old-channel"], options.TwitchApi.GetBroadcasterIds());
        Assert.Equal("old-channel", storage.GetString("BroadcasterIds"));
        Assert.Equal("oldlogin", storage.GetString("ChannelLogin"));
        Assert.Equal(0, storage.SetStringsCalls);
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

    private sealed class ValidTokenProvider(
        string clientId,
        IReadOnlyList<string> scopes) : ITwitchAccessTokenProvider
    {
        public Task<bool> HasStoredAuthorizationAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<string> GetAccessTokenAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult("token");

        public Task<string> RefreshAccessTokenAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult("token");

        public Task<TwitchTokenValidation> ValidateStoredAuthorizationAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new TwitchTokenValidation(
                    clientId,
                    "account",
                    "reader",
                    scopes,
                    3600));

        public Task StoreNewAuthorizationAsync(
            TwitchTokenSet tokenSet,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class AsynchronousValidTokenProvider(
        string clientId,
        IReadOnlyList<string> scopes) : ITwitchAccessTokenProvider
    {
        public Task<bool> HasStoredAuthorizationAsync(
            CancellationToken cancellationToken = default) =>
            CompleteAsynchronously(true, cancellationToken);

        public Task<string> GetAccessTokenAsync(
            CancellationToken cancellationToken = default) =>
            CompleteAsynchronously("token", cancellationToken);

        public Task<string> RefreshAccessTokenAsync(
            CancellationToken cancellationToken = default) =>
            CompleteAsynchronously("token", cancellationToken);

        public Task<TwitchTokenValidation> ValidateStoredAuthorizationAsync(
            CancellationToken cancellationToken = default) =>
            CompleteAsynchronously(
                new TwitchTokenValidation(
                    clientId,
                    "account",
                    "reader",
                    scopes,
                    3600),
                cancellationToken);

        public Task StoreNewAuthorizationAsync(
            TwitchTokenSet tokenSet,
            CancellationToken cancellationToken = default) =>
            CompleteAsynchronously(true, cancellationToken);
    }

    private sealed class CompleteAsynchronousMultiChannelHelixClient : ITwitchHelixClient
    {
        public Task<TwitchUserInfo> GetCurrentUserAsync(
            CancellationToken cancellationToken = default) =>
            CompleteAsynchronously(
                new TwitchUserInfo("reader", "account", "Account"),
                cancellationToken);

        public Task<TwitchChannelInfo?> GetChannelAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default) =>
            CompleteAsynchronously<TwitchChannelInfo?>(
                broadcasterId switch
                {
                    "channel-1" => new TwitchChannelInfo(
                        "channel-1",
                        "channelone",
                        "Channel one",
                        "game-1",
                        "Game one",
                        string.Empty,
                        "de"),
                    "channel-2" => new TwitchChannelInfo(
                        "channel-2",
                        "channeltwo",
                        "Channel two",
                        "game-2",
                        "Game two",
                        string.Empty,
                        "de"),
                    _ => null,
                },
                cancellationToken);

        public Task<IReadOnlyList<TwitchGameInfo>> GetGamesAsync(
            IReadOnlyCollection<string> categoryIds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TwitchGameInfo?> GetGameByNameAsync(
            string categoryName,
            CancellationToken cancellationToken = default) =>
            CompleteAsynchronously<TwitchGameInfo?>(
                categoryName switch
                {
                    "Just Chatting" => new TwitchGameInfo(
                        EffectivePolicyResolver.BootstrapJustChattingCategoryId,
                        "Just Chatting",
                        string.Empty),
                    "IRL" => new TwitchGameInfo(
                        EffectivePolicyResolver.BootstrapIrlCategoryId,
                        "IRL",
                        string.Empty),
                    _ => null,
                },
                cancellationToken);

        public Task<IReadOnlyList<TwitchModeratedChannel>> GetModeratedChannelsAsync(
            string userId,
            CancellationToken cancellationToken = default) =>
            CompleteAsynchronously<IReadOnlyList<TwitchModeratedChannel>>(
            [
                new("channel-1", "channelone", "Channel one"),
                new("channel-2", "channeltwo", "Channel two"),
            ],
            cancellationToken);

        public Task<TwitchBannedUserInfo?> GetBannedUserAsync(
            string broadcasterId,
            string userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteMessageAsync(
            string broadcasterId,
            string moderatorId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task TimeoutUserAsync(
            string broadcasterId,
            string moderatorId,
            string userId,
            int durationSeconds,
            string reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task BanUserAsync(
            string broadcasterId,
            string moderatorId,
            string userId,
            string reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TwitchSendChatMessageResult> SendChatMessageAsync(
            string broadcasterId,
            string senderId,
            string message,
            string? replyParentMessageId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CreateEventSubSubscriptionAsync(
            TwitchEventSubSubscriptionRequest subscription,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class AsynchronousPolicyStore : IPolicyStore
    {
        public Task EnsureBroadcasterSeededAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default) =>
            CompleteAsynchronously(true, cancellationToken);

        public Task<EffectivePolicySnapshot> GetEffectivePolicyAsync(
            string broadcasterId,
            string? categoryId,
            CancellationToken cancellationToken = default) =>
            CompleteAsynchronously(
                new EffectivePolicySnapshot(
                    broadcasterId,
                    categoryId,
                    1,
                    1,
                    $"policy-{broadcasterId}",
                    string.Empty,
                    [],
                    []),
                cancellationToken);
    }

    private sealed class PumpingSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)>
            _workItems = [];

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (!_workItems.IsAddingCompleted)
            {
                _workItems.Add((callback, state));
            }
        }

        public static void Run(Func<Task> action)
        {
            Exception? failure = null;
            var thread = new Thread(
                () =>
                {
                    using var context = new PumpingSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(context);
                    try
                    {
                        var task = action();
                        _ = task.ContinueWith(
                            _ => context._workItems.CompleteAdding(),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                        foreach (var (callback, state) in context._workItems.GetConsumingEnumerable())
                        {
                            callback(state);
                        }

                        task.GetAwaiter().GetResult();
                    }
                    catch (Exception exception)
                    {
                        failure = exception;
                    }
                    finally
                    {
                        SynchronizationContext.SetSynchronizationContext(null);
                    }
                })
            {
                IsBackground = true,
                Name = "SettingsViewModel test UI thread",
            };

            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The test UI thread did not complete.");
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }

        public void Dispose() => _workItems.Dispose();
    }

    private static async Task<T> CompleteAsynchronously<T>(
        T result,
        CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task CreateCategoryTableAsync(
        SqliteConnectionFactory connectionFactory)
    {
        await using var connection = await connectionFactory.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE twitch_categories(
                category_id TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                last_verified_utc TEXT NULL);
            """;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class IncompleteMultiChannelHelixClient : ITwitchHelixClient
    {
        public Task<TwitchUserInfo> GetCurrentUserAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new TwitchUserInfo("reader", "account", "Account"));

        public Task<TwitchChannelInfo?> GetChannelAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TwitchChannelInfo?>(
                broadcasterId == "new-1"
                    ? new TwitchChannelInfo(
                        "new-1",
                        "newone",
                        "New one",
                        "category-1",
                        "Category one",
                        string.Empty,
                        "de")
                    : null);

        public Task<IReadOnlyList<TwitchGameInfo>> GetGamesAsync(
            IReadOnlyCollection<string> categoryIds,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TwitchGameInfo?> GetGameByNameAsync(
            string categoryName,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<TwitchModeratedChannel>> GetModeratedChannelsAsync(
            string userId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TwitchModeratedChannel>>(
            [
                new("new-1", "newone", "New one"),
                new("new-2", "newtwo", "New two"),
            ]);

        public Task<TwitchBannedUserInfo?> GetBannedUserAsync(
            string broadcasterId,
            string userId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteMessageAsync(
            string broadcasterId,
            string moderatorId,
            string messageId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task TimeoutUserAsync(
            string broadcasterId,
            string moderatorId,
            string userId,
            int durationSeconds,
            string reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task BanUserAsync(
            string broadcasterId,
            string moderatorId,
            string userId,
            string reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TwitchSendChatMessageResult> SendChatMessageAsync(
            string broadcasterId,
            string senderId,
            string message,
            string? replyParentMessageId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task CreateEventSubSubscriptionAsync(
            TwitchEventSubSubscriptionRequest subscription,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TrackingCoordinator(bool isRunning) : IModerationCoordinator
    {
        public event EventHandler<ModerationPipelineEventArgs>? EventReceived
        {
            add { }
            remove { }
        }

        public bool IsRunning { get; private set; } = isRunning;

        public int StartCalls { get; private set; }

        public int StopCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            StartCalls++;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            StopCalls++;
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task ReanalyzeCaseAsync(
            long caseId,
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

        public int SetStringsCalls { get; private set; }

        public string LocalFolderPath => string.Empty;

        public string? GetString(string key) => _values.GetValueOrDefault(key);

        public void SetString(string key, string value)
        {
            _values[key] = value;
        }

        public void SetStrings(IReadOnlyDictionary<string, string> values)
        {
            SetStringsCalls++;
            foreach (var (key, value) in values)
            {
                _values[key] = value;
            }
        }
    }
}
