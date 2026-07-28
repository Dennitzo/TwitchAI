using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using TwitchAI.Core.Models;

namespace TwitchAI.Infrastructure.Persistence;

public sealed record ChatReplyTemplateRecord(
    long TemplateId,
    long RowVersion,
    string ExampleMessage,
    string NormalizedExample,
    string ResponseMessage,
    bool IsEnabled,
    int HitCount,
    DateTimeOffset? LastHitAt);

public sealed record ChatReplyProposalRecord(
    long ProposalId,
    long RowVersion,
    string SourceMessageId,
    string BroadcasterId,
    string ChatterUserId,
    string ChatterLogin,
    string ChatterDisplayName,
    string OriginalMessage,
    string IdentityFingerprint,
    string? CategoryId,
    string CategoryName,
    DateTimeOffset MessageReceivedAt,
    long? TemplateId,
    long TemplateRowVersion,
    string ConfiguredResponse,
    string RenderedResponse,
    double Confidence,
    Guid BatchId,
    long? AiLatencyMilliseconds,
    string Status,
    string? TwitchResponseId,
    string? TwitchDropCode,
    string? TwitchDropMessage);

public sealed record ChatReplyProposalAddResult(
    ChatReplyProposalRecord Proposal,
    bool IsCreated);

public interface IChatReplyStore
{
    Task<IReadOnlyList<ChatReplyTemplateRecord>> LoadTemplatesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatReplyTemplateRecord>> LoadActiveTemplatesAsync(
        CancellationToken cancellationToken = default);

    Task<long> AddTemplateAsync(
        string exampleMessage,
        string responseMessage,
        CancellationToken cancellationToken = default);

    Task<long> UpdateTemplateAsync(
        long templateId,
        long expectedRowVersion,
        string exampleMessage,
        string responseMessage,
        CancellationToken cancellationToken = default);

    Task<long> SetTemplateEnabledAsync(
        long templateId,
        long expectedRowVersion,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<ChatReplyProposalAddResult?> AddProposalAsync(
        ChatMessageSnapshot message,
        ChatReplyTemplateRecord replyTemplate,
        string renderedResponse,
        double confidence,
        Guid batchId,
        long? aiLatencyMilliseconds,
        CancellationToken cancellationToken = default);

    Task<ChatReplyProposalRecord?> TryBeginExecutionAsync(
        long proposalId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default);

    Task<long?> ReturnToPendingAsync(
        long proposalId,
        long expectedRowVersion,
        string? dropCode,
        string? dropMessage,
        CancellationToken cancellationToken = default);

    Task<bool> CompleteExecutionAsync(
        long proposalId,
        long expectedRowVersion,
        string status,
        string? twitchResponseId,
        string? dropCode,
        string? dropMessage,
        CancellationToken cancellationToken = default);

    Task<bool> DiscardAsync(
        long proposalId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default);
}

public sealed class SqliteChatReplyStore : IChatReplyStore
{
    public const string GlobalBroadcasterId = "*";

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;

    public SqliteChatReplyStore(
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task<IReadOnlyList<ChatReplyTemplateRecord>> LoadTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT template_id, row_version, example_message, normalized_example,
                   response_message, enabled, hit_count, last_hit_utc
            FROM chat_reply_templates
            WHERE broadcaster_id = @broadcaster_id
            ORDER BY template_id DESC;
            """;
        command.Parameters.AddWithValue("@broadcaster_id", GlobalBroadcasterId);
        return await ReadTemplatesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ChatReplyTemplateRecord>> LoadActiveTemplatesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT template_id, row_version, example_message, normalized_example,
                   response_message, enabled, hit_count, last_hit_utc
            FROM chat_reply_templates
            WHERE broadcaster_id = @broadcaster_id
              AND enabled = 1
            ORDER BY template_id;
            """;
        command.Parameters.AddWithValue("@broadcaster_id", GlobalBroadcasterId);
        return await ReadTemplatesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> AddTemplateAsync(
        string exampleMessage,
        string responseMessage,
        CancellationToken cancellationToken = default)
    {
        ValidateTemplate(exampleMessage, responseMessage);
        var now = SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow);
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO chat_reply_templates(
                broadcaster_id, example_message, normalized_example,
                response_message, enabled, hit_count, row_version,
                created_utc, updated_utc)
            VALUES(
                @broadcaster_id, @example_message, @normalized_example,
                @response_message, 1, 0, 1, @now, @now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@broadcaster_id", GlobalBroadcasterId);
        command.Parameters.AddWithValue("@example_message", exampleMessage);
        command.Parameters.AddWithValue(
            "@normalized_example",
            NormalizeExample(exampleMessage));
        command.Parameters.AddWithValue("@response_message", responseMessage);
        command.Parameters.AddWithValue("@now", now);
        try
        {
            return Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                "Diese Beispielphrase existiert bereits im Chat-Reiter.",
                exception);
        }
    }

