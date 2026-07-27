using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Infrastructure.Twitch;

public sealed record TwitchEventSubChannel(
    string BroadcasterId,
    string ReaderUserId);

public abstract record TwitchEventSubEvent(
    string EventMessageId,
    string BroadcasterId,
    DateTimeOffset ObservedAt);

public sealed record TwitchChatMessageEvent(
    string EventMessageId,
    string BroadcasterId,
    DateTimeOffset ObservedAt,
    string MessageId,
    string ChatterUserId,
    string ChatterLogin,
    string ChatterDisplayName,
    string MessageText,
    bool IsModerator,
    IReadOnlyDictionary<string, string> Badges,
    string? SourceBroadcasterId,
    string? SourceMessageId)
    : TwitchEventSubEvent(EventMessageId, BroadcasterId, ObservedAt);

public sealed record TwitchCategoryChangedEvent(
    string EventMessageId,
    string BroadcasterId,
    DateTimeOffset ObservedAt,
    string? CategoryId,
    string? CategoryName,
    bool IsHelixReconciliation)
    : TwitchEventSubEvent(EventMessageId, BroadcasterId, ObservedAt);

public sealed record TwitchMessageDeletedEvent(
    string EventMessageId,
    string BroadcasterId,
    DateTimeOffset ObservedAt,
    string MessageId,
    string TargetUserId,
    string TargetUserLogin,
    string TargetUserName)
    : TwitchEventSubEvent(EventMessageId, BroadcasterId, ObservedAt);

public sealed record TwitchUserMessagesClearedEvent(
    string EventMessageId,
    string BroadcasterId,
    DateTimeOffset ObservedAt,
    string TargetUserId,
    string TargetUserLogin,
    string TargetUserName)
    : TwitchEventSubEvent(EventMessageId, BroadcasterId, ObservedAt);

public sealed record TwitchRoomClearedEvent(
    string EventMessageId,
    string BroadcasterId,
    DateTimeOffset ObservedAt)
    : TwitchEventSubEvent(EventMessageId, BroadcasterId, ObservedAt);

public sealed record TwitchEventSubRevokedEvent(
    string EventMessageId,
    string BroadcasterId,
    DateTimeOffset ObservedAt,
    string SubscriptionType,
    string Status)
    : TwitchEventSubEvent(EventMessageId, BroadcasterId, ObservedAt);

public sealed record TwitchEventSubGapEvent(
    string EventMessageId,
    string BroadcasterId,
    DateTimeOffset ObservedAt,
    DateTimeOffset ThroughReceivedAt,
    string Reason)
    : TwitchEventSubEvent(EventMessageId, BroadcasterId, ObservedAt);

internal sealed record TwitchEventSubBarrierEvent(
    string EventMessageId,
    string BroadcasterId,
    DateTimeOffset ObservedAt,
    TaskCompletionSource Completion)
    : TwitchEventSubEvent(EventMessageId, BroadcasterId, ObservedAt);

public interface IEventSubProcessingBarrier
{
    Task DrainAsync(CancellationToken cancellationToken = default);
}

public interface ITwitchEventSubWebSocketClient
{
    IAsyncEnumerable<TwitchEventSubEvent> ReadAllAsync(
        TwitchEventSubChannel channel,
        CancellationToken cancellationToken = default);
}

