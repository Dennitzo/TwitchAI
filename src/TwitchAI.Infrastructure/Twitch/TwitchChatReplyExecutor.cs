using TwitchAI.Core.Contracts;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.Persistence;

namespace TwitchAI.Infrastructure.Twitch;

public enum ChatReplyExecutionOutcome
{
    Succeeded,
    Rejected,
    Dropped,
    Failed,
    OutcomeUnknown,
}

public sealed record ChatReplyExecutionResult(
    ChatReplyExecutionOutcome Outcome,
    string StatusCode,
    string Message,
    long? RowVersion = null);

public interface ITwitchChatReplyExecutor
{
    Task<ChatReplyExecutionResult> ExecuteAsync(
        long proposalId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default);

    Task<ChatReplyExecutionResult> DiscardAsync(
        long proposalId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default);
}

public sealed class TwitchChatReplyExecutor : ITwitchChatReplyExecutor
{
    private readonly IChatReplyStore _store;
    private readonly ITwitchHelixClient _helixClient;
    private readonly ITwitchConnectionReadiness _connectionReadiness;
    private readonly InfrastructureOptions _options;
    private readonly ITwitchRuntimeModeSettings _runtimeMode;

    public TwitchChatReplyExecutor(
        IChatReplyStore store,
        ITwitchHelixClient helixClient,
        ITwitchConnectionReadiness connectionReadiness,
        InfrastructureOptions options,
        ITwitchRuntimeModeSettings? runtimeMode = null)
    {
        _store = store;
        _helixClient = helixClient;
        _connectionReadiness = connectionReadiness;
        _options = options;
        _runtimeMode = runtimeMode ?? new TwitchRuntimeModeSettings();
    }

