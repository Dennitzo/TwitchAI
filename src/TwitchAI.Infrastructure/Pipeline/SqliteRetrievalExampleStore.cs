using TwitchAI.Core.Models;
using TwitchAI.Infrastructure.Persistence;
using CoreEffectivePolicySnapshot = TwitchAI.Core.Models.EffectivePolicySnapshot;

namespace TwitchAI.Infrastructure.Pipeline;

public interface IRetrievalExampleStore
{
    ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantAsync(
        string channelId,
        string? categoryId,
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken = default);
}

public sealed class SqliteRetrievalExampleStore(ISqliteConnectionFactory connectionFactory)
    : IRetrievalExampleStore
{
    public async ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantAsync(
        string channelId,
        string? categoryId,
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentNullException.ThrowIfNull(policy);

        await using var connection = await connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.lower_text,
                   CASE m.operator_decision
                       WHEN 'discarded' THEN 'none'
                       ELSE m.actual_action
                   END,
                   COALESCE(m.actual_timeout_seconds, c.proposed_timeout_seconds),
                   c.policy_set,
                   COALESCE(m.actual_reason, m.discard_reason, c.reason),
                   CASE m.operator_decision
                       WHEN 'executed' THEN 1
                       ELSE 0
                   END,
                   COALESCE(m.rule_code, c.violation_rule_code)
            FROM moderation_feedback m
            INNER JOIN moderation_cases c ON c.case_id = m.case_id
            INNER JOIN chat_message_snapshots s
                ON s.twitch_message_id = c.twitch_message_id
            WHERE m.eligible_for_learning = 1
              AND m.review_completed = 1
              AND (c.category_id IS NULL OR c.category_id = @category_id)
            ORDER BY m.decided_utc DESC
            LIMIT 24;
            """;
        command.Parameters.AddWithValue("@category_id", SqliteData.DbValue(categoryId));

        var examples = new List<RetrievalExample>(24);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var policySet = ParsePolicy(reader.GetString(3));
            if (!policy.Contains(policySet))
            {
                continue;
            }

            var action = ParseAction(reader.GetString(1));
            var ruleCode = reader.IsDBNull(6) ? null : reader.GetString(6);
            if (ruleCode is not null && policy.FindRule(ruleCode) is null)
            {
                continue;
            }

            examples.Add(
                new RetrievalExample(
                    reader.GetString(0).ToLowerInvariant(),
                    action,
                    action == ModerationActionKind.Timeout && !reader.IsDBNull(2)
                        ? reader.GetInt32(2)
                        : null,
                    policySet,
                    reader.GetString(4),
                    reader.GetBoolean(5),
                    ruleCode));
        }

        return examples;
    }

    private static ModerationActionKind ParseAction(string value)
    {
        return value switch
        {
            "none" => ModerationActionKind.None,
            "delete" => ModerationActionKind.Delete,
            "timeout" => ModerationActionKind.Timeout,
            _ => throw new InvalidDataException($"Unsupported feedback action '{value}'."),
        };
    }

    private static PolicySetCode ParsePolicy(string value)
    {
        return value switch
        {
            "general" => PolicySetCode.General,
            "backseatgaming" => PolicySetCode.Backseatgaming,
            _ => throw new InvalidDataException($"Unsupported feedback policy '{value}'."),
        };
    }
}
