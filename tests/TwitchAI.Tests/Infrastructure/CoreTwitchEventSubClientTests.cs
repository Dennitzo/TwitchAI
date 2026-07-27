using System.Runtime.CompilerServices;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.Tests.Infrastructure;

public sealed class CoreTwitchEventSubClientTests
{
    [Fact]
    public async Task ReadEventsAsync_PreservesModeratorRole_WhenBadgesAreMissing()
    {
        var client = CreateClient(
            new TwitchChatMessageEvent(
                "eventsub-message-1",
                "channel-1",
                DateTimeOffset.UtcNow,
                "message-1",
                "moderator-2",
                "moderator",
                "Moderator",
                "This message must bypass moderation.",
                IsModerator: true,
                new Dictionary<string, string>(),
                SourceBroadcasterId: null,
                SourceMessageId: null),
            new ThrowingAvailabilityStore(AvailabilityFailure.None),
            new RecordingDedupeStore(),
            new ThrowingChannelStateRepository());

        var chatEvent = await ReadSingleAsync(client);

        Assert.NotNull(chatEvent.Message);
        Assert.True(chatEvent.Message.Badges.ContainsKey("moderator"));
        Assert.True(
            TwitchAI.Core.Services.ChatMessageIdentityFingerprint.IsValid(
                chatEvent.Message));
    }

    [Fact]
    public async Task ReadEventsAsync_ReleasesDeleteMarker_WhenAvailabilityPersistenceFails()
    {
        const string eventMessageId = "eventsub-delete-1";
        var dedupe = new RecordingDedupeStore();
        var client = CreateClient(
            new TwitchMessageDeletedEvent(
                eventMessageId,
                "channel-1",
                DateTimeOffset.UtcNow,
                "message-1",
                "user-1",
                "viewer",
                "Viewer"),
            new ThrowingAvailabilityStore(AvailabilityFailure.Delete),
            dedupe,
            new ThrowingChannelStateRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await DrainAsync(client));

        Assert.Equal([eventMessageId], dedupe.ReleasedEventMessageIds);
    }

    [Fact]
    public async Task ReadEventsAsync_ReleasesCategoryMarker_WhenCategoryPersistenceFails()
    {
        const string eventMessageId = "eventsub-category-1";
        var dedupe = new RecordingDedupeStore();
        var client = CreateClient(
            new TwitchCategoryChangedEvent(
                eventMessageId,
                "channel-1",
                DateTimeOffset.UtcNow,
                "32982",
                "Grand Theft Auto V",
                IsHelixReconciliation: false),
            new ThrowingAvailabilityStore(AvailabilityFailure.None),
            dedupe,
            new ThrowingChannelStateRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await DrainAsync(client));

        Assert.Equal([eventMessageId], dedupe.ReleasedEventMessageIds);
    }

    [Fact]
    public async Task ReadEventsAsync_DoesNotReleaseSyntheticGapMarker_WhenPersistenceFails()
    {
        var dedupe = new RecordingDedupeStore();
        var client = CreateClient(
            new TwitchEventSubGapEvent(
                "local-eventsub-gap-fixed",
                "channel-1",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                "unexpected_disconnect"),
            new ThrowingAvailabilityStore(AvailabilityFailure.Gap),
            dedupe,
            new ThrowingChannelStateRepository());

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await DrainAsync(client));

