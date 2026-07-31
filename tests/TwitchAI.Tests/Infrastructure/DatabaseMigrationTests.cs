using Microsoft.Data.Sqlite;

namespace TwitchAI.Tests.Infrastructure;

public sealed class DatabaseMigrationTests
{
    [Fact]
    public async Task Initialize_CreatesSchemaAndBootstrapCategories()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();

        var tables = await ReadStringsAsync(
            connection,
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """);
        var categories = await ReadStringsAsync(
            connection,
            "SELECT category_id FROM twitch_categories ORDER BY category_id;");

        Assert.Contains("schema_migrations", tables);
        Assert.Contains("moderation_cases", tables);
        Assert.Contains("processed_eventsub_events", tables);
        Assert.Contains("configuration_audit", tables);
        Assert.Contains("message_availability_events", tables);
        Assert.Contains("message_clear_watermarks", tables);
        Assert.Contains("chat_reply_templates", tables);
        Assert.Contains("chat_reply_proposals", tables);
        Assert.Contains("manual_learning_examples", tables);
        Assert.Contains("learning_example_overrides", tables);
        Assert.Equal(["32982", "509658", "509672"], categories);

        await using var seedCommand = connection.CreateCommand();
        seedCommand.CommandText =
            """
            SELECT subject_value, normalized_value
            FROM whitelist_phrases
            WHERE broadcaster_id = '*' AND subject_kind = 'exact_message';
            """;
        await using var seed = await seedCommand.ExecuteReaderAsync();
        Assert.True(await seed.ReadAsync());
        Assert.Equal("KEKW", seed.GetString(0));
        Assert.Equal("kekw", seed.GetString(1));
    }

    [Fact]
    public async Task Initialize_IsIdempotentAndUsesWalWithForeignKeys()
    {
        await using var database = await TestDatabase.CreateAsync();

        await database.Initializer.InitializeAsync();
        await database.Initializer.InitializeAsync();

        await using var connection = await database.OpenConnectionAsync();
        var migrationCount = await ScalarAsync<long>(
            connection,
            "SELECT COUNT(*) FROM schema_migrations;");
        var journalMode = await ScalarAsync<string>(connection, "PRAGMA journal_mode;");
        var foreignKeys = await ScalarAsync<long>(connection, "PRAGMA foreign_keys;");

        Assert.Equal(17, migrationCount);
        Assert.Equal("wal", journalMode, ignoreCase: true);
        Assert.Equal(1, foreignKeys);
    }

    [Fact]
    public async Task Version17_AddsIrlCategoryAndBackseatgamingExclusionIdempotently()
    {
        await using var database = await TestDatabase.CreateAtSchemaVersionAsync(16);

        await database.Initializer.InitializeAsync();
        await database.Initializer.InitializeAsync();

        await using var connection = await database.OpenConnectionAsync();
        var irlCategories = await ReadStringsAsync(
            connection,
            """
            SELECT category_id || '|' || display_name
            FROM twitch_categories
            WHERE category_id = '509672';
            """);
        var excludedCategories = await ReadStringsAsync(
            connection,
            """
            SELECT exclusion.category_id
            FROM policy_category_exclusions exclusion
            INNER JOIN policy_sets policy
                ON policy.scope_id = exclusion.scope_id
            WHERE policy.broadcaster_id = '*'
              AND policy.set_code = 'backseatgaming'
              AND policy.enabled = 1
              AND policy.archived_utc IS NULL
              AND exclusion.enabled = 1
              AND exclusion.archived_utc IS NULL
              AND exclusion.category_id IN ('509658', '509672')
            ORDER BY exclusion.category_id;
            """);
        var exclusionCounts = await ReadStringsAsync(
            connection,
            """
            SELECT category_id || '|' || COUNT(*)
            FROM policy_category_exclusions
            WHERE category_id IN ('509658', '509672')
            GROUP BY category_id
            ORDER BY category_id;
            """);
        var foreignKeyViolations = await ReadStringsAsync(
            connection,
            "PRAGMA foreign_key_check;");

        Assert.Equal(["509672|IRL"], irlCategories);
        Assert.Equal(["509658", "509672"], excludedCategories);
        Assert.Equal(["509658|1", "509672|1"], exclusionCounts);
        Assert.Empty(foreignKeyViolations);
        Assert.Equal(
            17L,
            await ScalarAsync<long>(
                connection,
                "SELECT MAX(version) FROM schema_migrations;"));
    }

    [Fact]
    public async Task ChatReplyMigration_UpgradesVersion10AndCreatesBothTables()
    {
        await using var database = await TestDatabase.CreateAtSchemaVersionAsync(10);

        await database.Initializer.InitializeAsync();

        await using var connection = await database.OpenConnectionAsync();
        var migrationVersion = await ScalarAsync<long>(
            connection,
            "SELECT MAX(version) FROM schema_migrations;");
        var tables = await ReadStringsAsync(
            connection,
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('chat_reply_templates','chat_reply_proposals')
            ORDER BY name;
            """);

