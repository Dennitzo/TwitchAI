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
        Assert.Equal(["32982", "509658"], categories);

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

        Assert.Equal(9, migrationCount);
        Assert.Equal("wal", journalMode, ignoreCase: true);
        Assert.Equal(1, foreignKeys);
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
                "Direkte Beleidigung gegen einen Nutzer.",
                70),
            ["G_HARASSMENT"] = (
                "Belästigung/Mobbing",
                "Gezielte Belästigung eines Nutzers.",
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
                Assert.Equal(600, reader.GetInt64(5));
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
        Assert.Equal(2, backups.Length);

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
