using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TwitchAI.Core.Models;

namespace TwitchAI.Infrastructure.Persistence;

public sealed record PolicyChangeSet(
    string BroadcasterId,
    PolicyScopeKind? ScopeKind,
    string? CategoryId,
    bool AllowlistChanged,
    long? NewRevision);

public interface IPolicyCacheInvalidator
{
    void Invalidate(PolicyChangeSet changeSet);
}

public interface IPolicyStore
{
    Task EnsureBroadcasterSeededAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default);

    Task<EffectivePolicySnapshot> GetEffectivePolicyAsync(
        string broadcasterId,
        string? categoryId,
        CancellationToken cancellationToken = default);

    Task<EffectivePolicySnapshot> GetLearningPolicyAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromException<EffectivePolicySnapshot>(
            new NotSupportedException(
                "This policy store does not expose an offline learning snapshot."));
}

public sealed class SqlitePolicyStore : IPolicyStore, IPolicyCacheInvalidator
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly IChatTextNormalizer _normalizer;
    private readonly ConcurrentDictionary<PolicyCacheKey, Lazy<Task<EffectivePolicySnapshot>>> _cache =
        new();
    private readonly ConcurrentDictionary<string, Lazy<Task>> _seedTasks =
        new(StringComparer.Ordinal);

    public SqlitePolicyStore(
        ISqliteConnectionFactory connectionFactory,
        IChatTextNormalizer normalizer,
        ISqliteWriteCoordinator? writeCoordinator = null)
    {
        _connectionFactory = connectionFactory;
        _normalizer = normalizer;
        _writeCoordinator = writeCoordinator ?? SqliteWriteCoordinator.ProcessWide;
    }

    public async Task EnsureBroadcasterSeededAsync(
        string broadcasterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);

        var lazy = _seedTasks.GetOrAdd(
            GlobalPolicyStorage.BroadcasterId,
            key => new Lazy<Task>(
                () => SeedBroadcasterAsync(key, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _seedTasks.TryRemove(GlobalPolicyStorage.BroadcasterId, out _);
            throw;
        }
    }

    private async Task SeedBroadcasterAsync(
        string broadcasterId,
        CancellationToken cancellationToken)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();
        var now = SqliteData.ToDatabaseTimestamp(DateTimeOffset.UtcNow);

        await InsertScopeAsync(
            connection,
            transaction,
            broadcasterId,
            "general",
            null,
            now,
            cancellationToken).ConfigureAwait(false);
        await InsertScopeAsync(
            connection,
            transaction,
            broadcasterId,
            "category",
            null,
            now,
            cancellationToken).ConfigureAwait(false);

        var generalScopeId = await GetScopeIdAsync(
            connection,
            transaction,
            broadcasterId,
            "general",
            cancellationToken).ConfigureAwait(false);
        var backseatScopeId = await GetScopeIdAsync(
            connection,
            transaction,
            broadcasterId,
            "backseatgaming",
            cancellationToken).ConfigureAwait(false);

        await InsertGeneralRuleSeedAsync(
            connection,
            transaction,
            generalScopeId,
            now,
            cancellationToken).ConfigureAwait(false);
        await InsertBackseatgamingSeedAsync(
            connection,
            transaction,
            backseatScopeId,
            now,
            cancellationToken).ConfigureAwait(false);
        await InsertCategoryExclusionAsync(
            connection,
            transaction,
            backseatScopeId,
            GlobalPolicyStorage.JustChattingCategoryId,
            now,
            cancellationToken).ConfigureAwait(false);
        await InsertCategoryExclusionAsync(
            connection,
            transaction,
            backseatScopeId,
            GlobalPolicyStorage.IrlCategoryId,
            now,
            cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        Invalidate(new PolicyChangeSet(
            broadcasterId,
            null,
            null,
            AllowlistChanged: false,
            NewRevision: null));
    }

    public async Task<EffectivePolicySnapshot> GetEffectivePolicyAsync(
        string broadcasterId,
        string? categoryId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(broadcasterId);
        await EnsureBroadcasterSeededAsync(broadcasterId, cancellationToken).ConfigureAwait(false);

        var scopes = await ReadApplicableScopesAsync(
            broadcasterId,
            categoryId,
            cancellationToken).ConfigureAwait(false);
        var general = scopes.Single(item => item.ScopeKind == PolicyScopeKind.General);
        var category = scopes.SingleOrDefault(item => item.ScopeKind == PolicyScopeKind.Category);
        var key = new PolicyCacheKey(
            broadcasterId,
            category?.CategoryId,
            general.Revision,
            category?.Revision ?? 0);

        var lazy = _cache.GetOrAdd(
            key,
            _ => new Lazy<Task<EffectivePolicySnapshot>>(
                () => BuildSnapshotAsync(
                    broadcasterId,
                    categoryId,
                    general,
                    category,
                    CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    public async Task<EffectivePolicySnapshot> GetLearningPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        await EnsureBroadcasterSeededAsync(
                GlobalPolicyStorage.BroadcasterId,
                cancellationToken)
            .ConfigureAwait(false);

        var scopes = await ReadLearningScopesAsync(cancellationToken)
            .ConfigureAwait(false);
        var general = scopes.Single(item => item.ScopeKind == PolicyScopeKind.General);
        var backseatgaming = scopes.Single(item => item.ScopeKind == PolicyScopeKind.Category);
        var key = new PolicyCacheKey(
            GlobalPolicyStorage.BroadcasterId,
            CategoryId: null,
            general.Revision,
            backseatgaming.Revision);
        var lazy = _cache.GetOrAdd(
            key,
            _ => new Lazy<Task<EffectivePolicySnapshot>>(
                () => BuildSnapshotAsync(
                    GlobalPolicyStorage.BroadcasterId,
                    requestedCategoryId: null,
                    general,
                    backseatgaming,
                    CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _cache.TryRemove(key, out _);
            throw;
        }
    }

    public void Invalidate(PolicyChangeSet changeSet)
    {
        ArgumentNullException.ThrowIfNull(changeSet);

        foreach (var key in _cache.Keys)
        {
            if (!StringComparer.Ordinal.Equals(
                    changeSet.BroadcasterId,
                    GlobalPolicyStorage.BroadcasterId)
                && !StringComparer.Ordinal.Equals(
                    key.BroadcasterId,
                    changeSet.BroadcasterId))
            {
                continue;
            }

            if (changeSet.ScopeKind == PolicyScopeKind.Category &&
                !StringComparer.Ordinal.Equals(key.CategoryId, changeSet.CategoryId))
            {
                continue;
            }

            _cache.TryRemove(key, out _);
        }
    }

    private async Task<IReadOnlyList<PolicyScopeRecord>> ReadApplicableScopesAsync(
        string broadcasterId,
        string? categoryId,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT scope_id, broadcaster_id, scope_kind, category_id, revision, enabled
            FROM policy_sets
            WHERE broadcaster_id = @broadcaster_id
              AND enabled = 1
              AND archived_utc IS NULL
              AND (
                  scope_kind = 'general' OR
                  (
                      scope_kind = 'category'
                      AND @category_id IS NOT NULL
                      AND EXISTS (
                          SELECT 1
                          FROM twitch_categories known
                          WHERE known.category_id = @category_id
                      )
                      AND NOT EXISTS (
                          SELECT 1
                          FROM policy_category_exclusions e
                          WHERE e.scope_id = policy_sets.scope_id
                            AND e.category_id = @category_id
                            AND e.enabled = 1
                            AND e.archived_utc IS NULL
                      )
                  )
              )
            ORDER BY CASE scope_kind WHEN 'general' THEN 0 ELSE 1 END;
            """;
        command.Parameters.AddWithValue(
            "@broadcaster_id",
            GlobalPolicyStorage.BroadcasterId);
        command.Parameters.AddWithValue("@category_id", SqliteData.DbValue(categoryId));

        var scopes = new List<PolicyScopeRecord>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scopes.Add(new PolicyScopeRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                ParseScopeKind(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                reader.GetBoolean(5)));
        }

        if (!scopes.Any(item => item.ScopeKind == PolicyScopeKind.General))
        {
            throw new InvalidOperationException(
                $"Broadcaster '{broadcasterId}' has no enabled general policy scope.");
        }

        return scopes;
    }

    private async Task<IReadOnlyList<PolicyScopeRecord>> ReadLearningScopesAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT scope_id, broadcaster_id, scope_kind, category_id, revision, enabled
            FROM policy_sets
            WHERE broadcaster_id = @global_scope
              AND enabled = 1
              AND archived_utc IS NULL
              AND scope_kind IN ('general', 'category')
            ORDER BY CASE scope_kind WHEN 'general' THEN 0 ELSE 1 END;
            """;
        command.Parameters.AddWithValue(
            "@global_scope",
            GlobalPolicyStorage.BroadcasterId);

        var scopes = new List<PolicyScopeRecord>(2);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scopes.Add(new PolicyScopeRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                ParseScopeKind(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4),
                reader.GetBoolean(5)));
        }

        if (!scopes.Any(item => item.ScopeKind == PolicyScopeKind.General)
            || !scopes.Any(item => item.ScopeKind == PolicyScopeKind.Category))
        {
            throw new InvalidOperationException(
                "The global learning policy requires active General and Backseatgaming sets.");
        }

        return scopes;
    }

    private async Task<EffectivePolicySnapshot> BuildSnapshotAsync(
        string broadcasterId,
        string? requestedCategoryId,
        PolicyScopeRecord general,
        PolicyScopeRecord? category,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var rulesWithScope = await ReadRulesAsync(
            connection,
            general.ScopeId,
            category?.ScopeId,
            cancellationToken).ConfigureAwait(false);

        var deduplicated = rulesWithScope
            .GroupBy(item => item.Rule.RuleCode, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(item => item.ScopeRank).ThenBy(item => item.Rule.Priority).First())
            .OrderBy(item => item.ScopeRank)
            .ThenBy(item => item.Rule.Priority)
            .ThenBy(item => item.Rule.RuleCode, StringComparer.Ordinal)
            .ToArray();

        var ruleIds = deduplicated.Select(item => item.Rule.RuleId).ToHashSet();
        var phrases = await ReadPhrasesAsync(connection, ruleIds, cancellationToken)
            .ConfigureAwait(false);
        var prompt = CompilePromptFragment(deduplicated, phrases);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(prompt)));
        var hasCategoryRules = deduplicated.Any(item => item.ScopeRank == 1);

        return new EffectivePolicySnapshot(
            broadcasterId,
            hasCategoryRules ? requestedCategoryId : null,
            general.Revision,
            category?.Revision ?? 0,
            hash,
            prompt,
            deduplicated.Select(item => item.Rule).ToArray(),
            phrases);
    }

    private static async Task<IReadOnlyList<ScopedRule>> ReadRulesAsync(
        SqliteConnection connection,
        long generalScopeId,
        long? categoryScopeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT r.rule_id, r.scope_id, r.rule_code, r.title,
                   r.prompt_instruction, r.default_action,
                   r.default_timeout_seconds, r.public_reason,
                   r.priority, r.enabled, r.row_version,
                   r.created_utc, r.updated_utc, r.archived_utc,
                   r.evaluation_mode,
                   CASE s.scope_kind WHEN 'general' THEN 0 ELSE 1 END AS scope_rank
            FROM blacklist_rules r
            INNER JOIN policy_sets s ON s.scope_id = r.scope_id
            WHERE r.enabled = 1
              AND r.archived_utc IS NULL
              AND r.rule_code <> 'G_STREAMER_INSTRUCTION'
              AND (r.scope_id = @general_scope_id OR r.scope_id = @category_scope_id)
            ORDER BY scope_rank, r.priority, r.rule_code;
            """;
        command.Parameters.AddWithValue("@general_scope_id", generalScopeId);
        command.Parameters.AddWithValue("@category_scope_id", SqliteData.DbValue(categoryScopeId));

        var rules = new List<ScopedRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rules.Add(new ScopedRule(
                ReadRule(reader),
                reader.GetInt32(15)));
        }

        return rules;
    }

    private static async Task<IReadOnlyList<BlacklistPhraseRecord>> ReadPhrasesAsync(
        SqliteConnection connection,
        HashSet<long> ruleIds,
        CancellationToken cancellationToken)
    {
        if (ruleIds.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        var parameterNames = new List<string>(ruleIds.Count);
        var index = 0;
        foreach (var ruleId in ruleIds)
        {
            var parameterName = $"@rule_{index++}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, ruleId);
        }

        command.CommandText =
            $"""
             SELECT phrase_id, rule_id, phrase, normalized_phrase,
                    enabled, row_version, archived_utc
             FROM phrase_rules
             WHERE enabled = 1
              AND archived_utc IS NULL
              AND (
                  manual_override = 'active' OR
                  (
                      manual_override = 'auto'
                      AND positive_confirmations >= 1
                      AND (
                          CAST(positive_confirmations AS REAL) /
                          (positive_confirmations + negative_confirmations)
                      ) >= 0.60
                  )
              )
               AND rule_id IN ({string.Join(",", parameterNames)})
             ORDER BY rule_id, normalized_phrase;
             """;

        var phrases = new List<BlacklistPhraseRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var normalized = reader.GetString(3);
            if (!seen.Add(normalized))
            {
                continue;
            }

            phrases.Add(new BlacklistPhraseRecord(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                normalized,
                reader.GetBoolean(4),
                reader.GetInt64(5),
                SqliteData.GetNullableDateTimeOffset(reader, 6)));
        }

        return phrases;
    }

    private static string CompilePromptFragment(
        IReadOnlyList<ScopedRule> rules,
        IReadOnlyList<BlacklistPhraseRecord> phrases)
    {
        _ = phrases;
        var builder = new StringBuilder();
        builder.AppendLine("active moderation policies:");

        foreach (var item in rules)
        {
            var rule = item.Rule;
            builder.Append(item.ScopeRank == 0 ? "g|" : "c|");
            builder.Append(SingleLine(rule.RuleCode));
            builder.Append("|a=");
            builder.Append(rule.DefaultAction switch
            {
                ModerationRuleAction.Timeout => "timeout",
                ModerationRuleAction.Ban => "ban",
                _ => "delete",
            });
            if (rule.DefaultTimeoutSeconds is { } seconds)
            {
                builder.Append("|s=");
                builder.Append(seconds);
            }

            builder.Append("|reason=");
            builder.Append(SingleLine(rule.PublicReason));
            builder.Append("|rule=");
            builder.Append(SingleLine(rule.PromptInstruction));

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string SingleLine(string value)
    {
        return value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/').Trim();
    }

    private static BlacklistRuleRecord ReadRule(SqliteDataReader reader)
    {
        return new BlacklistRuleRecord(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            ParseAction(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetBoolean(9),
            reader.GetInt64(10),
            SqliteData.GetDateTimeOffset(reader, 11),
            SqliteData.GetDateTimeOffset(reader, 12),
            SqliteData.GetNullableDateTimeOffset(reader, 13),
            ParseEvaluationMode(reader.GetString(14)));
    }

    private static async Task InsertScopeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string broadcasterId,
        string scopeKind,
        string? categoryId,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO policy_sets(
                broadcaster_id, scope_kind, set_code, category_id,
                revision, enabled, created_utc, updated_utc)
            VALUES(
                @broadcaster_id, @scope_kind, @set_code, @category_id,
                1, 1, @now, @now);
            """;
        command.Parameters.AddWithValue("@broadcaster_id", broadcasterId);
        command.Parameters.AddWithValue("@scope_kind", scopeKind);
        command.Parameters.AddWithValue(
            "@set_code",
            scopeKind == "general" ? "general" : "backseatgaming");
        command.Parameters.AddWithValue("@category_id", SqliteData.DbValue(categoryId));
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> GetScopeIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string broadcasterId,
        string setCode,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT scope_id
            FROM policy_sets
            WHERE broadcaster_id = @broadcaster_id
              AND set_code = @set_code;
            """;
        command.Parameters.AddWithValue("@broadcaster_id", broadcasterId);
        command.Parameters.AddWithValue("@set_code", setCode);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task InsertBackseatgamingSeedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long scopeId,
        string now,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO blacklist_rules(
                    scope_id, rule_code, title, prompt_instruction,
                    default_action, default_timeout_seconds, public_reason,
                    priority, enabled, row_version, created_utc, updated_utc)
                VALUES(
                    @scope_id, @rule_code, 'Backseatgaming',
                    @instruction, 'timeout', 600, @reason,
                    100, 1, 1, @now, @now);
                """;
            command.Parameters.AddWithValue("@scope_id", scopeId);
            command.Parameters.AddWithValue(
                "@rule_code",
                GlobalPolicyStorage.BackseatgamingRuleCode);
            command.Parameters.AddWithValue(
                "@instruction",
                ModerationRuleCodes.BackseatgamingPromptInstruction);
            command.Parameters.AddWithValue("@reason", "Unaufgefordertes Backseatgaming");
            command.Parameters.AddWithValue("@now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long ruleId;
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                SELECT rule_id
                FROM blacklist_rules
                WHERE scope_id = @scope_id AND rule_code = @rule_code;
                """;
            command.Parameters.AddWithValue("@scope_id", scopeId);
            command.Parameters.AddWithValue(
                "@rule_code",
                GlobalPolicyStorage.BackseatgamingRuleCode);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            ruleId = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        string[] phrases = ["fahr links", "nimm die andere waffe", "du musst zuerst"];
        foreach (var phrase in phrases)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT OR IGNORE INTO phrase_rules(
                    rule_id, phrase, normalized_phrase, enabled,
                    manual_override, positive_confirmations, negative_confirmations,
                    row_version, created_utc, updated_utc)
                VALUES(
                    @rule_id, @phrase, @normalized_phrase, 1,
                    'auto', 1, 0, 1, @now, @now);
                """;
            command.Parameters.AddWithValue("@rule_id", ruleId);
            command.Parameters.AddWithValue("@phrase", phrase);
            command.Parameters.AddWithValue("@normalized_phrase", _normalizer.Normalize(phrase));
            command.Parameters.AddWithValue("@now", now);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertGeneralRuleSeedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long scopeId,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO blacklist_rules(
                scope_id, rule_code, title, prompt_instruction,
                default_action, default_timeout_seconds, public_reason,
                priority, enabled, row_version, created_utc, updated_utc)
            VALUES(
                @scope_id, @rule_code, 'Allgemeine Blacklist',
                @instruction, 'timeout', 600, @reason,
                100, 1, 1, @now, @now);
            """;
        command.Parameters.AddWithValue("@scope_id", scopeId);
        command.Parameters.AddWithValue(
            "@rule_code",
            GlobalPolicyStorage.GeneralRuleCode);
        command.Parameters.AddWithValue(
            "@instruction",
            "Lokale Treffer als mögliches allgemeines Moderationssignal bewerten.");
        command.Parameters.AddWithValue(
            "@reason",
            "Verstoß gegen die allgemeinen Chatregeln");
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertCategoryExclusionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long backseatScopeId,
        string categoryId,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO policy_category_exclusions(
                scope_id, category_id, enabled, row_version,
                created_utc, updated_utc)
            VALUES(@scope_id, @category_id, 1, 1, @now, @now);
            """;
        command.Parameters.AddWithValue("@scope_id", backseatScopeId);
        command.Parameters.AddWithValue("@category_id", categoryId);
        command.Parameters.AddWithValue("@now", now);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static PolicyScopeKind ParseScopeKind(string value)
    {
        return value switch
        {
            "general" => PolicyScopeKind.General,
            "category" => PolicyScopeKind.Category,
            _ => throw new InvalidDataException($"Unsupported policy scope kind '{value}'."),
        };
    }

    private static ModerationRuleAction ParseAction(string value)
    {
        return value switch
        {
            "delete" => ModerationRuleAction.Delete,
            "timeout" => ModerationRuleAction.Timeout,
            "ban" => ModerationRuleAction.Ban,
            _ => throw new InvalidDataException($"Unsupported moderation action '{value}'."),
        };
    }

    private static RuleEvaluationMode ParseEvaluationMode(string value) => value switch
    {
        "semantic_ai" => RuleEvaluationMode.SemanticAi,
        "deterministic_phrase" => RuleEvaluationMode.DeterministicPhrase,
        "human_only" => RuleEvaluationMode.HumanOnly,
        _ => throw new InvalidDataException($"Unsupported evaluation mode '{value}'."),
    };

    private sealed record ScopedRule(BlacklistRuleRecord Rule, int ScopeRank);

    private sealed record PolicyCacheKey(
        string BroadcasterId,
        string? CategoryId,
        long GeneralRevision,
        long CategoryRevision);
}