        Assert.Equal(17, migrationVersion);
        Assert.Equal(
            ["chat_reply_proposals", "chat_reply_templates"],
            tables);
    }

    [Theory]
    [InlineData(
        "Beleidigung eines Nutzers.",
        "Belästigung eines Nutzers.")]
    [InlineData(
        "Direkte Beleidigung eines Nutzers.",
        "Gezielte Belästigung eines Nutzers.")]
    [InlineData(
        "Direkte Beleidigung gegen einen Nutzer.",
        "Gezielte Belästigung eines Nutzers.")]
    public async Task Version15_UpdatesKnownActiveRuleReasonsOnceAndPreservesHistory(
        string legacyInsultReason,
        string legacyHarassmentReason)
    {
        await using var database = await TestDatabase.CreateAtSchemaVersionAsync(14);
        const string timestamp = "2026-07-29T12:00:00.0000000+00:00";
        const string historicalReason = "Beleidigung eines Nutzers.";
        const string auditJson =
            """{"rule_code":"G_INSULT","public_reason":"Beleidigung eines Nutzers."}""";

        long generalRevisionBefore;
        long insultRowVersionBefore;
        long harassmentRowVersionBefore;
        await using (var connection = await database.OpenConnectionAsync())
        await using (var transaction = connection.BeginTransaction())
        {
            await using var fixture = connection.CreateCommand();
            fixture.Transaction = transaction;
            fixture.CommandText =
                """
                UPDATE blacklist_rules
                SET public_reason = CASE rule_code
                        WHEN 'G_INSULT' THEN @insult_reason
                        WHEN 'G_HARASSMENT' THEN @harassment_reason
                        ELSE public_reason
                    END,
                    updated_utc = @timestamp
                WHERE scope_id = (
                        SELECT scope_id
                        FROM policy_sets
                        WHERE broadcaster_id = '*'
                          AND set_code = 'general'
                          AND archived_utc IS NULL
                    )
                  AND rule_code IN ('G_INSULT','G_HARASSMENT')
                  AND enabled = 1
                  AND archived_utc IS NULL;

                UPDATE policy_sets
                SET updated_utc = @timestamp
                WHERE broadcaster_id = '*'
                  AND set_code = 'general'
                  AND archived_utc IS NULL;

                INSERT INTO chat_message_snapshots(
                    twitch_message_id, eventsub_notification_id,
                    broadcaster_id, chatter_user_id,
                    chatter_login_original, chatter_name_original,
                    message_text_original, lower_text,
                    message_text_normalized, normalization_version,
                    original_sha256, normalized_sha256,
                    identity_fingerprint, channel_context_revision,
                    category_is_verified, ingress_sequence,
                    badges_json, received_utc)
                VALUES(
                    'v15-history-message', 'v15-history-event',
                    'channel-1', 'user-1',
                    'UserOne', 'UserOne',
                    'historical message', 'historical message',
                    'historical message', 1,
                    'v15-original-hash', 'v15-normalized-hash',
                    'v15-identity-hash', 1,
                    1, 1, '{}', @timestamp);

                INSERT INTO moderation_cases(
                    twitch_message_id, broadcaster_id,
                    chatter_user_id, chatter_name_snapshot,
                    message_text_original, message_text_normalized,
                    effective_policy_hash, model_id, envelope_json,
                    proposed_action, proposed_timeout_seconds,
                    violation_rule_code, reason, policy_set,
                    confidence, status, created_utc)
                VALUES(
                    'v15-history-message', 'channel-1',
                    'user-1', 'UserOne',
                    'historical message', 'historical message',
                    'policy-hash', 'model', '{}',
                    'timeout', 600,
                    'G_INSULT', @historical_reason, 'general',
                    0.9, 'executed', @timestamp);

                INSERT INTO configuration_audit(
                    entity_type, entity_id, operation,
                    before_json, after_json, scope_id,
                    change_reason, committed_utc)
                SELECT
                    'blacklist_rules', 'v15-history', 'update',
                    @audit_json, @audit_json, scope_id,
                    'historical reason fixture', @timestamp
                FROM policy_sets
                WHERE broadcaster_id = '*'
                  AND set_code = 'general'
                  AND archived_utc IS NULL;
                """;
            fixture.Parameters.AddWithValue("@insult_reason", legacyInsultReason);
            fixture.Parameters.AddWithValue(
                "@harassment_reason",
                legacyHarassmentReason);
            fixture.Parameters.AddWithValue("@historical_reason", historicalReason);
            fixture.Parameters.AddWithValue("@audit_json", auditJson);
            fixture.Parameters.AddWithValue("@timestamp", timestamp);
            await fixture.ExecuteNonQueryAsync();
            await transaction.CommitAsync();

            generalRevisionBefore = await ScalarAsync<long>(
                connection,
                """
                SELECT revision
                FROM policy_sets
                WHERE broadcaster_id = '*'
                  AND set_code = 'general'
                  AND archived_utc IS NULL;
                """);
            insultRowVersionBefore = await ScalarAsync<long>(
                connection,
                """
                SELECT r.row_version
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'general'
                  AND r.rule_code = 'G_INSULT'
                  AND r.archived_utc IS NULL;
                """);
            harassmentRowVersionBefore = await ScalarAsync<long>(
                connection,
                """
                SELECT r.row_version
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'general'
                  AND r.rule_code = 'G_HARASSMENT'
                  AND r.archived_utc IS NULL;
                """);
        }

        await database.Initializer.InitializeAsync();

        await using var verify = await database.OpenConnectionAsync();
        Assert.Equal(
            [
                "G_HARASSMENT|Gezielte Belästigung eines Nutzers oder Streamers.",
                "G_INSULT|Direkte Beleidigung eines Nutzers oder Streamers.",
            ],
            await ReadStringsAsync(
                verify,
                """
                SELECT r.rule_code || '|' || r.public_reason
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'general'
                  AND r.rule_code IN ('G_INSULT','G_HARASSMENT')
                  AND r.archived_utc IS NULL
                ORDER BY r.rule_code;
                """));
        Assert.Equal(
            generalRevisionBefore + 1,
            await ScalarAsync<long>(
                verify,
                """
                SELECT revision
                FROM policy_sets
                WHERE broadcaster_id = '*'
                  AND set_code = 'general'
                  AND archived_utc IS NULL;
                """));
        Assert.Equal(
            insultRowVersionBefore + 1,
            await ScalarAsync<long>(
                verify,
                """
                SELECT r.row_version
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'general'
                  AND r.rule_code = 'G_INSULT'
                  AND r.archived_utc IS NULL;
                """));
        Assert.Equal(
            harassmentRowVersionBefore + 1,
            await ScalarAsync<long>(
                verify,
                """
                SELECT r.row_version
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'general'
                  AND r.rule_code = 'G_HARASSMENT'
                  AND r.archived_utc IS NULL;
                """));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                verify,
                """
                SELECT COUNT(*)
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'general'
                  AND r.rule_code IN ('G_INSULT','G_HARASSMENT')
                  AND r.archived_utc IS NULL
                  AND r.updated_utc = '2026-07-29T12:00:00.0000000+00:00';
                """));
        Assert.NotEqual(
            timestamp,
            await ScalarAsync<string>(
                verify,
                """
                SELECT updated_utc
                FROM policy_sets
                WHERE broadcaster_id = '*'
                  AND set_code = 'general'
                  AND archived_utc IS NULL;
                """));
        Assert.Equal(
            historicalReason,
            await ScalarAsync<string>(
                verify,
                """
                SELECT reason
                FROM moderation_cases
                WHERE twitch_message_id = 'v15-history-message';
                """));
        Assert.Equal(
            $"{auditJson}|{auditJson}",
            await ScalarAsync<string>(
                verify,
                """
                SELECT before_json || '|' || after_json
                FROM configuration_audit
                WHERE change_reason = 'historical reason fixture';
                """));
        Assert.Equal(
            17L,
            await ScalarAsync<long>(
                verify,
                "SELECT MAX(version) FROM schema_migrations;"));
    }

    [Fact]
    public async Task Version14_RemovesLegacyManualPhraseSideEffectsAndKeepsLearningExample()
    {
        await using var database = await TestDatabase.CreateAtSchemaVersionAsync(13);
        const string timestamp = "2026-07-29T10:00:00.0000000+00:00";

        await using (var connection = await database.OpenConnectionAsync())
        await using (var transaction = connection.BeginTransaction())
        {
            await using var fixture = connection.CreateCommand();
            fixture.Transaction = transaction;
            fixture.CommandText =
                """
                INSERT INTO phrase_rules(
                    rule_id, phrase, normalized_phrase, enabled,
                    manual_override, positive_confirmations,
                    negative_confirmations, row_version,
                    created_utc, updated_utc)
                SELECT
                    rule_id, 'bestehende phrase', 'bestehende phrase', 1,
                    'active', 2, 1, 1, @timestamp, @timestamp
                FROM blacklist_rules
                WHERE rule_code = 'G_OTHER_ABUSE'
                  AND archived_utc IS NULL
                LIMIT 1;

                INSERT INTO manual_learning_examples(
                    test_id, message_text_original, lower_text,
                    ai_verdict, ai_action, ai_timeout_seconds,
                    ai_policy_set, ai_rule_code, ai_reason,
                    ai_trigger_phrases_json, confidence,
                    local_match_codes_json, final_decision,
                    actual_action, actual_timeout_seconds,
                    actual_policy_set, actual_rule_code, actual_reason,
                    actual_trigger_phrases_json,
                    policy_revision_fingerprint, model_id,
                    prompt_tokens, completion_tokens, total_tokens,
                    ai_latency_ms, eligible_for_learning,
                    created_utc, updated_utc)
                VALUES(
                    'legacy-v13-learning',
                    'Bestehende Phrase und nur vom Lerntest',
                    'bestehende phrase und nur vom lerntest',
                    'moderate', 'timeout', 600,
                    'general', 'G_OTHER_ABUSE', 'Test',
                    '["bestehende phrase","nur vom lerntest"]', 0.9,
                    '[]', 'confirmed_violation',
                    'timeout', 600,
                    'general', 'G_OTHER_ABUSE', 'Test',
                    '["bestehende phrase","nur vom lerntest"]',
                    'policy', 'model', 10, 5, 15, 20, 1,
                    @timestamp, @timestamp);
                """;
            fixture.Parameters.AddWithValue("@timestamp", timestamp);
            await fixture.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        long generalRevisionBefore;
        long backseatRevisionBefore;
        await using (var before = await database.OpenConnectionAsync())
        {
            generalRevisionBefore = await ScalarAsync<long>(
                before,
                """
                SELECT revision
                FROM policy_sets
                WHERE broadcaster_id = '*' AND set_code = 'general'
                  AND archived_utc IS NULL;
                """);
            backseatRevisionBefore = await ScalarAsync<long>(
                before,
                """
                SELECT revision
                FROM policy_sets
                WHERE broadcaster_id = '*' AND set_code = 'backseatgaming'
                  AND archived_utc IS NULL;
                """);
            Assert.Equal(
                "3|1",
                await ScalarAsync<string>(
                    before,
                    """
                    SELECT positive_confirmations || '|' || negative_confirmations
                    FROM phrase_rules
                    WHERE normalized_phrase = 'bestehende phrase';
                    """));
            Assert.Equal(
                "1|0",
                await ScalarAsync<string>(
                    before,
                    """
                    SELECT positive_confirmations || '|' || negative_confirmations
                    FROM phrase_rules
                    WHERE normalized_phrase = 'nur vom lerntest';
                    """));
            Assert.Equal(
                2L,
                await ScalarAsync<long>(
                    before,
                    """
                    SELECT COUNT(*)
                    FROM phrase_feedback
                    WHERE manual_example_id IS NOT NULL;
                    """));
        }

        await database.Initializer.InitializeAsync();

        await using var verify = await database.OpenConnectionAsync();
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                verify,
                """
                SELECT COUNT(*)
                FROM manual_learning_examples
                WHERE test_id = 'legacy-v13-learning';
                """));
        Assert.Equal(
            "2|1",
            await ScalarAsync<string>(
                verify,
                """
                SELECT positive_confirmations || '|' || negative_confirmations
                FROM phrase_rules
                WHERE normalized_phrase = 'bestehende phrase';
                """));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                verify,
                """
                SELECT COUNT(*)
                FROM phrase_rules
                WHERE normalized_phrase = 'nur vom lerntest';
                """));
        Assert.DoesNotContain(
            "manual_example_id",
            await ReadStringsAsync(
                verify,
                "SELECT name FROM pragma_table_info('phrase_feedback');"));
        Assert.Empty(
            await ReadStringsAsync(
                verify,
                """
                SELECT name
                FROM sqlite_master
                WHERE type = 'trigger'
                  AND name LIKE 'trg_manual_learning_%';
                """));
        Assert.Equal(
            generalRevisionBefore + 1,
            await ScalarAsync<long>(
                verify,
                """
                SELECT revision
                FROM policy_sets
                WHERE broadcaster_id = '*' AND set_code = 'general'
                  AND archived_utc IS NULL;
                """));
        Assert.Equal(
            backseatRevisionBefore + 2,
            await ScalarAsync<long>(
                verify,
                """
                SELECT revision
                FROM policy_sets
                WHERE broadcaster_id = '*' AND set_code = 'backseatgaming'
                  AND archived_utc IS NULL;
                """));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                verify,
                "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task Version12_PreservesModerationRowsAndAllowsBanActions()
    {
        await using var database = await TestDatabase.CreateAtSchemaVersionAsync(11);
        const string timestamp = "2026-07-28T10:00:00.0000000+00:00";

        await using (var connection = await database.OpenConnectionAsync())
        await using (var transaction = connection.BeginTransaction())
        {
            await using var fixture = connection.CreateCommand();
            fixture.Transaction = transaction;
            fixture.CommandText =
                """
                INSERT INTO chat_message_snapshots(
                    twitch_message_id, eventsub_notification_id,
                    broadcaster_id, chatter_user_id,
                    chatter_login_original, chatter_name_original,
                    message_text_original, lower_text,
                    message_text_normalized, normalization_version,
                    original_sha256, normalized_sha256,
                    identity_fingerprint, channel_context_revision,
                    category_is_verified, ingress_sequence,
                    badges_json, received_utc)
                VALUES(
                    'v12-existing-message', 'v12-existing-event',
                    'channel-1', 'user-1',
                    'UserOne', 'UserOne',
                    'existing text', 'existing text',
                    'existing text', 1,
                    'original-hash', 'normalized-hash',
                    'identity-hash', 1,
                    1, 1, '{}', @timestamp);

                INSERT INTO moderation_cases(
                    case_id, twitch_message_id, broadcaster_id,
                    chatter_user_id, chatter_name_snapshot,
                    message_text_original, message_text_normalized,
                    effective_policy_hash, policy_revisions_json,
                    message_snapshot_json, model_id, envelope_json,
                    proposed_action, proposed_timeout_seconds,
                    violation_rule_code, reason, policy_set,
                    trigger_phrases_json, confidence, status,
                    row_version, created_utc, batch_id,
                    local_match_codes_json, ai_latency_ms)
                VALUES(
                    1200, 'v12-existing-message', 'channel-1',
                    'user-1', 'UserOne',
                    'existing text', 'existing text',
                    'policy-hash', '{}', '{}', 'model', '{}',
                    'timeout', 600, 'G_INSULT',
                    'Bestehender Grund.', 'general',
                    '["existing text"]', 0.9, 'executed',
                    3, @timestamp, 'batch-existing',
                    '["G_INSULT"]', 42);

                INSERT INTO moderation_feedback(
                    feedback_id, case_id, operator_decision,
                    actual_action, actual_timeout_seconds,
                    actual_reason, eligible_for_learning,
                    human_approved, twitch_executed,
                    learning_accepted, decided_utc,
                    review_completed, rule_code)
                VALUES(
                    1200, 1200, 'executed',
                    'timeout', 600,
                    'Bestehender Grund.', 1,
                    1, 1, 1, @timestamp,
                    1, 'G_INSULT');

                INSERT INTO action_attempts(
                    execution_id, case_id, broadcaster_id,
                    twitch_message_id, target_user_id,
                    action, timeout_seconds, reason, status,
                    requested_utc, completed_utc, http_status,
                    response_json, rule_code)
                VALUES(
                    1200, 1200, 'channel-1',
                    'v12-existing-message', 'user-1',
                    'timeout', 600, 'Bestehender Grund.', 'succeeded',
                    @timestamp, @timestamp, 204,
                    '{}', 'G_INSULT');

                INSERT INTO phrase_feedback(
                    feedback_id, case_id, source_kind, source_event_id,
                    label_action, rule_code, trigger_phrase,
                    policy_set, is_positive, message_normalized,
                    eligible, confirmed_utc)
                VALUES(
                    1200, 1200, 'operator_execute', 'v12-existing-feedback',
                    'timeout', 'G_INSULT', 'existing text',
                    'general', 1, 'existing text',
                    1, @timestamp);
                """;
            fixture.Parameters.AddWithValue("@timestamp", timestamp);
            await fixture.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await database.Initializer.InitializeAsync();

        await using var verify = await database.OpenConnectionAsync();
        Assert.Equal(
            "timeout|600|G_INSULT|batch-existing|42",
            await ScalarAsync<string>(
                verify,
                """
                SELECT proposed_action || '|' || proposed_timeout_seconds || '|' ||
                       violation_rule_code || '|' || batch_id || '|' || ai_latency_ms
                FROM moderation_cases
                WHERE case_id = 1200;
                """));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                verify,
                "SELECT COUNT(*) FROM moderation_feedback WHERE feedback_id = 1200;"));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                verify,
                "SELECT COUNT(*) FROM action_attempts WHERE execution_id = 1200;"));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                verify,
                "SELECT COUNT(*) FROM phrase_feedback WHERE feedback_id = 1200;"));

        var newRules = await ReadStringsAsync(
            verify,
            """
            SELECT r.rule_code || '|' || r.title || '|' || r.public_reason ||
                   '|' || r.default_action || '|' || r.default_timeout_seconds
            FROM blacklist_rules r
            INNER JOIN policy_sets s ON s.scope_id = r.scope_id
            WHERE s.broadcaster_id = '*'
              AND s.set_code = 'general'
              AND r.rule_code IN ('G_ADVERTISING','G_BEGGING')
              AND r.archived_utc IS NULL
            ORDER BY r.rule_code;
            """);
        Assert.Equal(
            [
                "G_ADVERTISING|Werbung|Unaufgeforderte Werbung oder Eigenwerbung.|timeout|120",
                "G_BEGGING|Gebettel|Betteln nach Subs, Geschenken oder Giveaways.|timeout|120",
            ],
            newRules);

        await using (var transaction = verify.BeginTransaction())
        {
            await using var insertBan = verify.CreateCommand();
            insertBan.Transaction = transaction;
            insertBan.CommandText =
                """
                INSERT INTO chat_message_snapshots(
                    twitch_message_id, eventsub_notification_id,
                    broadcaster_id, chatter_user_id,
                    chatter_login_original, chatter_name_original,
                    message_text_original, lower_text,
                    message_text_normalized, normalization_version,
                    original_sha256, normalized_sha256,
                    identity_fingerprint, channel_context_revision,
                    category_is_verified, ingress_sequence,
                    badges_json, received_utc)
                VALUES(
                    'v12-ban-message', 'v12-ban-event',
                    'channel-1', 'user-2',
                    'UserTwo', 'UserTwo',
                    'ban text', 'ban text',
                    'ban text', 1,
                    'ban-original-hash', 'ban-normalized-hash',
                    'ban-identity-hash', 1,
                    1, 2, '{}', @timestamp);

                INSERT INTO moderation_cases(
                    case_id, twitch_message_id, broadcaster_id,
                    chatter_user_id, chatter_name_snapshot,
                    message_text_original, message_text_normalized,
                    effective_policy_hash, model_id, envelope_json,
                    proposed_action, violation_rule_code, reason,
                    policy_set, confidence, status, created_utc)
                VALUES(
                    1201, 'v12-ban-message', 'channel-1',
                    'user-2', 'UserTwo',
                    'ban text', 'ban text',
                    'policy-hash', 'model', '{}',
                    'ban', 'G_INSULT', 'Banwürdige Beleidigung.',
                    'general', 0.99, 'executed', @timestamp);

                INSERT INTO moderation_feedback(
                    feedback_id, case_id, operator_decision,
                    actual_action, actual_reason,
                    eligible_for_learning, human_approved,
                    twitch_executed, learning_accepted, decided_utc,
                    review_completed, rule_code)
                VALUES(
                    1201, 1201, 'executed',
                    'ban', 'Banwürdige Beleidigung.',
                    1, 1, 1, 1, @timestamp,
                    1, 'G_INSULT');

                INSERT INTO action_attempts(
                    execution_id, case_id, broadcaster_id,
                    twitch_message_id, target_user_id,
                    action, reason, status,
                    requested_utc, completed_utc,
                    http_status, response_json, rule_code)
                VALUES(
                    1201, 1201, 'channel-1',
                    'v12-ban-message', 'user-2',
                    'ban', 'Banwürdige Beleidigung.', 'succeeded',
                    @timestamp, @timestamp,
                    204, '{}', 'G_INSULT');

                INSERT INTO phrase_feedback(
                    feedback_id, case_id, source_kind, source_event_id,
                    label_action, rule_code, trigger_phrase,
                    policy_set, is_positive, message_normalized,
                    eligible, confirmed_utc)
                VALUES(
                    1201, 1201, 'operator_execute', 'v12-ban-feedback',
                    'ban', 'G_INSULT', 'ban text',
                    'general', 1, 'ban text',
                    1, @timestamp);
                """;
            insertBan.Parameters.AddWithValue("@timestamp", timestamp);
            await insertBan.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                verify,
                "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(
            [
                "ix_action_attempt_message",
                "ix_moderation_cases_status_created",
                "ix_phrase_feedback_retrieval",
                "ux_action_attempt_case",
            ],
            await ReadStringsAsync(
                verify,
                """
                SELECT name
                FROM sqlite_master
                WHERE type = 'index'
                  AND name IN (
                    'ix_action_attempt_message',
                    'ix_moderation_cases_status_created',
                    'ix_phrase_feedback_retrieval',
                    'ux_action_attempt_case')
                ORDER BY name;
                """));
    }

    [Fact]
    public async Task UserLoginWhitelistMigration_PreservesRowsAndForeignKeys()
    {
        await using var database = await TestDatabase.CreateAtSchemaVersionAsync(9);
        const long allowlistId = 4242;
        const string timestamp = "2026-07-27T10:15:30.0000000+00:00";
        await using (var connection = await database.OpenConnectionAsync())
        await using (var transaction = connection.BeginTransaction())
        {
            await using (var fixture = connection.CreateCommand())
            {
                fixture.Transaction = transaction;
                fixture.CommandText =
                    """
                    INSERT INTO whitelist_phrases(
                        allowlist_id, broadcaster_id, subject_kind,
                        subject_value, normalized_value, enabled,
                        hit_count, last_hit_utc, row_version,
                        created_utc, updated_utc, archived_utc)
                    VALUES(
                        @allowlist_id, '*', 'user_id',
                        '123456789', '123456789', 0,
                        7, @timestamp, 9,
                        @timestamp, @timestamp, @timestamp);

                    INSERT INTO chat_message_snapshots(
                        twitch_message_id, eventsub_notification_id,
                        broadcaster_id, chatter_user_id,
                        chatter_login_original, chatter_name_original,
                        message_text_original, lower_text,
                        message_text_normalized, normalization_version,
                        original_sha256, normalized_sha256,
                        identity_fingerprint, channel_context_revision,
                        category_is_verified, ingress_sequence,
                        badges_json, received_utc)
                    VALUES(
                        'whitelist-migration-message',
                        'whitelist-migration-event',
                        'channel-a', '123456789',
                        'legacy_login', 'Legacy Login',
                        'legacy message', 'legacy message',
                        'legacy message', 1,
                        'original-hash', 'normalized-hash',
                        'identity-fingerprint', 1,
                        1, 1, '{}', @timestamp);

                    INSERT INTO moderation_cases(
                        twitch_message_id, broadcaster_id, chatter_user_id,
                        chatter_name_snapshot, message_text_original,
                        message_text_normalized, effective_policy_hash,
                        policy_revisions_json, message_snapshot_json,
                        matched_allowlist_id, model_id, envelope_json,
                        proposed_action, proposed_timeout_seconds,
                        violation_rule_code, reason, policy_set,
                        trigger_phrases_json, confidence, status,
                        row_version, created_utc, resolved_utc)
                    VALUES(
                        'whitelist-migration-message',
                        'channel-a', '123456789',
                        'Legacy Login', 'legacy message',
                        'legacy message', 'policy-hash',
                        '{}', '{}',
                        @allowlist_id, 'model', '{}',
                        'none', NULL,
                        NULL, '', 'general',
                        '[]', NULL, 'allowlisted',
                        1, @timestamp, @timestamp);
                    """;
                fixture.Parameters.AddWithValue("@allowlist_id", allowlistId);
                fixture.Parameters.AddWithValue("@timestamp", timestamp);
                await fixture.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        await database.Initializer.InitializeAsync();

        await using var migrated = await database.OpenConnectionAsync();
        await using (var verify = migrated.CreateCommand())
        {
            verify.CommandText =
                """
                SELECT
                    broadcaster_id, subject_kind,
                    subject_value, normalized_value, enabled,
                    hit_count, last_hit_utc, row_version,
                    created_utc, updated_utc, archived_utc
                FROM whitelist_phrases
                WHERE allowlist_id = @allowlist_id;
                """;
            verify.Parameters.AddWithValue("@allowlist_id", allowlistId);
            await using var reader = await verify.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("*", reader.GetString(0));
            Assert.Equal("user_id", reader.GetString(1));
            Assert.Equal("123456789", reader.GetString(2));
            Assert.Equal("123456789", reader.GetString(3));
            Assert.Equal(0L, reader.GetInt64(4));
            Assert.Equal(7L, reader.GetInt64(5));
            Assert.Equal(timestamp, reader.GetString(6));
            Assert.Equal(9L, reader.GetInt64(7));
            Assert.Equal(timestamp, reader.GetString(8));
            Assert.Equal(timestamp, reader.GetString(9));
            Assert.Equal(timestamp, reader.GetString(10));
        }

        Assert.Equal(
            allowlistId,
            await ScalarAsync<long>(
                migrated,
                """
                SELECT matched_allowlist_id
                FROM moderation_cases
                WHERE twitch_message_id = 'whitelist-migration-message';
                """));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                migrated,
                "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                migrated,
                """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'index' AND name = 'ix_whitelist_match';
                """));

        await using (var insertLogin = migrated.CreateCommand())
        {
            insertLogin.CommandText =
                """
                INSERT INTO whitelist_phrases(
                    broadcaster_id, subject_kind,
                    subject_value, normalized_value,
                    enabled, row_version, created_utc, updated_utc)
                VALUES(
                    '*', 'user_login',
                    'MixedCaseLogin', 'mixedcaselogin',
                    1, 1, @timestamp, @timestamp);
                """;
            insertLogin.Parameters.AddWithValue("@timestamp", timestamp);
            Assert.Equal(1, await insertLogin.ExecuteNonQueryAsync());
        }

        await using var invalidKind = migrated.CreateCommand();
        invalidKind.CommandText =
            """
            INSERT INTO whitelist_phrases(
                broadcaster_id, subject_kind,
                subject_value, normalized_value,
                enabled, row_version, created_utc, updated_utc)
            VALUES(
                '*', 'user_name',
                'invalid', 'invalid',
                1, 1, @timestamp, @timestamp);
            """;
        invalidKind.Parameters.AddWithValue("@timestamp", timestamp);
        await Assert.ThrowsAsync<SqliteException>(
            () => invalidKind.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task AllowedReviewCleanup_RemovesOnlyAutomaticNegativeOnlyPhrases()
    {
        await using var database = await TestDatabase.CreateAtSchemaVersionAsync(7);
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO phrase_rules(
                    rule_id, phrase, normalized_phrase, enabled,
                    manual_override, positive_confirmations, negative_confirmations,
                    row_version, created_utc, updated_utc)
                SELECT
                    r.rule_id, seed.phrase, seed.phrase, 1,
                    seed.manual_override, seed.positive_count, seed.negative_count,
                    1, @now, @now
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                CROSS JOIN (
                    SELECT 'zorro' AS phrase, 'auto' AS manual_override,
                           0 AS positive_count, 1 AS negative_count
                    UNION ALL
                    SELECT 'nichts', 'auto', 0, 2
                    UNION ALL
                    SELECT 'manuell verboten', 'active', 0, 1
                    UNION ALL
                    SELECT 'positiv gelernt', 'auto', 1, 1
                ) seed
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'backseatgaming'
                  AND r.rule_code = 'BACKSEATGAMING'
                  AND r.archived_utc IS NULL;
                """;
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await database.Initializer.InitializeAsync();

        await using var migrated = await database.OpenConnectionAsync();
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                migrated,
                """
                SELECT COUNT(*)
                FROM phrase_rules
                WHERE normalized_phrase IN ('zorro', 'nichts');
                """));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                migrated,
                """
                SELECT COUNT(*)
                FROM phrase_rules
                WHERE normalized_phrase = 'manuell verboten'
                  AND manual_override = 'active'
                  AND negative_confirmations = 1;
                """));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                migrated,
                """
                SELECT COUNT(*)
                FROM phrase_rules
                WHERE normalized_phrase = 'positiv gelernt'
                  AND positive_confirmations = 1
                  AND negative_confirmations = 1;
                """));
    }

    [Fact]
    public async Task BackseatCriteriaMigration_ReplacesBroadRuleWithoutChangingGeneral()
    {
        await using var database = await TestDatabase.CreateAtSchemaVersionAsync(8);
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                UPDATE blacklist_rules
                SET prompt_instruction = 'too broad'
                WHERE rule_code = 'BACKSEATGAMING'
                  AND archived_utc IS NULL;

                UPDATE blacklist_rules
                SET prompt_instruction = 'general marker'
                WHERE rule_code = 'G_OTHER_ABUSE'
                  AND archived_utc IS NULL;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await database.Initializer.InitializeAsync();

        await using var migrated = await database.OpenConnectionAsync();
        Assert.Equal(
            TwitchAI.Core.Models.ModerationRuleCodes.BackseatgamingPromptInstruction,
            await ScalarAsync<string>(
                migrated,
                """
                SELECT r.prompt_instruction
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'backseatgaming'
                  AND r.rule_code = 'BACKSEATGAMING'
                  AND r.archived_utc IS NULL;
                """));
        Assert.Equal(
            "general marker",
            await ScalarAsync<string>(
                migrated,
                """
                SELECT r.prompt_instruction
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'general'
                  AND r.rule_code = 'G_OTHER_ABUSE'
                  AND r.archived_utc IS NULL;
                """));
    }

    [Fact]
    public async Task GlobalScopeMigration_MergesChannelConfigurationAndArchivesProvenance()
    {
        await using var database = await TestDatabase.CreateAtSchemaVersionAsync(4);
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO policy_sets(
                    broadcaster_id, scope_kind, set_code, category_id,
                    revision, enabled, row_version, created_utc, updated_utc)
                VALUES
                    ('legacy-channel-1', 'category', 'backseatgaming', NULL,
                     3, 1, 1, @now, @now),
                    ('legacy-channel-2', 'category', 'backseatgaming', NULL,
                     4, 1, 1, @now, @now);

                INSERT INTO blacklist_rules(
                    scope_id, rule_code, title, prompt_instruction,
                    default_action, default_timeout_seconds, public_reason,
                    priority, enabled, row_version, created_utc, updated_utc)
                SELECT
                    scope_id, 'GTA_BACKSEAT', 'Legacy Backseat',
                    'legacy instruction', 'timeout', 600, 'legacy reason',
                    100, 1, 1, @now, @now
                FROM policy_sets
                WHERE broadcaster_id IN ('legacy-channel-1', 'legacy-channel-2');

                INSERT INTO phrase_rules(
                    rule_id, phrase, normalized_phrase, enabled,
                    manual_override, positive_confirmations, negative_confirmations,
                    row_version, created_utc, updated_utc)
                SELECT
                    r.rule_id, 'hidden route', 'hidden route', 1,
                    CASE s.broadcaster_id
                        WHEN 'legacy-channel-1' THEN 'auto'
                        ELSE 'active'
                    END,
                    CASE s.broadcaster_id
                        WHEN 'legacy-channel-1' THEN 1
                        ELSE 2
                    END,
                    CASE s.broadcaster_id
                        WHEN 'legacy-channel-1' THEN 1
                        ELSE 0
                    END,
                    1, @now, @now
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id IN ('legacy-channel-1', 'legacy-channel-2');

                INSERT INTO whitelist_phrases(
                    broadcaster_id, subject_kind, subject_value, normalized_value,
                    enabled, hit_count, row_version, created_utc, updated_utc)
                VALUES
                    ('legacy-channel-1', 'exact_message',
                     'shared whitelist', 'shared whitelist',
                     1, 2, 1, @now, @now),
                    ('legacy-channel-2', 'exact_message',
                     'shared whitelist', 'shared whitelist',
                     1, 3, 1, @now, @now);

                INSERT INTO chat_message_snapshots(
                    twitch_message_id, eventsub_notification_id,
                    broadcaster_id, chatter_user_id,
                    chatter_login_original, chatter_name_original,
                    message_text_original, lower_text, message_text_normalized,
                    normalization_version, original_sha256, normalized_sha256,
                    identity_fingerprint, category_id, category_name_snapshot,
                    channel_context_revision, category_is_verified,
                    ingress_sequence, badges_json, received_utc)
                VALUES(
                    'legacy-message', 'legacy-event',
                    'legacy-channel-1', 'legacy-user',
                    'legacy-login', 'Legacy User',
                    'Hidden route', 'hidden route', 'hidden route',
                    1, 'original-hash', 'normalized-hash',
                    'legacy-fingerprint', '32982', 'Grand Theft Auto V',
                    1, 1, 1, '{}', @now);

                INSERT INTO moderation_cases(
                    twitch_message_id, broadcaster_id, chatter_user_id,
                    chatter_name_snapshot, message_text_original,
                    message_text_normalized, category_id, category_name_snapshot,
                    effective_policy_hash, policy_revisions_json,
                    message_snapshot_json, model_id, envelope_json,
                    proposed_action, proposed_timeout_seconds, proposed_rule_code,
                    reason, policy_set, trigger_phrases_json, confidence,
                    status, row_version, created_utc)
                VALUES(
                    'legacy-message', 'legacy-channel-1', 'legacy-user',
                    'Legacy User', 'Hidden route',
                    'hidden route', '32982', 'Grand Theft Auto V',
                    'legacy-policy-hash', '{}',
                    '{}', 'legacy-model', '{}',
                    'delete', NULL, 'GTA_BACKSEAT',
                    'legacy reason', 'backseatgaming', '["hidden route"]', 0.9,
                    'executed', 1, @now);

                INSERT INTO moderation_feedback(
                    case_id, operator_decision, actual_action,
                    actual_reason, eligible_for_learning,
                    human_approved, twitch_executed, learning_accepted,
                    decided_utc, review_completed)
                SELECT
                    case_id, 'executed', 'delete',
                    'legacy reason', 1,
                    1, 1, 1,
                    @now, 1
                FROM moderation_cases
                WHERE twitch_message_id = 'legacy-message';

                INSERT INTO action_attempts(
                    case_id, broadcaster_id, twitch_message_id,
                    target_user_id, action, reason, status,
                    requested_utc, completed_utc, http_status)
                SELECT
                    case_id, broadcaster_id, twitch_message_id,
                    chatter_user_id, 'delete', 'legacy reason', 'succeeded',
                    @now, @now, 204
                FROM moderation_cases
                WHERE twitch_message_id = 'legacy-message';

                INSERT INTO phrase_feedback(
                    case_id, source_kind, source_event_id, label_action,
                    rule_code, trigger_phrase, policy_set, is_positive,
                    category_id, message_normalized, eligible, confirmed_utc)
                SELECT
                    case_id, 'operator_execute', 'legacy-feedback', 'delete',
                    'GTA_BACKSEAT', 'hidden route', 'backseatgaming', 1,
                    '32982', 'hidden route', 1, @now
                FROM moderation_cases
                WHERE twitch_message_id = 'legacy-message';
                """;
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        using (var migrationInitializer =
               new TwitchAI.Infrastructure.Persistence.DatabaseInitializer(
                   database.ConnectionFactory))
        {
            await migrationInitializer.InitializeAsync();
        }

        await using var verify = await database.OpenConnectionAsync();
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                verify,
                """
                SELECT COUNT(*)
                FROM phrase_rules p
                INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND r.rule_code = 'BACKSEATGAMING'
                  AND p.normalized_phrase = 'hidden route'
                  AND p.positive_confirmations = 3
                  AND p.negative_confirmations = 1
                  AND p.manual_override = 'active'
                  AND p.archived_utc IS NULL;
                """));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                verify,
                """
                SELECT COUNT(*)
                FROM phrase_rules p
                INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id <> '*'
                  AND p.normalized_phrase = 'hidden route'
                  AND p.archived_utc IS NOT NULL;
                """));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                verify,
                """
                SELECT COUNT(*)
                FROM policy_sets
                WHERE broadcaster_id <> '*'
                  AND enabled = 0
                  AND archived_utc IS NOT NULL;
                """));
        Assert.Equal(
            5L,
            await ScalarAsync<long>(
                verify,
                """
                SELECT hit_count
                FROM whitelist_phrases
                WHERE broadcaster_id = '*'
                  AND normalized_value = 'shared whitelist'
                  AND archived_utc IS NULL;
                """));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                verify,
                """
                SELECT COUNT(*)
                FROM whitelist_phrases
                WHERE broadcaster_id <> '*'
                  AND normalized_value = 'shared whitelist'
                  AND archived_utc IS NOT NULL;
                """));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                verify,
                """
                SELECT COUNT(*)
                FROM blacklist_rules
                WHERE rule_code = 'GTA_BACKSEAT'
                  AND archived_utc IS NULL;
                """));
        Assert.Equal(
            "legacy-channel-1|BACKSEATGAMING",
            await ScalarAsync<string>(
                verify,
                """
                SELECT broadcaster_id || '|' || violation_rule_code
                FROM moderation_cases
                WHERE twitch_message_id = 'legacy-message';
                """));
        Assert.Equal(
            "BACKSEATGAMING",
            await ScalarAsync<string>(
                verify,
                """
                SELECT rule_code
                FROM phrase_feedback
                WHERE source_event_id = 'legacy-feedback';
                """));
        Assert.Equal(
            "BACKSEATGAMING|BACKSEATGAMING",
            await ScalarAsync<string>(
                verify,
                """
                SELECT
                    feedback.rule_code || '|' || attempt.rule_code
                FROM moderation_feedback feedback
                INNER JOIN action_attempts attempt
                    ON attempt.case_id = feedback.case_id
                INNER JOIN moderation_cases moderation_case
                    ON moderation_case.case_id = feedback.case_id
                WHERE moderation_case.twitch_message_id = 'legacy-message';
                """));
    }

    [Fact]
    public async Task Version6_CanonicalizesLegacyBackseatRowsAndKeepsArchivedHistory()
    {
        await using var database = await TestDatabase.CreateAtSchemaVersionAsync(5);
        long legacyRuleId;
        await using (var connection = await database.OpenConnectionAsync())
        {
            var scopeId = await ScalarAsync<long>(
                connection,
                """
                SELECT scope_id
                FROM policy_sets
                WHERE broadcaster_id = '*' AND set_code = 'backseatgaming';
                """);
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO blacklist_rules(
                    scope_id, rule_code, title, prompt_instruction,
                    default_action, default_timeout_seconds, public_reason,
                    priority, enabled, row_version, created_utc, updated_utc,
                    archived_utc)
                VALUES(
                    @scope_id, 'GTA_BACKSEAT', 'Historische GTA-Regel',
                    'legacy', 'timeout', 600, 'legacy',
                    100, 1, 3, @now, @now, @now);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("@scope_id", scopeId);
            insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            legacyRuleId = Convert.ToInt64(
                await insert.ExecuteScalarAsync(),
                System.Globalization.CultureInfo.InvariantCulture);

            await using var audit = connection.CreateCommand();
            audit.CommandText =
                """
                INSERT INTO configuration_audit(
                    entity_type, entity_id, operation, before_json, after_json,
                    change_reason, committed_utc)
                VALUES(
                    'blacklist_rules', @entity_id, 'update',
                    '{"rule_code":"GTA_BACKSEAT","title":"Unverändert"}',
                    '{"rule_code":"GTA_BACKSEAT","title":"Unverändert"}',
                    'migration fixture', @now);
                """;
            audit.Parameters.AddWithValue("@entity_id", legacyRuleId.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            audit.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            await audit.ExecuteNonQueryAsync();
        }

        await database.Initializer.InitializeAsync();

        await using var verify = await database.OpenConnectionAsync();
        Assert.Equal(
            "BACKSEATGAMING|0|3",
            await ScalarAsync<string>(
                verify,
                $"""
                 SELECT rule_code || '|' || enabled || '|' || row_version
                 FROM blacklist_rules
                 WHERE rule_id = {legacyRuleId};
                 """));
        Assert.Equal(
            2L,
            await ScalarAsync<long>(
                verify,
                """
                SELECT COUNT(*)
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'backseatgaming'
                  AND r.rule_code = 'BACKSEATGAMING';
                """));
        Assert.Equal(
            1L,
            await ScalarAsync<long>(
                verify,
                """
                SELECT COUNT(*)
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'backseatgaming'
                  AND r.rule_code = 'BACKSEATGAMING'
                  AND r.archived_utc IS NULL;
                """));
        Assert.Equal(
            "BACKSEATGAMING|BACKSEATGAMING",
            await ScalarAsync<string>(
                verify,
                """
                SELECT
                    json_extract(before_json, '$.rule_code') || '|' ||
                    json_extract(after_json, '$.rule_code')
                FROM configuration_audit
                WHERE change_reason = 'migration fixture';
                """));

        await using var duplicate = verify.CreateCommand();
        duplicate.CommandText =
            """
            INSERT INTO blacklist_rules(
                scope_id, rule_code, title, prompt_instruction,
                default_action, default_timeout_seconds, public_reason,
                priority, enabled, row_version, created_utc, updated_utc)
            SELECT
                scope_id, 'BACKSEATGAMING', 'Unzulässiges Duplikat', 'duplicate',
                'timeout', 600, 'duplicate', 100, 1, 1, @now, @now
            FROM policy_sets
            WHERE broadcaster_id = '*' AND set_code = 'backseatgaming';
            """;
        duplicate.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        await Assert.ThrowsAsync<SqliteException>(() => duplicate.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task Version7_SeedsSemanticGeneralRulesAndViolationAttributionSchema()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var connection = await database.OpenConnectionAsync();

        var expected = new Dictionary<string, (string Title, string Reason, long Priority)>(
            StringComparer.Ordinal)
        {
            ["G_SEXUALIZATION"] = (
                "Sexualisierung",
                "Sexualisierende Aussage über eine Person.",
                50),
            ["G_SEXUAL_EXPLICIT"] = (
                "Explizit sexueller Inhalt",
                "Sexuell expliziter Inhalt.",
                50),
            ["G_INSULT"] = (
                "Beleidigung",
                "Direkte Beleidigung eines Nutzers oder Streamers.",
                70),
            ["G_HARASSMENT"] = (
                "Belästigung/Mobbing",
                "Gezielte Belästigung eines Nutzers oder Streamers.",
                60),
            ["G_RACISM"] = (
                "Rassismus",
                "Rassistische oder ethnisch abwertende Aussage.",
                30),
            ["G_SEXISM"] = (
                "Sexismus",
                "Sexistische oder frauenfeindliche Aussage.",
                30),
            ["G_LGBTQ_HATE"] = (
                "Queerfeindlichkeit",
                "Queerfeindliche Aussage.",
                30),
            ["G_ABLEISM"] = (
                "Behindertenfeindlichkeit",
                "Behindertenfeindliche Aussage.",
                30),
            ["G_HATE_OTHER"] = (
                "Sonstige Hassrede",
                "Menschenfeindliche Hassrede.",
                35),
            ["G_THREAT"] = (
                "Direkte Drohung",
                "Gewaltandrohung gegen eine Person.",
                20),
            ["G_VIOLENCE"] = (
                "Gewaltverherrlichung",
                "Gewaltverherrlichender Inhalt.",
                40),
            ["G_PERSONAL_DATA"] = (
                "Persönliche Daten/Doxxing",
                "Veröffentlichung persönlicher Daten.",
                10),
            ["G_SELF_HARM_INCITEMENT"] = (
                "Aufforderung zur Selbstverletzung",
                "Aufforderung zu Selbstverletzung.",
                20),
            ["G_SPAM"] = (
                "Spam",
                "Wiederholter Spam.",
                80),
            ["G_SCAM"] = (
                "Betrug/Phishing",
                "Betrugs- oder Phishingversuch.",
                80),
            ["G_DISRUPTION"] = (
                "Chatstörung",
                "Gezielte Störung des Chats.",
                80),
            ["G_ADVERTISING"] = (
                "Werbung",
                "Unaufgeforderte Werbung oder Eigenwerbung.",
                75),
            ["G_BEGGING"] = (
                "Gebettel",
                "Betteln nach Subs, Geschenken oder Giveaways.",
                76),
            ["G_OTHER_ABUSE"] = (
                "Sonstiger General-Verstoß",
                "Sonstiger Verstoß gegen die Chatregeln.",
                100),
        };

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT
                    r.rule_code, r.title, r.public_reason, r.priority,
                    r.default_action, r.default_timeout_seconds
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND s.set_code = 'general'
                  AND s.archived_utc IS NULL
                  AND r.archived_utc IS NULL
                ORDER BY r.rule_code;
                """;
            await using var reader = await command.ExecuteReaderAsync();
            var actualCodes = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync())
            {
                var code = reader.GetString(0);
                actualCodes.Add(code);
                var definition = expected[code];
                Assert.Equal(definition.Title, reader.GetString(1));
                Assert.Equal(definition.Reason, reader.GetString(2));
                Assert.Equal(definition.Priority, reader.GetInt64(3));
                Assert.Equal("timeout", reader.GetString(4));
                Assert.Equal(
                    code is "G_ADVERTISING" or "G_BEGGING" ? 120 : 600,
                    reader.GetInt64(5));
            }

            Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actualCodes.Order(StringComparer.Ordinal));
        }

        Assert.Equal(
            "sie sieht sexy aus|sie sieht sexy aus|1|active|0|0",
            await ScalarAsync<string>(
                connection,
                """
                SELECT
                    p.phrase || '|' || p.normalized_phrase || '|' ||
                    p.enabled || '|' || p.manual_override || '|' ||
                    p.positive_confirmations || '|' || p.negative_confirmations
                FROM phrase_rules p
                INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND r.rule_code = 'G_SEXUALIZATION'
                  AND p.normalized_phrase = 'sie sieht sexy aus';
                """));

        var caseColumns = await ReadStringsAsync(
            connection,
            "SELECT name FROM pragma_table_info('moderation_cases') ORDER BY cid;");
        Assert.Contains("violation_rule_code", caseColumns);
        Assert.DoesNotContain("proposed_rule_code", caseColumns);
        Assert.Contains(
            "rule_code",
            await ReadStringsAsync(
                connection,
                "SELECT name FROM pragma_table_info('moderation_feedback') ORDER BY cid;"));
        Assert.Contains(
            "rule_code",
            await ReadStringsAsync(
                connection,
                "SELECT name FROM pragma_table_info('action_attempts') ORDER BY cid;"));

        var deletionAuditColumns = await ReadStringsAsync(
            connection,
            "SELECT name FROM pragma_table_info('data_deletion_audit') ORDER BY cid;");
        Assert.Contains("target_id_sha256", deletionAuditColumns);
        Assert.DoesNotContain("target_record_id", deletionAuditColumns);
    }

    [Fact]
    public async Task RiskyMigrations_CreateVerifiedBackupsAndRestoreForeignKeys()
    {
        await using var database = await TestDatabase.CreateAtSchemaVersionAsync(5);

        await database.Initializer.InitializeAsync();

        var directory = Path.GetDirectoryName(database.Path);
        Assert.NotNull(directory);
        var backups = Directory.GetFiles(
            directory,
            $"{Path.GetFileName(database.Path)}.pre-schema-v*.bak");
        var expectedBackups =
            TwitchAI.Infrastructure.Persistence.SchemaMigrations.All.Count(
                migration => migration.Version > 5 && migration.RequiresBackup);
        Assert.Equal(expectedBackups, backups.Length);

        foreach (var backupPath in backups)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                ForeignKeys = true,
            }.ToString();
            await using var backup = new SqliteConnection(connectionString);
            await backup.OpenAsync();
            Assert.Equal(
                "ok",
                await ScalarAsync<string>(backup, "PRAGMA integrity_check;"),
                ignoreCase: true);
            Assert.Equal(
                0L,
                await ScalarAsync<long>(
                    backup,
                    "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        }

        await using var final = await database.OpenConnectionAsync();
        Assert.Equal(1L, await ScalarAsync<long>(final, "PRAGMA foreign_keys;"));
        Assert.Equal(
            0L,
            await ScalarAsync<long>(
                final,
                "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    private static async Task<IReadOnlyList<string>> ReadStringsAsync(
        SqliteConnection connection,
        string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static async Task<T> ScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var result = await command.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (T)Convert.ChangeType(
            result,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
