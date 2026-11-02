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
    public async Task Channels_AreNormalizedWhenLoadedAndSaved()
    {
        var options = new InfrastructureOptions();
        var storage = new MemoryLocalAppStorage(new Dictionary<string, string>
        {
            ["ChannelLogin"] = " ChannelOne, #CHANNELTWO, channelone ",
            ["LocalAiModel"] = "openai/gpt-oss-20b",
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
            storage,
            new ModerationQueueRuntimeSettings(),
            new TwitchRuntimeModeSettings(),
            null!,
            null!);

        Assert.Equal("channelone, channeltwo", viewModel.ChannelLogin);

        viewModel.ChannelLogin = " ChannelThree, @CHANNELFOUR, channelthree ";
        await viewModel.SaveAsync();

        Assert.Equal("channelthree, channelfour", viewModel.ChannelLogin);
        Assert.Equal(
            "channelthree, channelfour",
            storage.GetString("ChannelLogin"));
    }

    [Fact]
    public async Task LocalAiModel_WhenSelected_IsStagedUntilSaved()
    {
        var options = new InfrastructureOptions();
        var storage = new MemoryLocalAppStorage(new Dictionary<string, string>
        {
            ["LocalAiModel"] = "openai/gpt-oss-20b",
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
            storage,
            new ModerationQueueRuntimeSettings(),
            new TwitchRuntimeModeSettings(),
            null!,
            null!);

        viewModel.LocalAiModel = "openai/gpt-oss-120b";

        // Die Auswahl wird gestuft: Modell und Speicher bleiben unverändert.
        Assert.NotEqual("openai/gpt-oss-120b", options.LocalAi.Model);
        Assert.Equal(
            "openai/gpt-oss-20b",
            storage.GetString("LocalAiModel"));
        Assert.Equal(
            "Lokale-AI-Modell geändert · Speichern übernimmt die Auswahl",
            viewModel.SaveStatus);

        await viewModel.SaveAsync();

        Assert.Equal("openai/gpt-oss-120b", options.LocalAi.Model);
        Assert.Equal(
            "openai/gpt-oss-120b",
            storage.GetString("LocalAiModel"));
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

    [Fact]
    public async Task Save_BackseatgamingToggle_ReevaluatesPolicyOnEverySaveWhilePaused()
    {
        await using var harness = await SettingsSaveHarness.CreateAsync(isRunning: false);
        var resolver = new EffectivePolicyResolver(runtimeSettings: harness.PolicyRuntime);
        var channelContext = new TwitchAI.Core.Models.ChannelContextSnapshot(
            "channel-1", "game-1", "Game one", DateTimeOffset.UtcNow, 1, IsVerified: true);

        foreach (var enabled in new[] { true, false, true })
        {
            harness.ViewModel.BackseatgamingEnabled = enabled;

            await harness.ViewModel.SaveAsync();

            Assert.Equal(enabled, harness.PolicyRuntime.BackseatgamingEnabled);
            Assert.Equal(enabled.ToString(), harness.Storage.GetString("BackseatgamingEnabled"));
            Assert.Equal(
                enabled ? "General + Backseatgaming" : "Nur General",
                harness.State.ActivePolicies);
            Assert.Equal(
                enabled,
                resolver.Resolve(channelContext, 1, 1)
                    .Contains(TwitchAI.Core.Models.PolicySetCode.Backseatgaming));
            Assert.False(harness.ViewModel.IsBusy);
        }

        Assert.Equal(0, harness.Coordinator.StopCalls);
        Assert.Equal(0, harness.Coordinator.StartCalls);
        Assert.False(harness.State.IsAnalysisRunning);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_StopsOldWorkersBeforeCommittingAndResumesOnlyRunningAnalysis(bool isRunning)
    {
        await using var harness = await SettingsSaveHarness.CreateAsync(isRunning);
        harness.ViewModel.LocalAiModel = "coding/new-model";
        harness.ViewModel.ReasoningEffort = "low";
        harness.ViewModel.BackseatgamingEnabled = false;
        harness.ViewModel.RemoveIdenticalAiQueueMessages = false;
        var order = new List<string>();
        harness.Coordinator.OnStop = () =>
        {
            order.Add("stop");
            Assert.Equal("coding/old-model", harness.Options.LocalAi.Model);
            Assert.Equal("auto", harness.Options.LocalAi.ReasoningEffort);
            Assert.True(harness.PolicyRuntime.BackseatgamingEnabled);
            Assert.True(harness.QueueRuntime.RemoveIdenticalMessages);
            Assert.Equal("coding/old-model", harness.Storage.GetString("LocalAiModel"));
        };
        harness.Coordinator.OnStart = () =>
        {
            order.Add("start");
            Assert.Equal("coding/new-model", harness.Options.LocalAi.Model);
            Assert.Equal("low", harness.Options.LocalAi.ReasoningEffort);
            Assert.False(harness.PolicyRuntime.BackseatgamingEnabled);
            Assert.False(harness.QueueRuntime.RemoveIdenticalMessages);
            Assert.Equal("coding/new-model", harness.Storage.GetString("LocalAiModel"));
            Assert.True(harness.State.IsTwitchConnectionReady);
            Assert.Equal("Nur General", harness.State.ActivePolicies);
        };

        await harness.ViewModel.SaveAsync();

        string[] expectedOrder = isRunning ? ["stop", "start"] : [];
        Assert.Equal(expectedOrder, order);
        Assert.Equal(isRunning ? 1 : 0, harness.Coordinator.StopCalls);
        Assert.Equal(isRunning ? 1 : 0, harness.Coordinator.StartCalls);
        Assert.Equal(isRunning, harness.Coordinator.IsRunning);
        Assert.Equal(isRunning, harness.State.IsAnalysisRunning);
        Assert.Equal("coding/new-model", harness.Options.LocalAi.Model);
        Assert.Equal("low", harness.Options.LocalAi.ReasoningEffort);
        Assert.False(harness.PolicyRuntime.BackseatgamingEnabled);
        Assert.False(harness.QueueRuntime.RemoveIdenticalMessages);
        Assert.Contains("neu initialisiert", harness.ViewModel.SaveStatus);
    }

    [Fact]
    public async Task Save_WhenChannelPreparationFails_LeavesRunningAnalysisAndCommittedSettingsUntouched()
    {
        await using var harness = await SettingsSaveHarness.CreateAsync(
            isRunning: true,
            helixClient: new IncompleteMultiChannelHelixClient());
        var storedBefore = harness.Storage.Snapshot();
        harness.ViewModel.ChannelLogin = "newone, newtwo";
        harness.ViewModel.LocalAiModel = "coding/uncommitted-model";
        harness.ViewModel.ReasoningEffort = "low";
        harness.ViewModel.BackseatgamingEnabled = false;
        harness.ViewModel.RemoveIdenticalAiQueueMessages = false;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.ViewModel.SaveAsync());

        Assert.Contains("newtwo", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("public-client-id", harness.Options.TwitchOAuth.ClientId);
        Assert.Equal("coding/old-model", harness.Options.LocalAi.Model);
        Assert.Equal("auto", harness.Options.LocalAi.ReasoningEffort);
        Assert.Equal(["old-channel"], harness.Options.TwitchApi.GetBroadcasterIds());
        Assert.Equal("old-reader", harness.Options.TwitchApi.ReaderUserId);
        Assert.Equal("old-moderator", harness.Options.TwitchApi.ModeratorUserId);
        Assert.True(harness.PolicyRuntime.BackseatgamingEnabled);
        Assert.True(harness.QueueRuntime.RemoveIdenticalMessages);
        Assert.False(harness.TwitchRuntime.IsReadOnly);
        Assert.Equal(storedBefore, harness.Storage.Snapshot());
        Assert.Equal(0, harness.Storage.SetStringsCalls);
        Assert.Empty(harness.ChannelStates.Upserts);
        Assert.True(harness.Coordinator.IsRunning);
        Assert.True(harness.State.IsAnalysisRunning);
        Assert.True(harness.State.IsTwitchConnectionReady);
        Assert.Equal("Old channel", harness.State.ChannelDisplayName);
        Assert.Equal("Old category", harness.State.CategoryName);
        Assert.Equal("Nur General", harness.State.ActivePolicies);
        Assert.Equal(0, harness.Coordinator.StopCalls);
        Assert.Equal(0, harness.Coordinator.StartCalls);
        Assert.False(harness.ViewModel.IsBusy);
        Assert.Contains("nicht übernommen", harness.ViewModel.SaveStatus);
    }

    [Fact]
    public async Task Save_Offline_AppliesAllLocalSettingsImmediatelyWithoutRestartHint()
    {
        var options = new InfrastructureOptions
        {
            TwitchOAuth = new TwitchOAuthOptions { ClientId = "public-client-id" },
        };
        options.LocalAi.Model = "coding/old-model";
        var state = new AppState();
        var storage = new MemoryLocalAppStorage(new Dictionary<string, string>
        {
            ["TwitchClientId"] = "public-client-id",
            ["LocalAiModel"] = "coding/old-model",
        });
        var tokenProvider = new ConfigurationFailureTokenProvider();
        var coordinator = new TrackingCoordinator(isRunning: false);
        var policyRuntime = new ModerationPolicyRuntimeSettings();
        var queueRuntime = new ModerationQueueRuntimeSettings();
        var twitchRuntime = new TwitchRuntimeModeSettings();
        using var viewModel = new SettingsViewModel(
            state, options, null!, tokenProvider, null!, new TwitchConnectionReadiness(),
            null!, null!, null!, null!, storage, queueRuntime, twitchRuntime,
            coordinator, null!, policyRuntime)
        {
            ChannelLogin = " #NewChannel ",
            LocalAiModel = "coding/new-model",
            ReasoningEffort = "low",
            BackseatgamingEnabled = false,
            RemoveIdenticalAiQueueMessages = false,
            ReadOnlyMode = true,
            SelectedTheme = "Hell",
        };

        await viewModel.SaveAsync();

        Assert.Equal("coding/new-model", options.LocalAi.Model);
        Assert.Equal("low", options.LocalAi.ReasoningEffort);
        Assert.False(policyRuntime.BackseatgamingEnabled);
        Assert.False(queueRuntime.RemoveIdenticalMessages);
        Assert.True(twitchRuntime.IsReadOnly);
        Assert.True(state.IsReadOnlyMode);
        Assert.False(state.IsTwitchConnectionReady);
        Assert.False(state.IsAnalysisRunning);
        Assert.Equal("newchannel", storage.GetString("ChannelLogin"));
        Assert.Equal("coding/new-model", storage.GetString("LocalAiModel"));
        Assert.Equal("low", storage.GetString("ReasoningEffort"));
        Assert.Equal("Hell", storage.GetString("Theme"));
        Assert.Equal("False", storage.GetString("BackseatgamingEnabled"));
        Assert.Equal("False", storage.GetString("RemoveIdenticalAiQueueMessages"));
        Assert.Equal("True", storage.GetString("ReadOnlyMode"));
        Assert.Equal(1, tokenProvider.ValidateCalls);
        Assert.Equal(0, coordinator.StopCalls);
        Assert.Equal(0, coordinator.StartCalls);
        Assert.False(viewModel.IsBusy);
        Assert.Contains("gespeichert und angewandt", viewModel.SaveStatus);
        Assert.DoesNotContain("Neustart", viewModel.SaveStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_PersistsFreshVerifiedChannelContextsBeforeRestartingAnalysis()
    {
        await using var harness = await SettingsSaveHarness.CreateAsync(isRunning: true);
        harness.ViewModel.ChannelLogin = "channelone, channeltwo";
        var observedAfter = DateTimeOffset.UtcNow;
        harness.Coordinator.OnStart = () =>
        {
            Assert.Equal(2, harness.ChannelStates.Upserts.Count);
            Assert.Equal(["channel-1", "channel-2"], harness.Options.TwitchApi.GetBroadcasterIds());
            Assert.Equal("General + Backseatgaming", harness.State.ActivePolicies);
        };

        await harness.ViewModel.SaveAsync();

        Assert.Collection(
            harness.ChannelStates.Upserts,
            first => AssertFreshChannel(first, "channel-1", "channelone", "Channel one", "game-1", "Game one", observedAfter),
            second => AssertFreshChannel(second, "channel-2", "channeltwo", "Channel two", "game-2", "Game two", observedAfter));
        Assert.Equal(1, harness.Coordinator.StartCalls);
        Assert.Equal(1, harness.Coordinator.StopCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_WhilePreparing_BlocksShellAnalysisToggle(bool isRunning)
    {
        await using var harness = await SettingsSaveHarness.CreateAsync(isRunning);
        var shell = new ShellViewModel(harness.State, harness.Coordinator);
        var preparing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.TokenProvider.BeforeValidate = async cancellationToken =>
        {
            preparing.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
        };

        var save = harness.ViewModel.SaveAsync();
        await preparing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.True(harness.State.IsApplyingSettings);
            Assert.False(shell.CanToggleAnalysis);
            Assert.False(shell.ToggleAnalysisCommand.CanExecute(null));

            // Also protect direct command execution while its UI is disabled.
            await shell.ToggleAnalysisCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(0, harness.Coordinator.StartCalls);
            Assert.Equal(0, harness.Coordinator.StopCalls);
            Assert.Equal(isRunning, harness.Coordinator.IsRunning);
        }
        finally
        {
            release.TrySetResult();
            await save.WaitAsync(TimeSpan.FromSeconds(3));
        }

        Assert.False(harness.State.IsApplyingSettings);
        Assert.True(shell.CanToggleAnalysis);
        Assert.Equal(isRunning ? 1 : 0, harness.Coordinator.StartCalls);
        Assert.Equal(isRunning ? 1 : 0, harness.Coordinator.StopCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Save_WaitsForInFlightShellToggleBeforeDecidingWhetherToResume(bool wasRunning)
    {
        await using var harness = await SettingsSaveHarness.CreateAsync(wasRunning);
        var shell = new ShellViewModel(harness.State, harness.Coordinator);
        var toggling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task DelayFirstToggle(CancellationToken cancellationToken)
        {
            var firstCall = wasRunning
                ? harness.Coordinator.StopCalls == 1
                : harness.Coordinator.StartCalls == 1;
            if (firstCall)
            {
                toggling.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
        }
        if (wasRunning) harness.Coordinator.StopResponse = DelayFirstToggle;
        else harness.Coordinator.StartResponse = DelayFirstToggle;

        var toggle = shell.ToggleAnalysisCommand.ExecuteAsync(null);
        await toggling.Task.WaitAsync(TimeSpan.FromSeconds(3));
        harness.ViewModel.LocalAiModel = "coding/new-model";
        harness.ViewModel.BackseatgamingEnabled = false;
        var save = harness.ViewModel.SaveAsync();
        try
        {
            Assert.True(harness.State.IsApplyingSettings);
            Assert.False(save.IsCompleted);
            Assert.Equal(0, harness.TokenProvider.ValidateCalls);
            Assert.Equal("coding/old-model", harness.Options.LocalAi.Model);
            Assert.True(harness.PolicyRuntime.BackseatgamingEnabled);
            Assert.Equal(0, harness.Storage.SetStringsCalls);
        }
        finally
        {
            release.TrySetResult();
            await toggle.WaitAsync(TimeSpan.FromSeconds(3));
            await save.WaitAsync(TimeSpan.FromSeconds(3));
        }

        Assert.Equal(!wasRunning, harness.Coordinator.IsRunning);
        Assert.Equal(!wasRunning, harness.State.IsAnalysisRunning);
        Assert.Equal(wasRunning ? 0 : 2, harness.Coordinator.StartCalls);
        Assert.Equal(1, harness.Coordinator.StopCalls);
        Assert.Equal("coding/new-model", harness.Options.LocalAi.Model);
        Assert.False(harness.PolicyRuntime.BackseatgamingEnabled);
        Assert.False(harness.State.IsApplyingSettings);
    }

    private static void AssertFreshChannel(
        ChannelStateRecord state,
        string broadcasterId,
        string login,
        string displayName,
        string categoryId,
        string categoryName,
        DateTimeOffset observedAfter)
    {
        Assert.Equal(broadcasterId, state.BroadcasterId);
        Assert.Equal(login, state.BroadcasterLogin);
        Assert.Equal(displayName, state.BroadcasterName);
        Assert.Equal(categoryId, state.CategoryId);
        Assert.Equal(categoryName, state.CategoryName);
        Assert.Equal("reader", state.ModeratorUserId);
        Assert.True(state.IsVerified);
        Assert.InRange(state.ObservedAt, observedAfter, DateTimeOffset.UtcNow);
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
        public int ValidateCalls { get; private set; }

        public Func<CancellationToken, Task>? BeforeValidate { get; set; }

        public Task<bool> HasStoredAuthorizationAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<string> GetAccessTokenAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult("token");

        public Task<string> RefreshAccessTokenAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult("token");

        public async Task<TwitchTokenValidation> ValidateStoredAuthorizationAsync(
            CancellationToken cancellationToken = default)
        {
            ValidateCalls++;
            if (BeforeValidate is not null) await BeforeValidate(cancellationToken);
            return new TwitchTokenValidation(clientId, "account", "reader", scopes, 3600);
        }

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

    private sealed class RegisteredCategoryPolicyStore(
        SqliteConnectionFactory connectionFactory) : IPolicyStore
    {
        public Task EnsureBroadcasterSeededAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<EffectivePolicySnapshot> GetEffectivePolicyAsync(
            string broadcasterId,
            string? categoryId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM twitch_categories WHERE category_id = @category_id;";
            command.Parameters.AddWithValue("@category_id", categoryId ?? string.Empty);
            var categoryKnown = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture) > 0;
            return new EffectivePolicySnapshot(
                broadcasterId, categoryId, 1, categoryKnown ? 1 : 0,
                $"policy-{broadcasterId}", string.Empty, [], []);
        }
    }

    private sealed class RecordingChannelStateRepository : IChannelStateRepository
    {
        public List<ChannelStateRecord> Upserts { get; } = [];

        public Task UpsertAsync(ChannelStateRecord state, CancellationToken cancellationToken = default)
        {
            Upserts.Add(state);
            return Task.CompletedTask;
        }

        public Task<ChannelStateRecord?> GetAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Upserts.LastOrDefault(state => state.BroadcasterId == broadcasterId));
    }

    private sealed class SettingsSaveHarness : IAsyncDisposable
    {
        private readonly string _databaseDirectory = Path.Combine(
            Path.GetTempPath(), "TwitchAI.App.Tests", Guid.NewGuid().ToString("N"));
        private readonly SqliteWriteCoordinator _writeCoordinator = new();
        private readonly SqliteConnectionFactory _connectionFactory;

        private SettingsSaveHarness(bool isRunning, ITwitchHelixClient? helixClient)
        {
            Directory.CreateDirectory(_databaseDirectory);
            Options = new InfrastructureOptions
            {
                DatabasePath = Path.Combine(_databaseDirectory, "settings.db"),
                TwitchOAuth = new TwitchOAuthOptions { ClientId = "public-client-id" },
            };
            Options.LocalAi.Model = "coding/old-model";
            Options.TwitchApi.SetBroadcasterIds(["old-channel"]);
            Options.TwitchApi.ReaderUserId = "old-reader";
            Options.TwitchApi.ModeratorUserId = "old-moderator";
            _connectionFactory = new SqliteConnectionFactory(Options);
            Storage = new MemoryLocalAppStorage(new Dictionary<string, string>
            {
                ["TwitchClientId"] = "public-client-id",
                ["ChannelLogin"] = "channelone",
                ["LocalAiModel"] = "coding/old-model",
                ["ReasoningEffort"] = "auto",
                ["BroadcasterIds"] = "old-channel",
                ["ReaderUserId"] = "old-reader",
                ["ModeratorUserId"] = "old-moderator",
                ["BackseatgamingEnabled"] = "True",
                ["RemoveIdenticalAiQueueMessages"] = "True",
                ["ReadOnlyMode"] = "False",
            });
            State = new AppState
            {
                IsAnalysisRunning = isRunning,
                IsTwitchConnectionReady = true,
                ChannelDisplayName = "Old channel",
                CategoryName = "Old category",
                ActivePolicies = "Nur General",
            };
            Coordinator = new TrackingCoordinator(isRunning);
            TokenProvider = new ValidTokenProvider("public-client-id", Options.TwitchOAuth.Scopes);
            ViewModel = new SettingsViewModel(
                State, Options, null!, TokenProvider,
                helixClient ?? new CompleteAsynchronousMultiChannelHelixClient(),
                new TwitchConnectionReadiness(), null!, _connectionFactory, _writeCoordinator,
                new RegisteredCategoryPolicyStore(_connectionFactory), Storage, QueueRuntime,
                TwitchRuntime, Coordinator, null!, PolicyRuntime, ChannelStates);
        }

        public InfrastructureOptions Options { get; }

        public AppState State { get; }

        public MemoryLocalAppStorage Storage { get; }

        public SettingsViewModel ViewModel { get; }

        public TrackingCoordinator Coordinator { get; }

        public ValidTokenProvider TokenProvider { get; }

        public ModerationPolicyRuntimeSettings PolicyRuntime { get; } = new();

        public ModerationQueueRuntimeSettings QueueRuntime { get; } = new();

        public TwitchRuntimeModeSettings TwitchRuntime { get; } = new();

        public RecordingChannelStateRepository ChannelStates { get; } = new();

        public static async Task<SettingsSaveHarness> CreateAsync(
            bool isRunning,
            ITwitchHelixClient? helixClient = null)
        {
            var harness = new SettingsSaveHarness(isRunning, helixClient);
            await CreateCategoryTableAsync(harness._connectionFactory);
            return harness;
        }

        public ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            _writeCoordinator.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(_databaseDirectory, recursive: true);
            return ValueTask.CompletedTask;
        }
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

        public Action? OnStart { get; set; }

        public Action? OnStop { get; set; }

        public Func<CancellationToken, Task>? StartResponse { get; set; }

        public Func<CancellationToken, Task>? StopResponse { get; set; }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            OnStart?.Invoke();
            StartCalls++;
            if (StartResponse is not null) await StartResponse(cancellationToken);
            IsRunning = true;
        }

        public async Task StopAsync(CancellationToken cancellationToken = default)
        {
            OnStop?.Invoke();
            StopCalls++;
            if (StopResponse is not null) await StopResponse(cancellationToken);
            IsRunning = false;
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

        public KeyValuePair<string, string>[] Snapshot() =>
            _values.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();

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
