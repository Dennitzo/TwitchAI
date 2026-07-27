using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Authentication;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Twitch;
using CoreContracts = TwitchAI.Core.Contracts;

namespace TwitchAI.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTwitchAIInfrastructure(
        this IServiceCollection services,
        Action<InfrastructureOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new InfrastructureOptions();
        configure?.Invoke(options);
        Validate(options);
        services.AddSingleton(options);

        services.AddSingleton<ISqliteWriteCoordinator>(
            SqliteWriteCoordinator.ProcessWide);
        services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<IHostedService, DatabaseInitializationHostedService>();
        services.AddSingleton<IChatTextNormalizer, ChatTextNormalizer>();
        services.AddSingleton<IWhitelistTextNormalizer, WhitelistTextNormalizer>();
        services.AddSingleton<IChatMessageRepository, ChatMessageRepository>();
        services.AddSingleton<IChannelStateRepository, ChannelStateRepository>();
        services.AddSingleton<IEventSubDedupeStore, EventSubDedupeStore>();
        services.AddSingleton<IMessageAvailabilityStore, SqliteMessageAvailabilityStore>();
        services.AddSingleton<
            IPreviousSessionReviewCleaner,
            PreviousSessionReviewCleaner>();
        services.AddSingleton<IHostedService, AvailabilityRetentionHostedService>();

        services.AddSingleton<SqlitePolicyStore>();
        services.AddSingleton<IPolicyStore>(
            provider => provider.GetRequiredService<SqlitePolicyStore>());
        services.AddSingleton<IPolicyCacheInvalidator>(
            provider => provider.GetRequiredService<SqlitePolicyStore>());

        services.AddSingleton<SqliteWhitelistStore>();
        services.AddSingleton<IWhitelistStore>(
            provider => provider.GetRequiredService<SqliteWhitelistStore>());
        services.AddSingleton<IPolicyCacheInvalidator>(
            provider => provider.GetRequiredService<SqliteWhitelistStore>());

        services.AddSingleton<IDatabaseCatalog, DatabaseCatalog>();
        services.AddSingleton<IDatabaseTableReader, DatabaseTableReader>();
        services.AddSingleton<IDatabaseTableEditor, DatabaseTableEditor>();
        services.AddSingleton<IUniversalDatabaseEditor, UniversalDatabaseEditor>();
        services.AddSingleton<IDatabaseRowDeletionService, DatabaseRowDeletionService>();
        services.AddSingleton<
            CoreContracts.IDatabaseTableCatalog,
            CoreDatabaseTableCatalog>();
        services.AddSingleton<
            CoreContracts.IDatabaseEditorService,
            CoreDatabaseEditorService>();
        services.AddSingleton<
            CoreContracts.IWhitelistPhraseSource,
            SqliteWhitelistPhraseSource>();
        services.AddSingleton<
            CoreContracts.IPhraseRuleSource,
            SqlitePhraseRuleSource>();
        services.AddSingleton<
            CoreContracts.IModerationCaseRepository,
            CoreModerationCaseRepository>();
        services.AddSingleton<
            IModerationReviewFeedbackService,
            SqliteModerationReviewFeedbackService>();

        services.AddSingleton<ITwitchCredentialVault, WindowsPasswordVaultCredentialStore>();
        services.AddSingleton<ITwitchAccessTokenProvider, TwitchAccessTokenProvider>();
        services
            .AddHttpClient<ITwitchOAuthClient, TwitchOAuthClient>(client =>
            {
                client.BaseAddress = options.TwitchOAuth.IdentityBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        services
            .AddHttpClient<ITwitchHelixClient, TwitchHelixClient>(client =>
            {
                client.BaseAddress = options.TwitchApi.HelixBaseAddress;
                client.Timeout = TimeSpan.FromSeconds(30);
            });
        services.AddSingleton<TwitchConnectionReadiness>();
        services.AddSingleton<ITwitchConnectionReadiness>(
            provider => provider.GetRequiredService<TwitchConnectionReadiness>());
        services.AddSingleton<TwitchEventSubWebSocketClient>();
        services.AddSingleton<ITwitchEventSubWebSocketClient>(
            provider => provider.GetRequiredService<TwitchEventSubWebSocketClient>());
        services.AddSingleton<IEventSubProcessingBarrier>(
            provider => provider.GetRequiredService<TwitchEventSubWebSocketClient>());
        services.AddSingleton<HelixChannelContextService>();
        services.AddSingleton<CoreContracts.IChannelContextService>(
            provider => provider.GetRequiredService<HelixChannelContextService>());
        services.AddSingleton<
            CoreContracts.ITwitchEventSubClient,
            CoreTwitchEventSubClient>();
        services.AddSingleton<
            CoreContracts.ITwitchModerationExecutor,
            TwitchModerationExecutor>();

        services.AddSingleton<ILlmRunMetricsStore, SqliteLlmRunMetricsStore>();
        services.AddSingleton<
            ILocalAiRunActivityMonitor,
            LocalAiRunActivityMonitor>();
        services.AddSingleton<
            ILmStudioCredentialVault,
            WindowsLmStudioCredentialVault>();
        services
            .AddHttpClient<ILmStudioClient, LmStudioClient>(client =>
            {
                client.BaseAddress = options.LmStudio.BaseAddress;
                client.Timeout = options.LmStudio.RequestTimeout;
            });
        services.AddSingleton<
            CoreContracts.ILocalModerationClient,
            CoreLocalModerationClient>();

        return services;
    }

    private static void Validate(InfrastructureOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabasePath);
        if (!options.TwitchOAuth.IdentityBaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("Twitch IdentityBaseAddress must be absolute.", nameof(options));
        }

        if (!options.TwitchApi.HelixBaseAddress.IsAbsoluteUri ||
            !options.TwitchApi.EventSubWebSocketUri.IsAbsoluteUri)
        {
            throw new ArgumentException("Twitch API addresses must be absolute.", nameof(options));
        }

        if (!options.LmStudio.BaseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("LM Studio BaseAddress must be absolute.", nameof(options));
        }

        if (options.LmStudio.MaximumOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LmStudio.MaximumOutputTokens,
                "LM Studio MaximumOutputTokens must be positive.");
        }
    }
}
