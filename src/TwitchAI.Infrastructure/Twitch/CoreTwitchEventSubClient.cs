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
        var broadcasterIds = _options.GetBroadcasterIds();
        if (broadcasterIds.Count == 0 || string.IsNullOrWhiteSpace(_options.ReaderUserId))
        {
            throw new InvalidOperationException(
                "TwitchApi BroadcasterIds and ReaderUserId must be configured before EventSub starts.");
        }

        var registration = new TwitchEventSubChannel(
            broadcasterIds[0],
            _options.ReaderUserId)
        {
            BroadcasterIds = broadcasterIds,
        };
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
                        var badges = PreservePrivilegeBadges(message);
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
                            null)
                        {
                            TwitchEmoteCount = message.TwitchEmoteCount,
                            IsTwitchEmoteOnly = message.IsTwitchEmoteOnly,
                        };
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

    private static IReadOnlyDictionary<string, string> PreservePrivilegeBadges(
        TwitchChatMessageEvent message)
    {
        Dictionary<string, string>? mergedBadges = null;
        if (message.SourceBadges is { Count: > 0 } sourceBadges)
        {
            foreach (var sourceBadge in sourceBadges)
            {
                if (!TwitchChatBadgeRoles.GrantsAutomaticModerationBypass(
                        sourceBadge.Key)
                    || message.Badges.Keys.Any(
                        existing => string.Equals(
                            existing,
                            sourceBadge.Key,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                mergedBadges ??= new Dictionary<string, string>(
                    message.Badges,
                    StringComparer.OrdinalIgnoreCase);
                mergedBadges[sourceBadge.Key] = sourceBadge.Value;
            }
        }

        var badges = mergedBadges ?? message.Badges;
        var alreadyIdentifiedAsModerator =
            TwitchChatBadgeRoles.HasModeratorRole(badges.Keys)
            || badges.Keys.Any(key => string.Equals(
                key,
                TwitchChatBadgeRoles.Broadcaster,
                StringComparison.OrdinalIgnoreCase));
        if (!message.IsModerator || alreadyIdentifiedAsModerator)
        {
            return badges;
        }

        mergedBadges ??= new Dictionary<string, string>(
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
        mergedBadges[
            isBroadcaster
                ? TwitchChatBadgeRoles.Broadcaster
                : TwitchChatBadgeRoles.Moderator] = "1";
        return mergedBadges;
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