public sealed class TwitchEventSubWebSocketClient :
    ITwitchEventSubWebSocketClient,
    IEventSubProcessingBarrier
{
    private const int MaximumMessageBytes = 1_048_576;
    private readonly ITwitchHelixClient _helixClient;
    private readonly IEventSubDedupeStore _dedupeStore;
    private readonly ITwitchConnectionReadiness _connectionReadiness;
    private readonly TwitchApiOptions _options;
    private readonly object _activeWriterGate = new();
    private ChannelWriter<TwitchEventSubEvent>? _activeWriter;
    private string? _activeBroadcasterId;

    public TwitchEventSubWebSocketClient(
        ITwitchHelixClient helixClient,
        IEventSubDedupeStore dedupeStore,
        ITwitchConnectionReadiness connectionReadiness,
        InfrastructureOptions options)
    {
        _helixClient = helixClient;
        _dedupeStore = dedupeStore;
        _connectionReadiness = connectionReadiness;
        _options = options.TwitchApi;
    }

    public IAsyncEnumerable<TwitchEventSubEvent> ReadAllAsync(
        TwitchEventSubChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel.BroadcasterId);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel.ReaderUserId);
        return ReadBufferedAsync(channel, cancellationToken);
    }

    public async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        ChannelWriter<TwitchEventSubEvent> writer;
        string broadcasterId;
        lock (_activeWriterGate)
        {
            writer = _activeWriter
                ?? throw new InvalidOperationException(
                    "EventSub ist nicht aktiv; die Verfügbarkeit kann nicht sicher geprüft werden.");
            broadcasterId = _activeBroadcasterId
                ?? throw new InvalidOperationException(
                    "Der aktive EventSub-Kanal ist unbekannt.");
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observedAt = DateTimeOffset.UtcNow;
        await writer.WriteAsync(
            new TwitchEventSubBarrierEvent(
                $"local-eventsub-barrier-{Guid.NewGuid():N}",
                broadcasterId,
                observedAt,
                completion),
            cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<TwitchEventSubEvent> ReadBufferedAsync(
        TwitchEventSubChannel eventSubChannel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var buffer = Channel.CreateBounded<TwitchEventSubEvent>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        lock (_activeWriterGate)
        {
            if (_activeWriter is not null)
            {
                throw new InvalidOperationException(
                    "Für den EventSub-Client ist bereits ein aktiver Leser registriert.");
            }

            _activeWriter = buffer.Writer;
            _activeBroadcasterId = eventSubChannel.BroadcasterId;
        }
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(eventSubChannel, buffer.Writer, linkedSource.Token);

        try
        {
            await foreach (var item in buffer.Reader
                               .ReadAllAsync(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            lock (_activeWriterGate)
            {
                if (ReferenceEquals(_activeWriter, buffer.Writer))
                {
                    _activeWriter = null;
                    _activeBroadcasterId = null;
                }
            }

            await linkedSource.CancelAsync().ConfigureAwait(false);
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
            {
                // Enumeration cancellation is the expected shutdown path.
            }
        }
    }

    private async Task ProduceAsync(
        TwitchEventSubChannel channel,
        ChannelWriter<TwitchEventSubEvent> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            await ProduceCoreAsync(channel, writer, cancellationToken).ConfigureAwait(false);
            writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            writer.TryComplete();
        }
        catch (Exception exception)
        {
            _connectionReadiness.SetEventSubOffline(
                "EventSub wurde wegen eines Fehlers beendet.",
                isFaulted: true);
            writer.TryComplete(exception);
        }
        finally
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _connectionReadiness.SetEventSubOffline(
                    "EventSub ist pausiert.");
            }
        }
    }

    private async Task ProduceCoreAsync(
        TwitchEventSubChannel channel,
        ChannelWriter<TwitchEventSubEvent> writer,
        CancellationToken cancellationToken)
    {

        await _dedupeStore.DeleteOlderThanAsync(
            DateTimeOffset.UtcNow - _options.ProcessedEventRetention,
            cancellationToken).ConfigureAwait(false);

        TwitchCategoryChangedEvent? currentCategory;
        try
        {
            currentCategory = await ReconcileCategoryAsync(
                    channel.BroadcasterId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            currentCategory = null;
        }
        if (currentCategory is not null)
        {
            await writer.WriteAsync(currentCategory, cancellationToken).ConfigureAwait(false);
        }

        var socketUri = _options.EventSubWebSocketUri;
        var reconnectHandoff = false;
        var reconnectAttempt = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            _connectionReadiness.SetEventSubConnecting(
                isReconnect: reconnectAttempt > 0 || reconnectHandoff,
                reconnectAttempt > 0 || reconnectHandoff
                    ? "EventSub wird neu verbunden."
                    : "EventSub wird verbunden.");
            if (reconnectAttempt > 0 && !reconnectHandoff)
            {
                TwitchCategoryChangedEvent? reconciledAfterGap;
                try
                {
                    reconciledAfterGap = await ReconcileCategoryAsync(
                        channel.BroadcasterId,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    reconciledAfterGap = null;
                }
                if (reconciledAfterGap is not null
                    && !SameCategory(currentCategory, reconciledAfterGap))
                {
                    currentCategory = reconciledAfterGap;
                    await writer.WriteAsync(reconciledAfterGap, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            using var socket = new ClientWebSocket();
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            string? unexpectedDisconnectReason = null;
            var handoffRequestedThisConnection = false;

            try
            {
                await socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);
                var welcomeJson = await ReceiveTextAsync(socket, cancellationToken).ConfigureAwait(false)
                    ?? throw new WebSocketException("EventSub closed before session_welcome.");
                using var welcome = JsonDocument.Parse(welcomeJson);
                var session = ParseWelcome(welcome.RootElement);

                if (!reconnectHandoff)
                {
                    await CreateSubscriptionsAsync(channel, session.SessionId, cancellationToken)
                        .ConfigureAwait(false);
                }

                _connectionReadiness.SetEventSubReady(
                    "EventSub ist verbunden und empfangsbereit.");
                reconnectHandoff = false;
                reconnectAttempt = 0;
                var lastServerMessageAt = DateTimeOffset.UtcNow;
                var nextReconciliationAt =
                    DateTimeOffset.UtcNow + NextCategoryReconciliationDelay();
                Task<string?>? receiveTask = null;
                var reconnectRequested = false;

                while (socket.State == WebSocketState.Open && !reconnectRequested)
                {
                    receiveTask ??= ReceiveTextAsync(socket, cancellationToken);
                    var keepaliveDeadline =
                        lastServerMessageAt +
                        session.KeepaliveTimeout +
                        TimeSpan.FromSeconds(10);
                    var nextDeadline = nextReconciliationAt < keepaliveDeadline
                        ? nextReconciliationAt
                        : keepaliveDeadline;
                    var remaining = nextDeadline - DateTimeOffset.UtcNow;
                    if (remaining < TimeSpan.Zero)
                    {
                        remaining = TimeSpan.Zero;
                    }

                    var delayTask = Task.Delay(remaining, cancellationToken);
                    var completed = await Task.WhenAny(receiveTask, delayTask).ConfigureAwait(false);
                    if (completed == delayTask && !receiveTask.IsCompleted)
                    {
                        if (DateTimeOffset.UtcNow >= keepaliveDeadline)
                        {
                            throw new WebSocketException(
                                "EventSub keepalive deadline expired.");
                        }

                        TwitchCategoryChangedEvent? reconciled;
                        try
                        {
                            reconciled = await ReconcileCategoryAsync(
                                channel.BroadcasterId,
                                cancellationToken).ConfigureAwait(false);
                        }
                        catch (HttpRequestException)
                        {
                            reconciled = null;
                        }
                        nextReconciliationAt =
                            DateTimeOffset.UtcNow + NextCategoryReconciliationDelay();
                        if (reconciled is not null &&
                            !SameCategory(currentCategory, reconciled))
                        {
                            currentCategory = reconciled;
                            await writer.WriteAsync(reconciled, cancellationToken).ConfigureAwait(false);
                        }

                        continue;
                    }

                    var json = await receiveTask.ConfigureAwait(false);
                    receiveTask = null;
                    if (json is null)
                    {
                        unexpectedDisconnectReason = "socket_closed";
                        break;
                    }

                    lastServerMessageAt = DateTimeOffset.UtcNow;
                    using var document = JsonDocument.Parse(json);
                    var metadata = document.RootElement.GetProperty("metadata");
                    var messageType = RequiredString(metadata, "message_type");

                    switch (messageType)
                    {
                        case "session_keepalive":
                            break;
                        case "session_reconnect":
                            socketUri = ParseReconnectUri(document.RootElement);
                            reconnectHandoff = true;
                            handoffRequestedThisConnection = true;
                            reconnectRequested = true;
                            break;
                        case "notification":
                        {
                            var parsed = await ParseNotificationAsync(
                                document.RootElement,
                                cancellationToken).ConfigureAwait(false);
                            if (parsed is TwitchCategoryChangedEvent categoryChanged)
                            {
                                currentCategory = categoryChanged;
                            }

                            if (parsed is not null)
                            {
                                try
                                {
                                    await writer.WriteAsync(parsed, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                                catch
                                {
                                    await _dedupeStore.ReleaseProcessedAsync(
                                        parsed.EventMessageId,
                                        CancellationToken.None).ConfigureAwait(false);
                                    throw;
                                }
                            }

                            break;
                        }
                        case "revocation":
                        {
                            var revoked = await ParseRevocationAsync(
                                document.RootElement,
                                channel.BroadcasterId,
                                cancellationToken).ConfigureAwait(false);
                            if (revoked is not null)
                            {
                                try
                                {
                                    await writer.WriteAsync(revoked, cancellationToken)
                                        .ConfigureAwait(false);
                                }
                                catch
                                {
                                    await _dedupeStore.ReleaseProcessedAsync(
                                        revoked.EventMessageId,
                                        CancellationToken.None).ConfigureAwait(false);
                                    throw;
                                }
                                throw new InvalidDataException(
                                    $"EventSub subscription '{revoked.SubscriptionType}' was revoked with status '{revoked.Status}'.");
                            }

                            break;
                        }
                    }

                }

                if (socket.State == WebSocketState.Open)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Reconnect",
                        cancellationToken).ConfigureAwait(false);
                }

                if (reconnectRequested)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is WebSocketException
                    or HttpRequestException
                    or JsonException
                    or InvalidDataException)
            {
                _connectionReadiness.SetEventSubConnecting(
                    isReconnect: true,
                    "EventSub wurde unterbrochen und wird neu verbunden.");
                if (!handoffRequestedThisConnection)
                {
                    unexpectedDisconnectReason =
                        exception is WebSocketException webSocketException &&
                        webSocketException.WebSocketErrorCode ==
                        WebSocketError.ConnectionClosedPrematurely
                            ? "socket_closed_prematurely"
                            : exception is HttpRequestException
                                ? "twitch_http_error"
                                : exception is JsonException or InvalidDataException
                                    ? "eventsub_payload_error"
                                    : "websocket_error";
                    reconnectHandoff = false;
                    socketUri = _options.EventSubWebSocketUri;
                }
            }

            if (unexpectedDisconnectReason is not null)
            {
                var gapAt = DateTimeOffset.UtcNow;
                // Force the next successful Helix reconciliation to be emitted
                // even when Twitch kept the same category. The coordinator has
                // invalidated the old verified snapshot after this gap.
                currentCategory = null;
                await writer.WriteAsync(
                    new TwitchEventSubGapEvent(
                        $"local-eventsub-gap-{Guid.NewGuid():N}",
                        channel.BroadcasterId,
                        gapAt,
                        gapAt,
                        unexpectedDisconnectReason),
                    cancellationToken).ConfigureAwait(false);
            }

            reconnectAttempt++;
            var delay = Backoff(reconnectAttempt, _options.MaximumReconnectDelay);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CreateSubscriptionsAsync(
        TwitchEventSubChannel channel,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var chatCondition = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["broadcaster_user_id"] = channel.BroadcasterId,
            ["user_id"] = channel.ReaderUserId,
        };
        var channelCondition = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["broadcaster_user_id"] = channel.BroadcasterId,
        };

        TwitchEventSubSubscriptionRequest[] subscriptions =
        [
            new("channel.update", "2", channelCondition, sessionId),
            new("channel.chat.message", "1", chatCondition, sessionId),
            new("channel.chat.message_delete", "1", chatCondition, sessionId),
            new("channel.chat.clear_user_messages", "1", chatCondition, sessionId),
            new("channel.chat.clear", "1", chatCondition, sessionId),
        ];

        foreach (var subscription in subscriptions)
        {
            await _helixClient.CreateEventSubSubscriptionAsync(subscription, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<TwitchCategoryChangedEvent?> ReconcileCategoryAsync(
        string broadcasterId,
        CancellationToken cancellationToken)
    {
        var channel = await _helixClient.GetChannelAsync(broadcasterId, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            return null;
        }

        var observedAt = DateTimeOffset.UtcNow;
        return new TwitchCategoryChangedEvent(
            $"helix-category-{observedAt.ToUnixTimeMilliseconds()}",
            broadcasterId,
            observedAt,
            channel.CategoryId,
            channel.CategoryName,
            IsHelixReconciliation: true);
    }

    private async Task<TwitchEventSubEvent?> ParseNotificationAsync(
        JsonElement root,
        CancellationToken cancellationToken)
    {
        var metadata = root.GetProperty("metadata");
        var eventMessageId = RequiredString(metadata, "message_id");
        var subscriptionType = RequiredString(metadata, "subscription_type");
        var observedAt = ParseTimestamp(OptionalString(metadata, "message_timestamp"));
        var payload = root.GetProperty("payload");
        var @event = payload.GetProperty("event");
        var broadcasterId = RequiredString(@event, "broadcaster_user_id");

        // Parse and validate before committing the dedupe marker. A malformed
        // delivery must remain retryable instead of being silently lost forever.
        TwitchEventSubEvent? parsed = subscriptionType switch
        {
            "channel.chat.message" => ParseChatMessage(
                eventMessageId,
                broadcasterId,
                observedAt,
                @event),
            "channel.update" => new TwitchCategoryChangedEvent(
                eventMessageId,
                broadcasterId,
                observedAt,
                NullIfEmpty(OptionalString(@event, "category_id")),
                NullIfEmpty(OptionalString(@event, "category_name")),
                IsHelixReconciliation: false),
            "channel.chat.message_delete" => new TwitchMessageDeletedEvent(
                eventMessageId,
                broadcasterId,
                observedAt,
                RequiredString(@event, "message_id"),
                RequiredString(@event, "target_user_id"),
                OptionalString(@event, "target_user_login") ?? string.Empty,
                OptionalString(@event, "target_user_name") ?? string.Empty),
            "channel.chat.clear_user_messages" => new TwitchUserMessagesClearedEvent(
                eventMessageId,
                broadcasterId,
                observedAt,
                RequiredString(@event, "target_user_id"),
                OptionalString(@event, "target_user_login") ?? string.Empty,
                OptionalString(@event, "target_user_name") ?? string.Empty),
            "channel.chat.clear" => new TwitchRoomClearedEvent(
                eventMessageId,
                broadcasterId,
                observedAt),
            _ => null,
        };
        if (parsed is null ||
            !await _dedupeStore.TryMarkProcessedAsync(
                eventMessageId,
                subscriptionType,
                observedAt,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return parsed;
    }

    private async Task<TwitchEventSubRevokedEvent?> ParseRevocationAsync(
        JsonElement root,
        string broadcasterId,
        CancellationToken cancellationToken)
    {
        var metadata = root.GetProperty("metadata");
        var eventMessageId = RequiredString(metadata, "message_id");
        var observedAt = ParseTimestamp(OptionalString(metadata, "message_timestamp"));
        var payload = root.GetProperty("payload");
        var subscription = payload.GetProperty("subscription");
        var type = RequiredString(subscription, "type");
        if (!await _dedupeStore.TryMarkProcessedAsync(
                eventMessageId,
                type,
                observedAt,
                cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TwitchEventSubRevokedEvent(
            eventMessageId,
            broadcasterId,
            observedAt,
            type,
            RequiredString(subscription, "status"));
    }

    private static TwitchChatMessageEvent ParseChatMessage(
        string eventMessageId,
        string broadcasterId,
        DateTimeOffset observedAt,
        JsonElement @event)
    {
        var message = @event.GetProperty("message");
        var chatterUserId = RequiredString(@event, "chatter_user_id");
        var sourceBroadcasterId =
            NullIfEmpty(OptionalString(@event, "source_broadcaster_user_id"));
        var badges = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (@event.TryGetProperty("badges", out var badgesProperty) &&
            badgesProperty.ValueKind == JsonValueKind.Array)
        {
            foreach (var badge in badgesProperty.EnumerateArray())
            {
                var setId = NullIfEmpty(OptionalString(badge, "set_id"));
                if (setId is not null)
                {
                    badges[setId] =
                        NullIfEmpty(OptionalString(badge, "id")) ?? "1";
                }
            }
        }

        var isModerator =
            string.Equals(
                chatterUserId,
                broadcasterId,
                StringComparison.Ordinal) ||
            string.Equals(
                chatterUserId,
                sourceBroadcasterId,
                StringComparison.Ordinal) ||
            badges.ContainsKey("moderator") ||
            badges.ContainsKey("broadcaster");

        return new TwitchChatMessageEvent(
            eventMessageId,
            broadcasterId,
            observedAt,
            RequiredString(@event, "message_id"),
            chatterUserId,
            RequiredString(@event, "chatter_user_login"),
            RequiredString(@event, "chatter_user_name"),
            RequiredString(message, "text"),
            isModerator,
            badges,
            sourceBroadcasterId,
            NullIfEmpty(OptionalString(@event, "source_message_id")));
    }

    private static SessionWelcome ParseWelcome(JsonElement root)
    {
        var metadata = root.GetProperty("metadata");
        if (!StringComparer.Ordinal.Equals(
                RequiredString(metadata, "message_type"),
                "session_welcome"))
        {
            throw new InvalidDataException("Expected EventSub session_welcome.");
        }

        var session = root.GetProperty("payload").GetProperty("session");
        return new SessionWelcome(
            RequiredString(session, "id"),
            TimeSpan.FromSeconds(RequiredInt32(session, "keepalive_timeout_seconds")));
    }

    private static Uri ParseReconnectUri(JsonElement root)
    {
        var session = root.GetProperty("payload").GetProperty("session");
        return new Uri(RequiredString(session, "reconnect_url"), UriKind.Absolute);
    }

    private static async Task<string?> ReceiveTextAsync(
        ClientWebSocket socket,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(
                new ArraySegment<byte>(buffer),
                cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new InvalidDataException("EventSub sent a non-text WebSocket message.");
            }

            if (stream.Length + result.Count > MaximumMessageBytes)
            {
                throw new InvalidDataException("EventSub message exceeded the 1 MiB safety limit.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
            }
        }
    }

    private static TimeSpan Backoff(int attempt, TimeSpan maximum)
    {
        var exponentialSeconds = Math.Min(Math.Pow(2, Math.Min(attempt, 5)), maximum.TotalSeconds);
        var jitter = Random.Shared.NextDouble();
        return TimeSpan.FromSeconds(Math.Min(exponentialSeconds + jitter, maximum.TotalSeconds));
    }

    private TimeSpan NextCategoryReconciliationDelay()
    {
        var interval = _options.CategoryReconciliationInterval;
        var maximumJitterSeconds = Math.Min(
            30,
            Math.Max(1, interval.TotalSeconds * 0.1));
        var signedJitterSeconds =
            ((Random.Shared.NextDouble() * 2) - 1) * maximumJitterSeconds;
        var delay = interval + TimeSpan.FromSeconds(signedJitterSeconds);
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
    }

    private static bool SameCategory(
        TwitchCategoryChangedEvent? left,
        TwitchCategoryChangedEvent right)
    {
        return left is not null &&
               StringComparer.Ordinal.Equals(left.CategoryId, right.CategoryId) &&
               StringComparer.Ordinal.Equals(left.CategoryName, right.CategoryName);
    }

    private static DateTimeOffset ParseTimestamp(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.Parse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind);
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        return OptionalString(parent, name)
            ?? throw new InvalidDataException($"EventSub payload is missing '{name}'.");
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
            : throw new InvalidDataException($"EventSub payload is missing numeric '{name}'.");
    }

    private static string? NullIfEmpty(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private sealed record SessionWelcome(string SessionId, TimeSpan KeepaliveTimeout);
}