        Assert.Empty(dedupe.ReleasedEventMessageIds);
    }

    private static CoreTwitchEventSubClient CreateClient(
        TwitchEventSubEvent eventSubEvent,
        IMessageAvailabilityStore availabilityStore,
        IEventSubDedupeStore dedupeStore,
        IChannelStateRepository channelStateRepository)
    {
        var options = new InfrastructureOptions
        {
            TwitchApi = new TwitchApiOptions
            {
                BroadcasterId = "channel-1",
                ReaderUserId = "reader-1",
                ModeratorUserId = "moderator-1",
            },
        };
        var contextService = new HelixChannelContextService(
            channelStateRepository,
            new StubHelixClient(),
            options);
        return new CoreTwitchEventSubClient(
            new SingleEventWebSocketClient(eventSubEvent),
            contextService,
            availabilityStore,
            dedupeStore,
            options);
    }

    private static async Task DrainAsync(CoreTwitchEventSubClient client)
    {
        await foreach (var _ in client.ReadEventsAsync(CancellationToken.None))
        {
        }
    }

    private static async Task<TwitchChatEvent> ReadSingleAsync(
        CoreTwitchEventSubClient client)
    {
        var events = new List<TwitchChatEvent>();
        await foreach (var chatEvent in client.ReadEventsAsync(CancellationToken.None))
        {
            events.Add(chatEvent);
        }

        return Assert.Single(events);
    }

    private sealed class SingleEventWebSocketClient(TwitchEventSubEvent eventSubEvent)
        : ITwitchEventSubWebSocketClient
    {
        public async IAsyncEnumerable<TwitchEventSubEvent> ReadAllAsync(
            TwitchEventSubChannel channel,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Assert.Equal("channel-1", channel.BroadcasterId);
            Assert.Equal("reader-1", channel.ReaderUserId);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return eventSubEvent;
        }
    }

    private sealed class RecordingDedupeStore : IEventSubDedupeStore
    {
        public List<string> ReleasedEventMessageIds { get; } = [];

        public Task<bool> TryMarkProcessedAsync(
            string eventMessageId,
            string subscriptionType,
            DateTimeOffset receivedAt,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<int> DeleteOlderThanAsync(
            DateTimeOffset cutoff,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }

        public Task ReleaseProcessedAsync(
            string eventMessageId,
            CancellationToken cancellationToken = default)
        {
            ReleasedEventMessageIds.Add(eventMessageId);
            return Task.CompletedTask;
        }
    }

    private enum AvailabilityFailure
    {
        None,
        Delete,
        Gap,
    }

    private sealed class ThrowingAvailabilityStore(AvailabilityFailure failure)
        : IMessageAvailabilityStore
    {
        public Task RecordDeletedAsync(
            TwitchMessageDeletedEvent deleted,
            CancellationToken cancellationToken = default)
        {
            return failure == AvailabilityFailure.Delete
                ? Task.FromException(new InvalidOperationException("delete persistence failed"))
                : Task.CompletedTask;
        }

        public Task RecordUserClearAsync(
            TwitchUserMessagesClearedEvent cleared,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RecordRoomClearAsync(
            TwitchRoomClearedEvent cleared,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RecordGapAsync(
            TwitchEventSubGapEvent gap,
            CancellationToken cancellationToken = default)
        {
            return failure == AvailabilityFailure.Gap
                ? Task.FromException(new InvalidOperationException("gap persistence failed"))
                : Task.CompletedTask;
        }

        public Task<MessageAvailabilityAssessment> AssessAsync(
            string channelId,
            string twitchMessageId,
            string twitchUserId,
            DateTimeOffset messageReceivedAt,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new MessageAvailabilityAssessment(
                    MessageAvailability.ObservedActive,
                    DateTimeOffset.UtcNow,
                    "observed_active",
                    Revision: 1));
        }

        public Task<bool> AcceptExternalDeleteFeedbackAsync(
            string eventSubMessageId,
            long caseId,
            string triggerPhrase,
            PolicySetCode policySet,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(false);
        }

        public Task<int> DeleteUnlinkedOlderThanAsync(
            DateTimeOffset cutoff,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(0);
        }
    }

    private sealed class ThrowingChannelStateRepository : IChannelStateRepository
    {
        private static readonly ChannelStateRecord Existing = new(
            "channel-1",
            "channel",
            "Channel",
            "moderator-1",
            "509658",
            "Just Chatting",
            DateTimeOffset.UtcNow,
            Revision: 1,
            IsVerified: true);

        public Task UpsertAsync(
            ChannelStateRecord state,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException(
                new InvalidOperationException("category persistence failed"));
        }

        public Task<ChannelStateRecord?> GetAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<ChannelStateRecord?>(Existing);
        }
    }

    private sealed class StubHelixClient : ITwitchHelixClient
    {
        public Task<TwitchUserInfo> GetCurrentUserAsync(
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                new TwitchUserInfo("moderator-1", "moderator", "Moderator"));
        }

        public Task<TwitchChannelInfo?> GetChannelAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TwitchChannelInfo?>(null);
        }

        public Task<IReadOnlyList<TwitchGameInfo>> GetGamesAsync(
            IReadOnlyCollection<string> categoryIds,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TwitchGameInfo>>([]);
        }

        public Task<TwitchGameInfo?> GetGameByNameAsync(
            string categoryName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TwitchGameInfo?>(null);
        }

        public Task<IReadOnlyList<TwitchModeratedChannel>> GetModeratedChannelsAsync(
            string userId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<TwitchModeratedChannel>>([]);
        }

        public Task<TwitchBannedUserInfo?> GetBannedUserAsync(
            string broadcasterId,
            string userId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<TwitchBannedUserInfo?>(null);
        }

        public Task DeleteMessageAsync(
            string broadcasterId,
            string moderatorId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task TimeoutUserAsync(
            string broadcasterId,
            string moderatorId,
            string userId,
            int durationSeconds,
            string reason,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task CreateEventSubSubscriptionAsync(
            TwitchEventSubSubscriptionRequest subscription,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