    public async Task<ChatReplyExecutionResult> ExecuteAsync(
        long proposalId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        var enteredAsReadOnly = _runtimeMode.IsReadOnly;
        if (!enteredAsReadOnly && !_connectionReadiness.IsSessionReady)
        {
            return Rejected(
                "twitch_not_connected",
                "Die Twitch-Verbindung ist nicht aktiv. Die Antwort wurde nicht gesendet.");
        }

        var senderId = _options.TwitchApi.ModeratorUserId;
        var currentBroadcasterId = _options.TwitchApi.BroadcasterId;
        if (string.IsNullOrWhiteSpace(senderId)
            || string.IsNullOrWhiteSpace(currentBroadcasterId))
        {
            return Rejected(
                "twitch_identity_missing",
                "Das verbundene Twitch-Konto oder der Kanal ist nicht vollständig konfiguriert.");
        }

        var proposal = await _store
            .TryBeginExecutionAsync(proposalId, expectedRowVersion, cancellationToken)
            .ConfigureAwait(false);
        if (proposal is null)
        {
            return Rejected(
                "proposal_conflict",
                "Der Chat-Antwortvorschlag wurde bereits verarbeitet oder parallel geändert.");
        }

        if (!string.Equals(
                proposal.BroadcasterId,
                currentBroadcasterId,
                StringComparison.Ordinal))
        {
            await CompleteDefiniteFailureAsync(
                    proposal,
                    "channel_changed",
                    "Der aktuell verbundene Twitch-Kanal stimmt nicht mehr mit dem Vorschlag überein.",
                    cancellationToken)
                .ConfigureAwait(false);
            return new ChatReplyExecutionResult(
                ChatReplyExecutionOutcome.Failed,
                "channel_changed",
                "Der Kanal wurde gewechselt. Die Antwort wurde nicht gesendet.",
                proposal.RowVersion + 1);
        }

        string expectedResponse;
        try
        {
            expectedResponse = SqliteChatReplyStore.RenderResponse(
                proposal.ConfiguredResponse,
                proposal.ChatterLogin);
        }
        catch (ArgumentException exception)
        {
            await CompleteDefiniteFailureAsync(
                    proposal,
                    "reply_invalid",
                    exception.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ChatReplyExecutionResult(
                ChatReplyExecutionOutcome.Failed,
                "reply_invalid",
                exception.Message,
                proposal.RowVersion + 1);
        }

        if (!string.Equals(
                expectedResponse,
                proposal.RenderedResponse,
                StringComparison.Ordinal))
        {
            const string message =
                "Die lokal gerenderte Antwort stimmt nicht mehr mit der Vorlage überein.";
            await CompleteDefiniteFailureAsync(
                    proposal,
                    "reply_integrity_changed",
                    message,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ChatReplyExecutionResult(
                ChatReplyExecutionOutcome.Failed,
                "reply_integrity_changed",
                $"{message} Die Antwort wurde nicht gesendet.",
                proposal.RowVersion + 1);
        }

        if (enteredAsReadOnly || _runtimeMode.IsReadOnly)
        {
            await _store
                .CompleteExecutionAsync(
                    proposal.ProposalId,
                    proposal.RowVersion,
                    "executed",
                    twitchResponseId: null,
                    dropCode: "read_only_not_sent",
                    dropMessage: "Read-Only: Die Antwort wurde nicht an Twitch gesendet.",
                    cancellationToken)
                .ConfigureAwait(false);
            return new ChatReplyExecutionResult(
                ChatReplyExecutionOutcome.Succeeded,
                "read_only_not_sent",
                "Read-Only: Die Chat-Antwort wurde lokal bestätigt und nicht an Twitch gesendet.",
                proposal.RowVersion + 1);
        }

        try
        {
            var sendResult = await _helixClient
                .SendChatMessageAsync(
                    proposal.BroadcasterId,
                    senderId,
                    proposal.RenderedResponse,
                    replyParentMessageId: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!sendResult.IsSent)
            {
                await _store
                    .CompleteExecutionAsync(
                        proposal.ProposalId,
                        proposal.RowVersion,
                        "failed",
                        sendResult.MessageId,
                        sendResult.DropCode,
                        sendResult.DropMessage,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new ChatReplyExecutionResult(
                    ChatReplyExecutionOutcome.Dropped,
                    sendResult.DropCode ?? "chat_message_dropped",
                    sendResult.DropMessage
                    ?? "Twitch hat die Chatnachricht nicht angenommen.",
                    proposal.RowVersion + 1);
            }

            await _store
                .CompleteExecutionAsync(
                    proposal.ProposalId,
                    proposal.RowVersion,
                    "executed",
                    sendResult.MessageId,
                    dropCode: null,
                    dropMessage: null,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ChatReplyExecutionResult(
                ChatReplyExecutionOutcome.Succeeded,
                "chat_message_sent",
                "Die konfigurierte Antwort wurde an den Twitch-Chat gesendet.",
                proposal.RowVersion + 1);
        }
        catch (TwitchHelixException exception)
        {
            await CompleteDefiniteFailureAsync(
                    proposal,
                    "twitch_chat_rejected",
                    exception.Message,
                    cancellationToken)
                .ConfigureAwait(false);
            return new ChatReplyExecutionResult(
                ChatReplyExecutionOutcome.Failed,
                "twitch_chat_rejected",
                exception.Message,
                proposal.RowVersion + 1);
        }
        catch (Exception exception) when (
            exception is HttpRequestException
                or IOException
                or OperationCanceledException)
        {
            await _store
                .CompleteExecutionAsync(
                    proposal.ProposalId,
                    proposal.RowVersion,
                    "outcome_unknown",
                    twitchResponseId: null,
                    dropCode: "transport_outcome_unknown",
                    dropMessage: exception.Message,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return new ChatReplyExecutionResult(
                ChatReplyExecutionOutcome.OutcomeUnknown,
                "transport_outcome_unknown",
                "Die Anfrage könnte Twitch erreicht haben und wird deshalb nicht automatisch wiederholt.",
                proposal.RowVersion + 1);
        }
    }

    public async Task<ChatReplyExecutionResult> DiscardAsync(
        long proposalId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        var discarded = await _store
            .DiscardAsync(proposalId, expectedRowVersion, cancellationToken)
            .ConfigureAwait(false);
        return discarded
            ? new ChatReplyExecutionResult(
                ChatReplyExecutionOutcome.Succeeded,
                "chat_reply_discarded",
                "Der Chat-Antwortvorschlag wurde verworfen.",
                expectedRowVersion + 1)
            : Rejected(
                "proposal_conflict",
                "Der Chat-Antwortvorschlag wurde bereits verarbeitet oder parallel geändert.");
    }

    private async Task CompleteDefiniteFailureAsync(
        ChatReplyProposalRecord proposal,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        await _store
            .CompleteExecutionAsync(
                proposal.ProposalId,
                proposal.RowVersion,
                "failed",
                twitchResponseId: null,
                dropCode: code,
                dropMessage: message,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static ChatReplyExecutionResult Rejected(
        string code,
        string message) =>
        new(ChatReplyExecutionOutcome.Rejected, code, message);
}
