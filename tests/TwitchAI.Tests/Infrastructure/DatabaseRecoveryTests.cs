using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure.Persistence;
using CoreChatTextNormalizer = TwitchAI.Core.Services.ChatTextNormalizer;

namespace TwitchAI.Tests.Infrastructure;

public sealed class DatabaseRecoveryTests
{
    [Fact]
    public async Task Initialize_ReconcilesInterruptedIntentAndIsIdempotentAcrossStarts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var infrastructureNormalizer =
            new TwitchAI.Infrastructure.Persistence.ChatTextNormalizer();
        var repository = new CoreModerationCaseRepository(
            database.ConnectionFactory,
            infrastructureNormalizer);
        var message = Message(
            channelId: "recovery-channel",
            messageId: "recovery-message",
            userId: "recovery-user");
        var caseId = await repository.AddAsync(
            message,
            new ValidatedModerationDecision(
                "m1",
                ModerationActionKind.Delete,
                TimeoutSeconds: null,
                PolicySetCode.General,
                "recovery proposal",
                [],
                Confidence: 0.9,
                RuleCode: ModerationRuleCodes.GeneralPhrase),
            "policy-before-crash");
        long executionId;
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE moderation_cases
                SET status = 'executing',
                    row_version = row_version + 1
                WHERE case_id = @case_id;

                INSERT INTO action_attempts(
                    case_id, broadcaster_id, twitch_message_id, target_user_id,
                    action, timeout_seconds, reason, status, requested_utc)
                VALUES(
                    @case_id, @channel_id, @message_id, @user_id,
                    'delete', NULL, 'persisted intent', 'intent', @requested_utc);

                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("@case_id", caseId);
            command.Parameters.AddWithValue("@channel_id", message.Identity.ChannelId);
            command.Parameters.AddWithValue(
                "@message_id",
                message.Identity.TwitchMessageId);
            command.Parameters.AddWithValue("@user_id", message.Identity.TwitchUserId);
            command.Parameters.AddWithValue(
                "@requested_utc",
                message.Identity.ReceivedAtUtc.ToString("O"));
            executionId = Convert.ToInt64(
                await command.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        using (var firstStartup = new DatabaseInitializer(database.ConnectionFactory))
        {
            await firstStartup.InitializeAsync();
        }

        var afterFirstStartup = await ReadRecoveryStateAsync(
            database,
            caseId,
            executionId);
        Assert.Equal("outcome_unknown", afterFirstStartup.AttemptStatus);
        Assert.Equal("process_interrupted_after_intent", afterFirstStartup.ErrorCode);
        Assert.NotNull(afterFirstStartup.CompletedAt);
        Assert.Equal("outcome_unknown", afterFirstStartup.CaseStatus);
        Assert.Equal(3, afterFirstStartup.CaseRowVersion);
        Assert.Equal(1, afterFirstStartup.AvailabilityEventCount);

        var availability = await new SqliteMessageAvailabilityStore(
                database.ConnectionFactory,
                infrastructureNormalizer)
            .AssessAsync(
                message.Identity.ChannelId,
                message.Identity.TwitchMessageId,
                message.Identity.TwitchUserId,
                message.Identity.ReceivedAtUtc);
        Assert.Equal(MessageAvailability.OutcomeUnknown, availability.Availability);
        Assert.False(availability.CanExecuteTwitchAction);

        using (var secondStartup = new DatabaseInitializer(database.ConnectionFactory))
        {
            await secondStartup.InitializeAsync();
        }

        var afterSecondStartup = await ReadRecoveryStateAsync(
            database,
            caseId,
            executionId);
        Assert.Equal(afterFirstStartup, afterSecondStartup);
    }

    private static async Task<RecoveryState> ReadRecoveryStateAsync(
        TestDatabase database,
        long caseId,
        long executionId)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT a.status, a.error_code, a.completed_utc,
                   c.status, c.row_version,
                   (SELECT COUNT(*)
                    FROM message_availability_events e
                    WHERE e.eventsub_message_id = @recovery_event_id
                      AND e.availability = 'outcome_unknown'
                      AND e.correlation_kind = 'local_or_ambiguous'
                      AND e.case_id = @case_id)
            FROM action_attempts a
            INNER JOIN moderation_cases c ON c.case_id = a.case_id
            WHERE a.execution_id = @execution_id;
            """;
        command.Parameters.AddWithValue("@case_id", caseId);
        command.Parameters.AddWithValue("@execution_id", executionId);
        command.Parameters.AddWithValue(
            "@recovery_event_id",
            $"local-recovery:{executionId}");
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new RecoveryState(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetInt32(5));
    }

    private static ChatMessageSnapshot Message(
        string channelId,
        string messageId,
        string userId)
    {
        var receivedAt = DateTimeOffset.UtcNow;
        const string original = "message interrupted after intent";
        var identity = ChatMessageIdentityV1.Create(
            "recovery-event",
            messageId,
            channelId,
            userId,
            "viewer",
            "Viewer",
            original,
            receivedAt);
        return new ChatMessageSnapshot(
            identity,
            new CoreChatTextNormalizer().Process(original),
            new ChannelContextSnapshot(
                channelId,
                EffectivePolicyResolver.BootstrapJustChattingCategoryId,
                "Just Chatting",
                receivedAt,
                Revision: 1,
                IsVerified: true),
            IngressSequence: 1);
    }

    private sealed record RecoveryState(
        string AttemptStatus,
        string ErrorCode,
        string? CompletedAt,
        string CaseStatus,
        long CaseRowVersion,
        int AvailabilityEventCount);
}
