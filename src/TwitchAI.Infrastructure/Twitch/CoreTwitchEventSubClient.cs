using System.Runtime.CompilerServices;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Infrastructure.Twitch;

public sealed class CoreTwitchEventSubClient : ITwitchEventSubClient
{
    private readonly ITwitchEventSubWebSocketClient _client;
    private readonly HelixChannelContextService _channelContextService;
    private readonly IMessageAvailabilityStore _availabilityStore;
    private readonly IEventSubDedupeStore _dedupeStore;
    private readonly TwitchApiOptions _options;

    public CoreTwitchEventSubClient(
        ITwitchEventSubWebSocketClient client,
        HelixChannelContextService channelContextService,
        IMessageAvailabilityStore availabilityStore,
        IEventSubDedupeStore dedupeStore,
        InfrastructureOptions options)
    {
        _client = client;
        _channelContextService = channelContextService;
        _availabilityStore = availabilityStore;
        _dedupeStore = dedupeStore;
        _options = options.TwitchApi;
    }

    public async IAsyncEnumerable<TwitchChatEvent> ReadEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.BroadcasterId) ||
            string.IsNullOrWhiteSpace(_options.ReaderUserId))
        {
            throw new InvalidOperationException(
                "TwitchApi BroadcasterId and ReaderUserId must be configured before EventSub starts.");
        }

        var registration = new TwitchEventSubChannel(
            _options.BroadcasterId,
            _options.ReaderUserId);
        await _availabilityStore.DeleteUnlinkedOlderThanAsync(
            DateTimeOffset.UtcNow - TimeSpan.FromHours(24),
            cancellationToken).ConfigureAwait(false);

        await foreach (var item in _client
                           .ReadAllAsync(registration, cancellationToken)
                           .ConfigureAwait(false))
        {
            var hasPersistentDedupeMarker = HasPersistentDedupeMarker(item);
            var downstreamAccepted = false;
            try
            {
                TwitchChatEvent? outgoing = null;
                switch (item)
                {
                    case TwitchEventSubBarrierEvent barrier:
                        barrier.Completion.TrySetResult();
                        break;

                    case TwitchChatMessageEvent message:
                    {
                        var badges = PreservePrivilegeBadge(message);
                        var identity = ChatMessageIdentityV1.Create(
                            message.EventMessageId,
                            message.MessageId,
                            message.BroadcasterId,
                            message.ChatterUserId,
                            message.ChatterLogin,
                            message.ChatterDisplayName,
                            message.MessageText,
                            message.ObservedAt,
                            message.SourceBroadcasterId,
                            badges);
                        outgoing = new TwitchChatEvent(
                            TwitchChatEventKind.Message,
                            message.EventMessageId,
                            message.ObservedAt,
                            identity,
                            message.BroadcasterId,
                            null,
                            null,
                            null);
                        break;
                    }

                    case TwitchCategoryChangedEvent category:
                    {
                        var context = await _channelContextService.ObserveCategoryAsync(
                            category.BroadcasterId,
                            category.CategoryId,
                            category.CategoryName,
                            category.ObservedAt,
                            cancellationToken).ConfigureAwait(false);
                        outgoing = new TwitchChatEvent(
                            TwitchChatEventKind.ChannelUpdated,
                            category.EventMessageId,
                            category.ObservedAt,
                            null,
                            category.BroadcasterId,
                            null,
                            null,
                            context);
                        break;
                    }

                    case TwitchMessageDeletedEvent deleted:
                        await _availabilityStore.RecordDeletedAsync(deleted, cancellationToken)
                            .ConfigureAwait(false);
                        outgoing = new TwitchChatEvent(
                            TwitchChatEventKind.MessageDeleted,
                            deleted.EventMessageId,
                            deleted.ObservedAt,
                            null,
                            deleted.BroadcasterId,
                            deleted.MessageId,
                            deleted.TargetUserId,
                            null);
                        break;

                    case TwitchUserMessagesClearedEvent userCleared:
                        await _availabilityStore.RecordUserClearAsync(
                                userCleared,
                                cancellationToken)
                            .ConfigureAwait(false);
                        outgoing = new TwitchChatEvent(
                            TwitchChatEventKind.UserMessagesCleared,
                            userCleared.EventMessageId,
                            userCleared.ObservedAt,
                            null,
                            userCleared.BroadcasterId,
                            null,
                            userCleared.TargetUserId,
                            null);
                        break;

                    case TwitchRoomClearedEvent roomCleared:
                        await _availabilityStore.RecordRoomClearAsync(
                                roomCleared,
                                cancellationToken)
                            .ConfigureAwait(false);
                        outgoing = new TwitchChatEvent(
                            TwitchChatEventKind.RoomCleared,
                            roomCleared.EventMessageId,
                            roomCleared.ObservedAt,
                            null,
                            roomCleared.BroadcasterId,
                            null,
                            null,
                            null);
                        break;

                    case TwitchEventSubRevokedEvent revoked:
                        await _availabilityStore.RecordGapAsync(
                            new TwitchEventSubGapEvent(
                                revoked.EventMessageId,
                                revoked.BroadcasterId,
                                revoked.ObservedAt,
                                revoked.ObservedAt,
                                $"subscription_revoked:{revoked.Status}"),
                            cancellationToken).ConfigureAwait(false);
                        outgoing = new TwitchChatEvent(
                            TwitchChatEventKind.EventGap,
                            revoked.EventMessageId,
                            revoked.ObservedAt,
                            null,
                            revoked.BroadcasterId,
                            null,
                            null,
                            null);
                        break;

                    case TwitchEventSubGapEvent gap:
                        await _availabilityStore.RecordGapAsync(gap, cancellationToken)
                            .ConfigureAwait(false);
                        outgoing = new TwitchChatEvent(
                            TwitchChatEventKind.EventGap,
                            gap.EventMessageId,
                            gap.ThroughReceivedAt,
                            null,
                            gap.BroadcasterId,
                            null,
                            null,
                            null);
                        break;
                }

                if (outgoing is null)
                {
                    downstreamAccepted = true;
                    continue;
                }

                yield return outgoing;
                downstreamAccepted = true;
            }
            finally
            {
                if (hasPersistentDedupeMarker && !downstreamAccepted)
                {
                    await _dedupeStore.ReleaseProcessedAsync(
                        item.EventMessageId,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, string> PreservePrivilegeBadge(
        TwitchChatMessageEvent message)
    {
        if (!message.IsModerator
            || message.Badges.Keys.Any(
                key => string.Equals(
                        key,
                        "moderator",
                        StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        key,
                        "broadcaster",
                        StringComparison.OrdinalIgnoreCase)))
        {
            return message.Badges;
        }

        var badges = new Dictionary<string, string>(
            message.Badges,
            StringComparer.OrdinalIgnoreCase);
        var isBroadcaster =
            string.Equals(
                message.ChatterUserId,
                message.BroadcasterId,
                StringComparison.Ordinal)
            || string.Equals(
                message.ChatterUserId,
                message.SourceBroadcasterId,
                StringComparison.Ordinal);
        badges[isBroadcaster ? "broadcaster" : "moderator"] = "1";
        return badges;
    }

    private static bool HasPersistentDedupeMarker(TwitchEventSubEvent item)
    {
        return item switch
        {
            TwitchCategoryChangedEvent category => !category.IsHelixReconciliation,
            TwitchChatMessageEvent => true,
            TwitchMessageDeletedEvent => true,
            TwitchUserMessagesClearedEvent => true,
            TwitchRoomClearedEvent => true,
            TwitchEventSubRevokedEvent => true,
            _ => false,
        };
    }
}
