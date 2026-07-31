using Microsoft.Data.Sqlite;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;

namespace TwitchAI.Infrastructure.Persistence;

public sealed class SqliteLearningDatasetService : ILearningDatasetService
{
    private const int MaximumPageSize = 500;

    internal const string EffectiveExamplesCte =
        """
        WITH base_examples AS (
            SELECT
                'twitch_moderation' AS source_kind,
                moderation.case_id AS source_id,
                snapshot.message_text_original AS original_text,
                snapshot.lower_text AS lower_text,
                CASE feedback.operator_decision
                    WHEN 'executed' THEN 'moderate'
                    ELSE 'allow'
                END AS learning_decision,
                moderation.policy_set AS policy_set,
                COALESCE(
                    feedback.rule_code,
                    moderation.violation_rule_code,
                    CASE moderation.policy_set
                        WHEN 'backseatgaming' THEN 'BACKSEATGAMING'
                        ELSE 'G_OTHER_ABUSE'
                    END) AS rule_code,
                CASE feedback.operator_decision
                    WHEN 'executed' THEN feedback.actual_action
                    ELSE 'none'
                END AS action,
                CASE feedback.operator_decision
                    WHEN 'executed' THEN feedback.actual_timeout_seconds
                    ELSE NULL
                END AS timeout_seconds,
                CASE feedback.operator_decision
                    WHEN 'executed' THEN COALESCE(
                        feedback.actual_reason,
                        moderation.reason,
                        '')
                    ELSE COALESCE(feedback.discard_reason, '')
                END AS reason,
                CASE
                    WHEN feedback.operator_decision = 'discarded'
                        THEN 'Live · als erlaubt markiert'
                    WHEN feedback.twitch_executed = 1
                        THEN 'Twitch-Moderation'
                    WHEN moderation.status = 'externally_completed'
                        THEN 'Externe Moderation'
                    ELSE 'Read-Only'
                END AS source_label,
                feedback.decided_utc AS occurred_utc,
                moderation.category_id AS category_id
            FROM moderation_feedback feedback
            INNER JOIN moderation_cases moderation
                ON moderation.case_id = feedback.case_id
            INNER JOIN chat_message_snapshots snapshot
                ON snapshot.twitch_message_id = moderation.twitch_message_id
            WHERE feedback.eligible_for_learning = 1
              AND feedback.learning_accepted = 1
              AND feedback.human_approved = 1
              AND feedback.review_completed = 1

            UNION ALL

            SELECT
                'manual_test' AS source_kind,
                example.manual_example_id AS source_id,
                example.message_text_original AS original_text,
                example.lower_text AS lower_text,
                CASE example.final_decision
                    WHEN 'confirmed_violation' THEN 'moderate'
                    ELSE 'allow'
                END AS learning_decision,
                example.actual_policy_set AS policy_set,
                example.actual_rule_code AS rule_code,
                example.actual_action AS action,
                example.actual_timeout_seconds AS timeout_seconds,
                CASE example.final_decision
                    WHEN 'marked_allowed' THEN 'Menschlich als erlaubt markiert.'
                    ELSE example.actual_reason
                END AS reason,
                'Lokaler Lerntest' AS source_label,
                example.created_utc AS occurred_utc,
                NULL AS category_id
            FROM manual_learning_examples example
            WHERE example.eligible_for_learning = 1
        ),
        effective_examples AS (
            SELECT
                base.source_kind,
                base.source_id,
                CASE WHEN edited.source_id IS NULL
                    THEN base.original_text ELSE edited.original_text END
                    AS original_text,
                CASE WHEN edited.source_id IS NULL
                    THEN base.lower_text ELSE edited.lower_text END
                    AS lower_text,
                CASE WHEN edited.source_id IS NULL
                    THEN base.learning_decision ELSE edited.learning_decision END
                    AS learning_decision,
                CASE WHEN edited.source_id IS NULL
                    THEN base.policy_set ELSE edited.policy_set END
                    AS policy_set,
                CASE WHEN edited.source_id IS NULL
                    THEN base.rule_code ELSE edited.rule_code END
                    AS rule_code,
                CASE WHEN edited.source_id IS NULL
                    THEN base.action ELSE edited.action END
                    AS action,
                CASE WHEN edited.source_id IS NULL
                    THEN base.timeout_seconds ELSE edited.timeout_seconds END
                    AS timeout_seconds,
                CASE WHEN edited.source_id IS NULL
                    THEN base.reason ELSE edited.reason END
                    AS reason,
                base.source_label,
                base.occurred_utc,
                base.category_id,
                COALESCE(edited.is_removed, 0) AS is_removed,
                COALESCE(edited.row_version, 0) AS row_version,
                COALESCE(edited.updated_utc, base.occurred_utc) AS effective_utc
            FROM base_examples base
            LEFT JOIN learning_example_overrides edited
              ON edited.source_kind = base.source_kind
             AND edited.source_id = base.source_id
        )
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly ILearningContextRevision _revision;

    public SqliteLearningDatasetService(
        ISqliteConnectionFactory connectionFactory,
        ILearningContextRevision revision,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _revision = revision;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async ValueTask<LearningDatasetPage> QueryAsync(
        LearningDatasetQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateQuery(query);

        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        var whereClause =
            """
            WHERE is_removed = 0
              AND (@decision IS NULL OR learning_decision = @decision)
              AND (@policy_set IS NULL OR policy_set = @policy_set)
              AND (
                  @search = ''
                  OR instr(lower(original_text), @search) > 0
                  OR instr(lower_text, @search) > 0
                  OR instr(lower(rule_code), @search) > 0
                  OR instr(lower(reason), @search) > 0
                  OR instr(lower(source_label), @search) > 0)
            """;

        var totalCount = 0;
        await using (var count = connection.CreateCommand())
        {
            count.CommandText = EffectiveExamplesCte +
                "\nSELECT COUNT(*) FROM effective_examples\n" + whereClause;
            AddQueryParameters(count, query);
            totalCount = Convert.ToInt32(
                await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = EffectiveExamplesCte + "\n" +
            """
            SELECT source_kind, source_id, original_text, lower_text,
                   learning_decision, policy_set, rule_code, action,
                   timeout_seconds, reason, source_label, occurred_utc,
                   row_version
            FROM effective_examples
            """ + "\n" + whereClause +
            $" ORDER BY {ResolveSortExpression(query.SortColumn)} " +
            (query.Descending ? "DESC" : "ASC") +
            ", source_kind ASC, source_id DESC LIMIT @page_size OFFSET @offset;";
        AddQueryParameters(command, query);
        command.Parameters.AddWithValue("@page_size", query.PageSize);
        command.Parameters.AddWithValue("@offset", query.Offset);

        var rows = new List<LearningDatasetRow>(Math.Min(query.PageSize, totalCount));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(ReadRow(reader));
        }

        return new LearningDatasetPage(rows, totalCount);
    }

    public async ValueTask<LearningDatasetRow> UpdateAsync(
        LearningSourceKey source,
        long expectedRowVersion,
        LearningDatasetDraft draft,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(source);
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);

        var normalized = ValidateAndNormalize(draft);
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var current = await ReadEffectiveByKeyAsync(
                connection,
                transaction,
                source,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Learning example '{source.DisplayId}' does not exist or is not eligible.");
        EnsureExpectedVersion(source, expectedRowVersion, current.RowVersion);
        await EnsureActiveRuleAsync(
                connection,
                transaction,
                normalized.PolicySet,
                normalized.RuleCode,
                cancellationToken)
            .ConfigureAwait(false);

        var now = SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow);
        var newVersion = expectedRowVersion + 1;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = expectedRowVersion == 0
                ?
                """
                INSERT INTO learning_example_overrides(
                    source_kind, source_id, original_text, lower_text,
                    learning_decision, policy_set, rule_code, action,
                    timeout_seconds, reason, is_removed, row_version,
                    created_utc, updated_utc)
                VALUES(
                    @source_kind, @source_id, @original_text, @lower_text,
                    @decision, @policy_set, @rule_code, @action,
                    @timeout_seconds, @reason, 0, 1, @now, @now);
                """
                :
                """
                UPDATE learning_example_overrides
                SET original_text = @original_text,
                    lower_text = @lower_text,
                    learning_decision = @decision,
                    policy_set = @policy_set,
                    rule_code = @rule_code,
                    action = @action,
                    timeout_seconds = @timeout_seconds,
                    reason = @reason,
                    is_removed = 0,
                    row_version = row_version + 1,
                    updated_utc = @now
                WHERE source_kind = @source_kind
                  AND source_id = @source_id
                  AND row_version = @expected_version;
                """;
            AddOverrideParameters(command, source, normalized, now);
            command.Parameters.AddWithValue("@expected_version", expectedRowVersion);
            try
            {
                if (await command.ExecuteNonQueryAsync(cancellationToken)
                        .ConfigureAwait(false) != 1)
                {
                    throw ConcurrencyException(source);
                }
            }
            catch (SqliteException exception) when (
                expectedRowVersion == 0 && exception.SqliteErrorCode == 19)
            {
                throw ConcurrencyException(source, exception);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _revision.Advance();
        return new LearningDatasetRow(
            source,
            source.DisplayId,
            normalized.OriginalText,
            normalized.OriginalText.ToLowerInvariant(),
            normalized.Decision,
            normalized.PolicySet,
            normalized.RuleCode,
            normalized.Action,
            normalized.TimeoutSeconds,
            normalized.Reason,
            current.SourceLabel,
            current.OccurredAtUtc,
            newVersion);
    }

    public async ValueTask RemoveAsync(
        LearningSourceKey source,
        long expectedRowVersion,
        CancellationToken cancellationToken = default)
    {
        ValidateSource(source);
        ArgumentOutOfRangeException.ThrowIfNegative(expectedRowVersion);

        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var current = await ReadEffectiveByKeyAsync(
                connection,
                transaction,
                source,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException(
                $"Learning example '{source.DisplayId}' does not exist or is not eligible.");
        EnsureExpectedVersion(source, expectedRowVersion, current.RowVersion);

        var now = SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = expectedRowVersion == 0
            ?
            """
            INSERT INTO learning_example_overrides(
                source_kind, source_id, original_text, lower_text,
                learning_decision, policy_set, rule_code, action,
                timeout_seconds, reason, is_removed, row_version,
                created_utc, updated_utc)
            VALUES(
                @source_kind, @source_id, @original_text, @lower_text,
                @decision, @policy_set, @rule_code, @action,
                @timeout_seconds, @reason, 1, 1, @now, @now);
            """
            :
            """
            UPDATE learning_example_overrides
            SET is_removed = 1,
                row_version = row_version + 1,
                updated_utc = @now
            WHERE source_kind = @source_kind
              AND source_id = @source_id
              AND row_version = @expected_version
              AND is_removed = 0;
            """;
        var snapshot = new LearningDatasetDraft(
            current.OriginalText,
            current.Decision,
            current.PolicySet,
            current.RuleCode,
            current.Action,
            current.TimeoutSeconds,
            current.Reason);
        AddOverrideParameters(command, source, snapshot, now);
        command.Parameters.AddWithValue("@expected_version", expectedRowVersion);
        try
        {
            if (await command.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false) != 1)
            {
                throw ConcurrencyException(source);
            }
        }
        catch (SqliteException exception) when (
            expectedRowVersion == 0 && exception.SqliteErrorCode == 19)
        {
            throw ConcurrencyException(source, exception);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _revision.Advance();
    }

    private static async Task<LearningDatasetRow?> ReadEffectiveByKeyAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LearningSourceKey source,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = EffectiveExamplesCte + "\n" +
            """
            SELECT source_kind, source_id, original_text, lower_text,
                   learning_decision, policy_set, rule_code, action,
                   timeout_seconds, reason, source_label, occurred_utc,
                   row_version
            FROM effective_examples
            WHERE source_kind = @source_kind
              AND source_id = @source_id;
            """;
        command.Parameters.AddWithValue("@source_kind", ToDatabaseSourceKind(source.Kind));
        command.Parameters.AddWithValue("@source_id", source.SourceId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadRow(reader)
            : null;
    }

    private static async Task EnsureActiveRuleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PolicySetCode policySet,
        string ruleCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT EXISTS(
                SELECT 1
                FROM blacklist_rules rule
                INNER JOIN policy_sets policy
                    ON policy.scope_id = rule.scope_id
                WHERE policy.broadcaster_id = @global_scope
                  AND policy.set_code = @policy_set
                  AND policy.enabled = 1
                  AND policy.archived_utc IS NULL
                  AND rule.rule_code = @rule_code
                  AND rule.enabled = 1
                  AND rule.archived_utc IS NULL);
            """;
        command.Parameters.AddWithValue("@global_scope", GlobalPolicyStorage.BroadcasterId);
        command.Parameters.AddWithValue("@policy_set", ToDatabasePolicySet(policySet));
        command.Parameters.AddWithValue("@rule_code", ruleCode);
        if (Convert.ToInt32(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            throw new ArgumentException(
                $"Rule '{ruleCode}' is not active for policy '{policySet}'.",
                nameof(ruleCode));
        }
    }

