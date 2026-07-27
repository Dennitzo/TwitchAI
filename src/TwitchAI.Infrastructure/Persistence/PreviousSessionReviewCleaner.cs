namespace TwitchAI.Infrastructure.Persistence;

public interface IPreviousSessionReviewCleaner
{
    Task<int> CloseAsync(
        DateTimeOffset closedAtUtc,
        CancellationToken cancellationToken = default);
}

public sealed class PreviousSessionReviewCleaner : IPreviousSessionReviewCleaner
{
    private const string RestartReason =
        "Beim App-Neustart automatisch verworfen.";

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;

    public PreviousSessionReviewCleaner(
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task<int> CloseAsync(
        DateTimeOffset closedAtUtc,
        CancellationToken cancellationToken = default)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var timestamp = SqliteData.ToDatabaseTimestamp(closedAtUtc);

        await using (var feedback = connection.CreateCommand())
        {
            feedback.Transaction = transaction;
            feedback.CommandText =
                """
                INSERT OR IGNORE INTO moderation_feedback(
                    case_id, operator_decision, actual_action,
                    actual_timeout_seconds, actual_reason, discard_reason, rule_code,
                    eligible_for_learning, human_approved, twitch_executed,
                    learning_accepted, review_completed, decided_utc)
                SELECT
                    case_id, 'discarded', 'none',
                    NULL, NULL, @reason, violation_rule_code,
                    0, 0, 0,
                    0, 1, @closed_utc
                FROM moderation_cases
                WHERE status = 'pending';
                """;
            feedback.Parameters.AddWithValue("@reason", RestartReason);
            feedback.Parameters.AddWithValue("@closed_utc", timestamp);
            await feedback.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        int closedPending;
        await using (var pending = connection.CreateCommand())
        {
            pending.Transaction = transaction;
            pending.CommandText =
                """
                UPDATE moderation_cases
                SET status = 'discarded',
                    row_version = row_version + 1,
                    resolved_utc = @closed_utc
                WHERE status = 'pending';
                """;
            pending.Parameters.AddWithValue("@closed_utc", timestamp);
            closedPending = await pending.ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        int closedExternalReviews;
        await using (var external = connection.CreateCommand())
        {
            external.Transaction = transaction;
            external.CommandText =
                """
                UPDATE moderation_feedback
                SET review_completed = 1,
                    eligible_for_learning = 0,
                    learning_accepted = 0,
                    rule_code = COALESCE(
                        rule_code,
                        (SELECT violation_rule_code
                         FROM moderation_cases c
                         WHERE c.case_id = moderation_feedback.case_id)),
                    discard_reason = COALESCE(discard_reason, @reason),
                    decided_utc = @closed_utc
                WHERE review_completed = 0
                  AND case_id IN (
                      SELECT case_id
                      FROM moderation_cases
                      WHERE status = 'externally_completed'
                  );
                """;
            external.Parameters.AddWithValue("@reason", RestartReason);
            external.Parameters.AddWithValue("@closed_utc", timestamp);
            closedExternalReviews = await external
                .ExecuteNonQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return closedPending + closedExternalReviews;
    }
}
