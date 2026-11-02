using System.Globalization;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Twitch;
using CoreChatTextNormalizer = TwitchAI.Core.Services.ChatTextNormalizer;

namespace TwitchAI.Tests.Infrastructure;

public sealed class TwitchChatReplyExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_SendsOnlyTheRenderedChatReplyAndPersistsSuccess()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (store, proposal) = await AddPendingProposalAsync(database);
        var helix = new RecordingHelixClient
        {
            SendResult = new TwitchSendChatMessageResult(
                "outgoing-message-1",
                IsSent: true,
                DropCode: null,
                DropMessage: null),
        };
        var executor = CreateExecutor(
            store,
            helix,
            database.Path,
            broadcasterId: "channel-1",
            senderId: "moderator-1",
            sessionReady: true);

        var result = await executor.ExecuteAsync(
            proposal.ProposalId,
            proposal.RowVersion);

        Assert.Equal(ChatReplyExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal("chat_message_sent", result.StatusCode);
        Assert.Equal(1, helix.SendCalls);
        Assert.Equal(0, helix.DeleteCalls);
        Assert.Equal(0, helix.TimeoutCalls);
        Assert.Equal(
            ("channel-1", "moderator-1", "!event @viewer_name", null),
            helix.LastSend);

        var persisted = await ReadProposalStateAsync(
            database,
            proposal.ProposalId);
        Assert.Equal("executed", persisted.Status);
        Assert.Equal("outgoing-message-1", persisted.TwitchResponseId);
        Assert.Null(persisted.DropCode);
        Assert.Equal(3, persisted.RowVersion);

        var repeated = await executor.ExecuteAsync(
            proposal.ProposalId,
            result.RowVersion!.Value);

        Assert.Equal(ChatReplyExecutionOutcome.Rejected, repeated.Outcome);
        Assert.Equal("proposal_conflict", repeated.StatusCode);
        Assert.Equal(1, helix.SendCalls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTwitchDropsMessage_PersistsDropAndDoesNotRetry()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (store, proposal) = await AddPendingProposalAsync(database);
        var helix = new RecordingHelixClient
        {
            SendResult = new TwitchSendChatMessageResult(
                string.Empty,
                IsSent: false,
                DropCode: "automod_held",
                DropMessage: "Message was held by AutoMod."),
        };
        var executor = CreateExecutor(
            store,
            helix,
            database.Path,
            broadcasterId: "channel-1",
            senderId: "moderator-1",
            sessionReady: true);

        var result = await executor.ExecuteAsync(
            proposal.ProposalId,
            proposal.RowVersion);

        Assert.Equal(ChatReplyExecutionOutcome.Dropped, result.Outcome);
        Assert.Equal("automod_held", result.StatusCode);
        Assert.Equal(1, helix.SendCalls);
        Assert.Equal(0, helix.DeleteCalls);
        Assert.Equal(0, helix.TimeoutCalls);

        var persisted = await ReadProposalStateAsync(
            database,
            proposal.ProposalId);
        Assert.Equal("failed", persisted.Status);
        Assert.Equal("automod_held", persisted.DropCode);
        Assert.Equal("Message was held by AutoMod.", persisted.DropMessage);
        Assert.Equal(3, persisted.RowVersion);

        var repeated = await executor.ExecuteAsync(
            proposal.ProposalId,
            result.RowVersion!.Value);

        Assert.Equal(ChatReplyExecutionOutcome.Rejected, repeated.Outcome);
        Assert.Equal(1, helix.SendCalls);
    }

    [Fact]
    public async Task ExecuteAsync_WithoutConnectedSession_RejectsBeforeClaimOrSend()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (store, proposal) = await AddPendingProposalAsync(database);
        var helix = new RecordingHelixClient();
        var executor = CreateExecutor(
            store,
            helix,
            database.Path,
            broadcasterId: "channel-1",
            senderId: "moderator-1",
            sessionReady: false);

        var result = await executor.ExecuteAsync(
            proposal.ProposalId,
            proposal.RowVersion);

        Assert.Equal(ChatReplyExecutionOutcome.Rejected, result.Outcome);
        Assert.Equal("twitch_not_connected", result.StatusCode);
        Assert.Equal(0, helix.SendCalls);
        Assert.Equal(0, helix.DeleteCalls);
        Assert.Equal(0, helix.TimeoutCalls);

        var persisted = await ReadProposalStateAsync(
            database,
            proposal.ProposalId);
        Assert.Equal("pending", persisted.Status);
        Assert.Equal(1, persisted.RowVersion);
    }

    [Fact]
    public async Task ExecuteAsync_AfterChannelChange_FailsWithoutCallingTwitch()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (store, proposal) = await AddPendingProposalAsync(database);
        var helix = new RecordingHelixClient();
        var executor = CreateExecutor(
            store,
            helix,
            database.Path,
            broadcasterId: "different-channel",
            senderId: "moderator-1",
            sessionReady: true);

        var result = await executor.ExecuteAsync(
            proposal.ProposalId,
            proposal.RowVersion);

        Assert.Equal(ChatReplyExecutionOutcome.Failed, result.Outcome);
        Assert.Equal("channel_changed", result.StatusCode);
        Assert.Equal(0, helix.SendCalls);
        Assert.Equal(0, helix.DeleteCalls);
        Assert.Equal(0, helix.TimeoutCalls);

        var persisted = await ReadProposalStateAsync(
            database,
            proposal.ProposalId);
        Assert.Equal("failed", persisted.Status);
        Assert.Equal("channel_changed", persisted.DropCode);
        Assert.Equal(3, persisted.RowVersion);
    }

    [Fact]
    public async Task ExecuteAsync_WhenProposalBelongsToAnyConnectedChannel_SendsToProposalChannel()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (store, proposal) = await AddPendingProposalAsync(database);
        var helix = new RecordingHelixClient();
        var executor = CreateExecutor(
            store,
            helix,
            database.Path,
            broadcasterId: "channel-2",
            senderId: "moderator-1",
            sessionReady: true,
            broadcasterIds: ["channel-2", "channel-1"]);

        var result = await executor.ExecuteAsync(
            proposal.ProposalId,
            proposal.RowVersion);

        Assert.Equal(ChatReplyExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal("chat_message_sent", result.StatusCode);
        Assert.Equal(1, helix.SendCalls);
        Assert.Equal("channel-1", helix.LastSend?.BroadcasterId);
    }

    [Fact]
    public async Task ExecuteAsync_OnAmbiguousNetworkFailure_RecordsOutcomeUnknownAndNeverRetries()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (store, proposal) = await AddPendingProposalAsync(database);
        var helix = new RecordingHelixClient
        {
            SendException = new HttpRequestException(
                "The connection was reset after upload."),
        };
        var executor = CreateExecutor(
            store,
            helix,
            database.Path,
            broadcasterId: "channel-1",
            senderId: "moderator-1",
            sessionReady: true);

        var result = await executor.ExecuteAsync(
            proposal.ProposalId,
            proposal.RowVersion);

        Assert.Equal(ChatReplyExecutionOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal("transport_outcome_unknown", result.StatusCode);
        Assert.Equal(1, helix.SendCalls);
        Assert.Equal(0, helix.DeleteCalls);
        Assert.Equal(0, helix.TimeoutCalls);

        var persisted = await ReadProposalStateAsync(
            database,
            proposal.ProposalId);
        Assert.Equal("outcome_unknown", persisted.Status);
        Assert.Equal("transport_outcome_unknown", persisted.DropCode);
        Assert.Equal(3, persisted.RowVersion);

        var repeated = await executor.ExecuteAsync(
            proposal.ProposalId,
            result.RowVersion!.Value);

        Assert.Equal(ChatReplyExecutionOutcome.Rejected, repeated.Outcome);
        Assert.Equal("proposal_conflict", repeated.StatusCode);
        Assert.Equal(1, helix.SendCalls);
    }

    [Fact]
    public async Task ExecuteAsync_ReadOnlyCompletesLocallyWithoutSendingChatMessage()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (store, proposal) = await AddPendingProposalAsync(database);
        var helix = new RecordingHelixClient();
        var executor = CreateExecutor(
            store,
            helix,
            database.Path,
            broadcasterId: "channel-1",
            senderId: "moderator-1",
            sessionReady: false,
            readOnly: true);

        var result = await executor.ExecuteAsync(
            proposal.ProposalId,
            proposal.RowVersion);

        Assert.Equal(ChatReplyExecutionOutcome.Succeeded, result.Outcome);
        Assert.Equal("read_only_not_sent", result.StatusCode);
        Assert.Equal(0, helix.SendCalls);
        var persisted = await ReadProposalStateAsync(database, proposal.ProposalId);
        Assert.Equal("executed", persisted.Status);
        Assert.Null(persisted.TwitchResponseId);
        Assert.Equal("read_only_not_sent", persisted.DropCode);
    }

    private static TwitchChatReplyExecutor CreateExecutor(
        IChatReplyStore store,
        ITwitchHelixClient helix,
        string databasePath,
        string broadcasterId,
        string senderId,
        bool sessionReady,
        bool readOnly = false,
        IReadOnlyList<string>? broadcasterIds = null)
    {
        var readiness = new TwitchConnectionReadiness();
        readiness.SetSessionReady(
            sessionReady,
            sessionReady ? "Twitch verbunden." : "Twitch nicht verbunden.");
        return new TwitchChatReplyExecutor(
            store,
            helix,
            readiness,
            new InfrastructureOptions
            {
                DatabasePath = databasePath,
                TwitchApi = new TwitchApiOptions
                {
                    BroadcasterId = broadcasterId,
                    BroadcasterIds = broadcasterIds?.ToArray() ?? [],
                    ModeratorUserId = senderId,
                },
            },
            new TwitchRuntimeModeSettings(readOnly));
    }

    private static async Task<(
        SqliteChatReplyStore Store,
        ChatReplyProposalRecord Proposal)> AddPendingProposalAsync(
        TestDatabase database)
    {
        var store = new SqliteChatReplyStore(database.ConnectionFactory);
        await store.AddTemplateAsync(
            "Wie lange geht das Event noch?",
            "!event @Username");
        var template = Assert.Single(await store.LoadActiveTemplatesAsync());
        var receivedAt = new DateTimeOffset(
            2026,
            7,
            28,
            12,
            0,
            0,
            TimeSpan.Zero);
        var identity = ChatMessageIdentityV1.Create(
            "event-1",
            "source-message-1",
            "channel-1",
            "user-1",
            "viewer_name",
            "Viewer Name",
            "Wie lange läuft das Event?",
            receivedAt);
        var context = new ChannelContextSnapshot(
            "channel-1",
            TwitchAI.Core.Services.EffectivePolicyResolver
                .BootstrapJustChattingCategoryId,
            "Just Chatting",
            receivedAt,
            Revision: 1,
            IsVerified: true);
        var message = new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(identity.OriginalText),
            context,
            IngressSequence: 1);
        var renderedResponse = SqliteChatReplyStore.RenderResponse(
            template.ResponseMessage,
            identity.Login);
        var added = await store.AddProposalAsync(
            message,
            template,
            renderedResponse,
            confidence: 0.94,
            batchId: Guid.NewGuid(),
            aiLatencyMilliseconds: 123);
        Assert.NotNull(added);
        Assert.True(added.IsCreated);
        return (store, added.Proposal);
    }

    private static async Task<ProposalState> ReadProposalStateAsync(
        TestDatabase database,
        long proposalId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT status, row_version, twitch_response_id,
                   twitch_drop_code, twitch_drop_message
            FROM chat_reply_proposals
            WHERE proposal_id = @proposal_id;
            """;
        command.Parameters.AddWithValue("@proposal_id", proposalId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new ProposalState(
            reader.GetString(0),
            Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    private sealed record ProposalState(
        string Status,
        long RowVersion,
        string? TwitchResponseId,
        string? DropCode,
        string? DropMessage);

    private sealed class RecordingHelixClient : ITwitchHelixClient
    {
        public TwitchSendChatMessageResult SendResult { get; init; } =
            new("outgoing-message", true, null, null);

        public Exception? SendException { get; init; }

        public int SendCalls { get; private set; }

        public int DeleteCalls { get; private set; }

        public int TimeoutCalls { get; private set; }

        public (
            string BroadcasterId,
            string SenderId,
            string Message,
            string? ReplyParentMessageId)? LastSend { get; private set; }

        public Task<TwitchSendChatMessageResult> SendChatMessageAsync(
            string broadcasterId,
            string senderId,
            string message,
            string? replyParentMessageId = null,
            CancellationToken cancellationToken = default)
        {
            SendCalls++;
            LastSend = (
                broadcasterId,
                senderId,
                message,
                replyParentMessageId);
            return SendException is null
                ? Task.FromResult(SendResult)
                : Task.FromException<TwitchSendChatMessageResult>(SendException);
        }

        public Task DeleteMessageAsync(
            string broadcasterId,
            string moderatorId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            throw new InvalidOperationException(
                "A chat reply must never delete a Twitch message.");
        }

        public Task TimeoutUserAsync(
            string broadcasterId,
            string moderatorId,
            string userId,
            int durationSeconds,
            string reason,
            CancellationToken cancellationToken = default)
        {
            TimeoutCalls++;
            throw new InvalidOperationException(
                "A chat reply must never timeout a Twitch user.");
        }

        public Task BanUserAsync(
            string broadcasterId,
            string moderatorId,
            string userId,
            string reason,
            CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException(
                "A chat reply must never ban a Twitch user.");
        }

        public Task<TwitchUserInfo> GetCurrentUserAsync(
            CancellationToken cancellationToken = default) =>
            Unexpected<TwitchUserInfo>();

        public Task<TwitchChannelInfo?> GetChannelAsync(
            string broadcasterId,
            CancellationToken cancellationToken = default) =>
            Unexpected<TwitchChannelInfo?>();

        public Task<IReadOnlyList<TwitchGameInfo>> GetGamesAsync(
            IReadOnlyCollection<string> categoryIds,
            CancellationToken cancellationToken = default) =>
            Unexpected<IReadOnlyList<TwitchGameInfo>>();

        public Task<TwitchGameInfo?> GetGameByNameAsync(
            string categoryName,
            CancellationToken cancellationToken = default) =>
            Unexpected<TwitchGameInfo?>();

        public Task<IReadOnlyList<TwitchModeratedChannel>> GetModeratedChannelsAsync(
            string userId,
            CancellationToken cancellationToken = default) =>
            Unexpected<IReadOnlyList<TwitchModeratedChannel>>();

        public Task<TwitchBannedUserInfo?> GetBannedUserAsync(
            string broadcasterId,
            string userId,
            CancellationToken cancellationToken = default) =>
            Unexpected<TwitchBannedUserInfo?>();

        public Task CreateEventSubSubscriptionAsync(
            TwitchEventSubSubscriptionRequest subscription,
            CancellationToken cancellationToken = default) =>
            Unexpected<object>();

        private static Task<T> Unexpected<T>() =>
            Task.FromException<T>(
                new InvalidOperationException(
                    "The chat reply executor called an unrelated Twitch API."));
    }
}