    private static LearningDatasetDraft ValidateAndNormalize(LearningDatasetDraft draft)
    {
        if (string.IsNullOrEmpty(draft.OriginalText) || draft.OriginalText.Length > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draft),
                "Learning text must contain between 1 and 500 characters.");
        }

        var ruleCode = draft.RuleCode?.Trim() ?? string.Empty;
        if (ruleCode.Length == 0 || ruleCode.Length > 100)
        {
            throw new ArgumentException("A valid rule code is required.", nameof(draft));
        }

        var reason = draft.Reason?.Trim() ?? string.Empty;
        if (reason.Length > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draft),
                "Learning reasons may contain at most 500 characters.");
        }

        if (draft.PolicySet == PolicySetCode.Backseatgaming &&
            !StringComparer.Ordinal.Equals(ruleCode, ModerationRuleCodes.Backseatgaming) ||
            draft.PolicySet == PolicySetCode.General &&
            StringComparer.Ordinal.Equals(ruleCode, ModerationRuleCodes.Backseatgaming))
        {
            throw new ArgumentException(
                "Rule code and policy set do not match.",
                nameof(draft));
        }

        if (draft.Decision == LearningDecision.Allow)
        {
            return draft with
            {
                RuleCode = ruleCode,
                Action = ModerationActionKind.None,
                TimeoutSeconds = null,
                Reason = reason,
            };
        }

        if (draft.Action == ModerationActionKind.None)
        {
            throw new ArgumentException(
                "Moderated learning examples require an action.",
                nameof(draft));
        }

        if (draft.Action == ModerationActionKind.Timeout)
        {
            if (draft.TimeoutSeconds is not >= 1 or > 1_209_600)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(draft),
                    "Timeout duration must be between 1 and 1209600 seconds.");
            }
        }
        else if (draft.TimeoutSeconds is not null)
        {
            throw new ArgumentException(
                "Only timeout actions may contain a duration.",
                nameof(draft));
        }

        return draft with { RuleCode = ruleCode, Reason = reason };
    }

    private static void ValidateQuery(LearningDatasetQuery query)
    {
        if (query.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        if (query.PageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(nameof(query));
        }

        _ = ResolveSortExpression(query.SortColumn);
    }

    private static void ValidateSource(LearningSourceKey source)
    {
        if (source.SourceId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }
    }

    private static void EnsureExpectedVersion(
        LearningSourceKey source,
        long expected,
        long actual)
    {
        if (expected != actual)
        {
            throw ConcurrencyException(source);
        }
    }

    private static InvalidOperationException ConcurrencyException(
        LearningSourceKey source,
        Exception? innerException = null) =>
        new(
            $"Learning example '{source.DisplayId}' changed concurrently. Reload it before saving.",
            innerException);

    private static void AddQueryParameters(
        SqliteCommand command,
        LearningDatasetQuery query)
    {
        command.Parameters.AddWithValue(
            "@decision",
            query.Decision is { } decision
                ? ToDatabaseDecision(decision)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "@policy_set",
            query.PolicySet is { } policySet
                ? ToDatabasePolicySet(policySet)
                : DBNull.Value);
        command.Parameters.AddWithValue(
            "@search",
            (query.SearchText ?? string.Empty).Trim().ToLowerInvariant());
    }

    private static void AddOverrideParameters(
        SqliteCommand command,
        LearningSourceKey source,
        LearningDatasetDraft draft,
        string now)
    {
        command.Parameters.AddWithValue("@source_kind", ToDatabaseSourceKind(source.Kind));
        command.Parameters.AddWithValue("@source_id", source.SourceId);
        command.Parameters.AddWithValue("@original_text", draft.OriginalText);
        command.Parameters.AddWithValue("@lower_text", draft.OriginalText.ToLowerInvariant());
        command.Parameters.AddWithValue("@decision", ToDatabaseDecision(draft.Decision));
        command.Parameters.AddWithValue("@policy_set", ToDatabasePolicySet(draft.PolicySet));
        command.Parameters.AddWithValue("@rule_code", draft.RuleCode);
        command.Parameters.AddWithValue("@action", ToDatabaseAction(draft.Action));
        command.Parameters.AddWithValue("@timeout_seconds", SqliteData.DbValue(draft.TimeoutSeconds));
        command.Parameters.AddWithValue("@reason", draft.Reason);
        command.Parameters.AddWithValue("@now", now);
    }

    private static LearningDatasetRow ReadRow(SqliteDataReader reader)
    {
        var source = new LearningSourceKey(
            ParseSourceKind(reader.GetString(0)),
            reader.GetInt64(1));
        return new LearningDatasetRow(
            source,
            source.DisplayId,
            reader.GetString(2),
            reader.GetString(3).ToLowerInvariant(),
            ParseDecision(reader.GetString(4)),
            ParsePolicySet(reader.GetString(5)),
            reader.GetString(6),
            ParseAction(reader.GetString(7)),
            reader.IsDBNull(8) ? null : reader.GetInt32(8),
            reader.GetString(9),
            reader.GetString(10),
            SqliteData.GetDateTimeOffset(reader, 11),
            reader.GetInt64(12));
    }

    private static string ResolveSortExpression(string? sortColumn) =>
        sortColumn?.Trim().ToLowerInvariant() switch
        {
            null or "" or "occurredatutc" => "occurred_utc",
            "displayid" =>
                "(CASE source_kind WHEN 'twitch_moderation' THEN 'T' ELSE 'L' END) " +
                "|| printf('%020d', source_id)",
            "originaltext" => "lower(original_text)",
            "decision" => "learning_decision",
            "policyset" => "policy_set",
            "rulecode" => "rule_code",
            "action" => "action",
            "timeoutseconds" => "timeout_seconds",
            "reason" => "lower(reason)",
            "sourcelabel" => "lower(source_label)",
            _ => throw new ArgumentException(
                $"Unsupported learning sort column '{sortColumn}'.",
                nameof(sortColumn)),
        };

    internal static string ToDatabaseSourceKind(LearningSourceKind value) => value switch
    {
        LearningSourceKind.TwitchModeration => "twitch_moderation",
        LearningSourceKind.ManualTest => "manual_test",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static LearningSourceKind ParseSourceKind(string value) => value switch
    {
        "twitch_moderation" => LearningSourceKind.TwitchModeration,
        "manual_test" => LearningSourceKind.ManualTest,
        _ => throw new InvalidDataException($"Unsupported learning source '{value}'."),
    };

    internal static string ToDatabaseDecision(LearningDecision value) => value switch
    {
        LearningDecision.Moderate => "moderate",
        LearningDecision.Allow => "allow",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static LearningDecision ParseDecision(string value) => value switch
    {
        "moderate" => LearningDecision.Moderate,
        "allow" => LearningDecision.Allow,
        _ => throw new InvalidDataException($"Unsupported learning decision '{value}'."),
    };

    internal static string ToDatabasePolicySet(PolicySetCode value) => value switch
    {
        PolicySetCode.General => "general",
        PolicySetCode.Backseatgaming => "backseatgaming",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static PolicySetCode ParsePolicySet(string value) => value switch
    {
        "general" => PolicySetCode.General,
        "backseatgaming" => PolicySetCode.Backseatgaming,
        _ => throw new InvalidDataException($"Unsupported policy set '{value}'."),
    };

    internal static string ToDatabaseAction(ModerationActionKind value) => value switch
    {
        ModerationActionKind.None => "none",
        ModerationActionKind.Delete => "delete",
        ModerationActionKind.Timeout => "timeout",
        ModerationActionKind.Ban => "ban",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    internal static ModerationActionKind ParseAction(string value) => value switch
    {
        "none" => ModerationActionKind.None,
        "delete" => ModerationActionKind.Delete,
        "timeout" => ModerationActionKind.Timeout,
        "ban" => ModerationActionKind.Ban,
        _ => throw new InvalidDataException($"Unsupported learning action '{value}'."),
    };
}