    public Task<long> UpdateTemplateAsync(
        long templateId,
        long expectedRowVersion,
        string exampleMessage,
        string responseMessage,
        CancellationToken cancellationToken = default)
    {
        ValidateTemplate(exampleMessage, responseMessage);
        return UpdateTemplateCoreAsync(
            templateId,
            expectedRowVersion,
            """
            example_message = @example_message,
            normalized_example = @normalized_example,
            response_message = @response_message,
            """,
            command =>
            {
                command.Parameters.AddWithValue("@example_message", exampleMessage);
                command.Parameters.AddWithValue(
                    "@normalized_example",
                    NormalizeExample(exampleMessage));
                command.Parameters.AddWithValue("@response_message", responseMessage);
            },
            cancellationToken);
    }

    public Task<long> SetTemplateEnabledAsync(
        long templateId,
        long expectedRowVersion,
        bool isEnabled,
        CancellationToken cancellationToken = default) =>
        UpdateTemplateCoreAsync(
            templateId,
            expectedRowVersion,
            "enabled = @enabled,",
            command => command.Parameters.AddWithValue("@enabled", isEnabled ? 1 : 0),
            cancellationToken);

    public async Task<ChatReplyProposalAddResult?> AddProposalAsync(
        ChatMessageSnapshot message,
        ChatReplyTemplateRecord replyTemplate,
        string renderedResponse,
        double confidence,
        Guid batchId,
        long? aiLatencyMilliseconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(replyTemplate);
        ValidateResponse(renderedResponse);
        if (confidence is < 0 or > 1 || double.IsNaN(confidence))
        {
            throw new ArgumentOutOfRangeException(nameof(confidence));
        }

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        await using (var templateCommand = connection.CreateCommand())
        {
            templateCommand.Transaction = transaction;
            templateCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM chat_reply_templates
                WHERE template_id = @template_id
                  AND row_version = @row_version
                  AND enabled = 1;
                """;
            templateCommand.Parameters.AddWithValue("@template_id", replyTemplate.TemplateId);
            templateCommand.Parameters.AddWithValue("@row_version", replyTemplate.RowVersion);
            var isCurrent = Convert.ToInt64(
                await templateCommand.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false),
                CultureInfo.InvariantCulture) == 1;
            if (!isCurrent)
            {
                return null;
            }
        }

        var now = SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow);
        long proposalId;
        var isCreated = false;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT OR IGNORE INTO chat_reply_proposals(
                    source_message_id, broadcaster_id, chatter_user_id,
                    chatter_login, chatter_display_name, original_message,
                    identity_fingerprint, category_id, category_name,
                    message_received_utc, template_id, template_row_version,
                    configured_response, rendered_response, confidence,
                    batch_id, ai_latency_ms, status, row_version, created_utc)
                VALUES(
                    @source_message_id, @broadcaster_id, @chatter_user_id,
                    @chatter_login, @chatter_display_name, @original_message,
                    @identity_fingerprint, @category_id, @category_name,
                    @message_received_utc, @template_id, @template_row_version,
                    @configured_response, @rendered_response, @confidence,
                    @batch_id, @ai_latency_ms, 'pending', 1, @created_utc);
                """;
            var identity = message.Identity;
            insert.Parameters.AddWithValue("@source_message_id", identity.TwitchMessageId);
            insert.Parameters.AddWithValue("@broadcaster_id", identity.ChannelId);
            insert.Parameters.AddWithValue("@chatter_user_id", identity.TwitchUserId);
            insert.Parameters.AddWithValue("@chatter_login", identity.Login);
            insert.Parameters.AddWithValue("@chatter_display_name", identity.DisplayName);
            insert.Parameters.AddWithValue("@original_message", identity.OriginalText);
            insert.Parameters.AddWithValue(
                "@identity_fingerprint",
                identity.IdentityFingerprint);
            insert.Parameters.AddWithValue(
                "@category_id",
                SqliteData.DbValue(message.ChannelContext.CategoryId));
            insert.Parameters.AddWithValue(
                "@category_name",
                message.ChannelContext.CategoryDisplayName);
            insert.Parameters.AddWithValue(
                "@message_received_utc",
                SqliteData.ToDatabaseTimestamp(identity.ReceivedAtUtc));
            insert.Parameters.AddWithValue("@template_id", replyTemplate.TemplateId);
            insert.Parameters.AddWithValue("@template_row_version", replyTemplate.RowVersion);
            insert.Parameters.AddWithValue(
                "@configured_response",
                replyTemplate.ResponseMessage);
            insert.Parameters.AddWithValue("@rendered_response", renderedResponse);
            insert.Parameters.AddWithValue("@confidence", confidence);
            insert.Parameters.AddWithValue("@batch_id", batchId.ToString("D"));
            insert.Parameters.AddWithValue(
                "@ai_latency_ms",
                SqliteData.DbValue(aiLatencyMilliseconds));
            insert.Parameters.AddWithValue("@created_utc", now);
            isCreated =
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }

        await using (var idCommand = connection.CreateCommand())
        {
            idCommand.Transaction = transaction;
            idCommand.CommandText =
                """
                SELECT proposal_id
                FROM chat_reply_proposals
                WHERE source_message_id = @source_message_id;
                """;
            idCommand.Parameters.AddWithValue(
                "@source_message_id",
                message.Identity.TwitchMessageId);
            proposalId = Convert.ToInt64(
                await idCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        if (isCreated)
        {
            await using var hit = connection.CreateCommand();
            hit.Transaction = transaction;
            hit.CommandText =
                """
                UPDATE chat_reply_templates
                SET hit_count = hit_count + 1,
                    last_hit_utc = @last_hit_utc
                WHERE template_id = @template_id;
                """;
            hit.Parameters.AddWithValue("@last_hit_utc", now);
            hit.Parameters.AddWithValue("@template_id", replyTemplate.TemplateId);
            await hit.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var proposal = await ReadProposalAsync(
                connection,
                transaction,
                proposalId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return proposal is null
            ? null
            : new ChatReplyProposalAddResult(proposal, isCreated);
    }

    public async Task<ChatReplyProposalRecord?> TryBeginExecutionAsync(
        long proposalId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE chat_reply_proposals
            SET status = 'executing',
                row_version = row_version + 1
            WHERE proposal_id = @proposal_id
              AND row_version = @row_version
              AND status = 'pending';
            """;
        command.Parameters.AddWithValue("@proposal_id", proposalId);
        command.Parameters.AddWithValue("@row_version", expectedRowVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var proposal = await ReadProposalAsync(
                connection,
                transaction,
                proposalId,
                cancellationToken)
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return proposal;
    }

    public async Task<long?> ReturnToPendingAsync(
        long proposalId,
        long expectedRowVersion,
        string? dropCode,
        string? dropMessage,
        CancellationToken cancellationToken = default)
    {
        var updated = await UpdateProposalStatusAsync(
                proposalId,
                expectedRowVersion,
                "pending",
                twitchResponseId: null,
                dropCode,
                dropMessage,
                resolvedAt: null,
                cancellationToken)
            .ConfigureAwait(false);
        return updated ? expectedRowVersion + 1 : null;
    }

    public Task<bool> CompleteExecutionAsync(
        long proposalId,
        long expectedRowVersion,
        string status,
        string? twitchResponseId,
        string? dropCode,
        string? dropMessage,
        CancellationToken cancellationToken = default)
    {
        if (status is not ("executed" or "failed" or "outcome_unknown"))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return UpdateProposalStatusAsync(
            proposalId,
            expectedRowVersion,
            status,
            twitchResponseId,
            dropCode,
            dropMessage,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public Task<bool> DiscardAsync(
        long proposalId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default) =>
        UpdateProposalStatusAsync(
            proposalId,
            expectedRowVersion,
            "discarded",
            twitchResponseId: null,
            dropCode: null,
            dropMessage: null,
            DateTimeOffset.UtcNow,
            cancellationToken);

    public static string RenderResponse(string configuredResponse, string twitchLogin)
    {
        ValidateResponse(configuredResponse);
        if (string.IsNullOrWhiteSpace(twitchLogin))
        {
            throw new ArgumentException(
                "Der Twitch-Login für @Username fehlt.",
                nameof(twitchLogin));
        }

        var rendered = configuredResponse.Replace(
            "@Username",
            $"@{twitchLogin}",
            StringComparison.Ordinal);
        ValidateResponse(rendered);
        return rendered;
    }

    public static string NormalizeExample(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
    }

    private async Task<long> UpdateTemplateCoreAsync(
        long templateId,
        long expectedRowVersion,
        string assignments,
        Action<SqliteCommand> addParameters,
        CancellationToken cancellationToken)
    {
        if (templateId <= 0 || expectedRowVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(templateId));
        }

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             UPDATE chat_reply_templates
             SET {assignments}
                 row_version = row_version + 1,
                 updated_utc = @updated_utc
             WHERE template_id = @template_id
               AND row_version = @row_version;
             """;
        addParameters(command);
        command.Parameters.AddWithValue("@template_id", templateId);
        command.Parameters.AddWithValue("@row_version", expectedRowVersion);
        command.Parameters.AddWithValue(
            "@updated_utc",
            SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException(
                    "Die Chat-Zeile wurde parallel geändert. Bitte Tabelle neu laden.");
            }
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException(
                "Diese Beispielphrase existiert bereits im Chat-Reiter.",
                exception);
        }

        return expectedRowVersion + 1;
    }

    private async Task<bool> UpdateProposalStatusAsync(
        long proposalId,
        long expectedRowVersion,
        string status,
        string? twitchResponseId,
        string? dropCode,
        string? dropMessage,
        DateTimeOffset? resolvedAt,
        CancellationToken cancellationToken)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE chat_reply_proposals
            SET status = @status,
                twitch_response_id = @twitch_response_id,
                twitch_drop_code = @twitch_drop_code,
                twitch_drop_message = @twitch_drop_message,
                resolved_utc = @resolved_utc,
                row_version = row_version + 1
            WHERE proposal_id = @proposal_id
              AND row_version = @row_version
              AND status IN ('pending','executing','failed');
            """;
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue(
            "@twitch_response_id",
            SqliteData.DbValue(twitchResponseId));
        command.Parameters.AddWithValue("@twitch_drop_code", SqliteData.DbValue(dropCode));
        command.Parameters.AddWithValue(
            "@twitch_drop_message",
            SqliteData.DbValue(dropMessage));
        command.Parameters.AddWithValue(
            "@resolved_utc",
            resolvedAt is null
                ? DBNull.Value
                : SqliteData.ToDatabaseTimestamp(resolvedAt.Value));
        command.Parameters.AddWithValue("@proposal_id", proposalId);
        command.Parameters.AddWithValue("@row_version", expectedRowVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task<IReadOnlyList<ChatReplyTemplateRecord>> ReadTemplatesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new List<ChatReplyTemplateRecord>();
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(
                new ChatReplyTemplateRecord(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt64(5) == 1,
                    reader.GetInt32(6),
                    SqliteData.GetNullableDateTimeOffset(reader, 7)));
        }

        return result;
    }

    private static async Task<ChatReplyProposalRecord?> ReadProposalAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long proposalId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT proposal_id, row_version, source_message_id, broadcaster_id,
                   chatter_user_id, chatter_login, chatter_display_name,
                   original_message, identity_fingerprint, category_id,
                   category_name, message_received_utc, template_id,
                   template_row_version, configured_response, rendered_response,
                   confidence, batch_id, ai_latency_ms, status,
                   twitch_response_id, twitch_drop_code, twitch_drop_message
            FROM chat_reply_proposals
            WHERE proposal_id = @proposal_id;
            """;
        command.Parameters.AddWithValue("@proposal_id", proposalId);
        await using var reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ChatReplyProposalRecord(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetString(10),
            SqliteData.GetDateTimeOffset(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetDouble(16),
            Guid.Parse(reader.GetString(17)),
            reader.IsDBNull(18) ? null : reader.GetInt64(18),
            reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetString(21),
            reader.IsDBNull(22) ? null : reader.GetString(22));
    }

    private static void ValidateTemplate(
        string exampleMessage,
        string responseMessage)
    {
        ArgumentNullException.ThrowIfNull(exampleMessage);
        if (string.IsNullOrWhiteSpace(exampleMessage)
            || exampleMessage.Length > 500)
        {
            throw new ArgumentException(
                "Die Chatnachricht muss 1 bis 500 Zeichen enthalten.",
                nameof(exampleMessage));
        }

        ValidateResponse(responseMessage);
    }

    private static void ValidateResponse(string responseMessage)
    {
        ArgumentNullException.ThrowIfNull(responseMessage);
        if (string.IsNullOrWhiteSpace(responseMessage)
            || responseMessage.Length > 500)
        {
            throw new ArgumentException(
                "Die Antwortnachricht muss 1 bis 500 Zeichen enthalten.",
                nameof(responseMessage));
        }
    }
}
