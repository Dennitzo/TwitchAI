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

    ValueTask<IReadOnlyList<RetrievalExample>> GetRelevantForLearningTestAsync(
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken = default) =>
        GetRelevantAsync(
            GlobalPolicyStorage.BroadcasterId,
            categoryId: null,
            policy,
            cancellationToken);
}

public sealed class SqliteRetrievalExampleStore(ISqliteConnectionFactory connectionFactory)
    : IRetrievalExampleStore
{
    private const int MaximumCandidatesPerSource = 12;

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
        var manual = await ReadManualExamplesAsync(
                connection,
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        var twitch = await ReadTwitchExamplesAsync(
                connection,
                categoryId,
                includeAllCategories: false,
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        return Interleave(manual, twitch);
    }

    public async ValueTask<IReadOnlyList<RetrievalExample>>
        GetRelevantForLearningTestAsync(
            CoreEffectivePolicySnapshot policy,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        await using var connection = await connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var manual = await ReadManualExamplesAsync(
                connection,
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        var twitch = await ReadTwitchExamplesAsync(
                connection,
                categoryId: null,
                includeAllCategories: true,
                policy,
                cancellationToken)
            .ConfigureAwait(false);
        return Interleave(manual, twitch);
    }

    private static async Task<IReadOnlyList<RetrievalExample>> ReadTwitchExamplesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string? categoryId,
        bool includeAllCategories,
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken)
    {
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
              AND (
                  @all_categories = 1
                  OR c.category_id IS NULL
                  OR c.category_id = @category_id)
            ORDER BY m.decided_utc DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@category_id", SqliteData.DbValue(categoryId));
        command.Parameters.AddWithValue(
            "@all_categories",
            includeAllCategories ? 1 : 0);
        command.Parameters.AddWithValue("@limit", MaximumCandidatesPerSource);

        return await ReadExamplesAsync(command, policy, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<RetrievalExample>> ReadManualExamplesAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT lower_text,
                   actual_action,
                   actual_timeout_seconds,
                   actual_policy_set,
                   actual_reason,
                   CASE final_decision
                       WHEN 'confirmed_violation' THEN 1
                       ELSE 0
                   END,
                   actual_rule_code
            FROM manual_learning_examples
            WHERE eligible_for_learning = 1
            ORDER BY created_utc DESC, manual_example_id DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", MaximumCandidatesPerSource);

        return await ReadExamplesAsync(command, policy, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<RetrievalExample>> ReadExamplesAsync(
        Microsoft.Data.Sqlite.SqliteCommand command,
        CoreEffectivePolicySnapshot policy,
        CancellationToken cancellationToken)
    {
        var examples = new List<RetrievalExample>(MaximumCandidatesPerSource);
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

    private static List<RetrievalExample> Interleave(
        IReadOnlyList<RetrievalExample> manual,
        IReadOnlyList<RetrievalExample> twitch)
    {
        var result = new List<RetrievalExample>(manual.Count + twitch.Count);
        var maximum = Math.Max(manual.Count, twitch.Count);
        for (var index = 0; index < maximum; index++)
        {
            if (index < manual.Count)
            {
                result.Add(manual[index]);
            }

            if (index < twitch.Count)
            {
                result.Add(twitch[index]);
            }
        }

        return result;
    }

    private static ModerationActionKind ParseAction(string value)
    {
        return value switch
        {
            "none" => ModerationActionKind.None,
            "delete" => ModerationActionKind.Delete,
            "timeout" => ModerationActionKind.Timeout,
            "ban" => ModerationActionKind.Ban,
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
