namespace TwitchAI.Infrastructure.Persistence;

internal sealed record SchemaMigration(
    long Version,
    string Description,
    string Sql,
    bool RequiresForeignKeysDisabled = false,
    bool RequiresBackup = false);

internal static class SchemaMigrations
{
    public static IReadOnlyList<SchemaMigration> All { get; } =
    [
        new(
            1,
            "Initial moderation, policy, audit, feedback, and transport schema",
            """
            CREATE TABLE twitch_categories (
                category_id       TEXT PRIMARY KEY,
                display_name      TEXT NOT NULL,
                last_seen_utc     TEXT NOT NULL,
                last_verified_utc TEXT
            ) STRICT;

            CREATE TABLE channel_context (
                broadcaster_id       TEXT PRIMARY KEY,
                broadcaster_login    TEXT NOT NULL,
                broadcaster_name     TEXT NOT NULL,
                moderator_user_id    TEXT NOT NULL,
                category_id          TEXT,
                category_name        TEXT,
                state_observed_utc    TEXT NOT NULL,
                revision              INTEGER NOT NULL DEFAULT 1 CHECK(revision > 0),
                is_verified           INTEGER NOT NULL DEFAULT 1 CHECK(is_verified IN (0,1)),
                FOREIGN KEY(category_id) REFERENCES twitch_categories(category_id)
            ) STRICT;

            CREATE TABLE policy_sets (
                scope_id       INTEGER PRIMARY KEY,
                broadcaster_id TEXT NOT NULL,
                scope_kind     TEXT NOT NULL CHECK(scope_kind IN ('general','category')),
                set_code       TEXT NOT NULL DEFAULT 'general'
                               CHECK(set_code IN ('general','backseatgaming')),
                category_id    TEXT,
                revision       INTEGER NOT NULL DEFAULT 1 CHECK(revision > 0),
                enabled        INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
                row_version    INTEGER NOT NULL DEFAULT 1 CHECK(row_version > 0),
                created_utc    TEXT NOT NULL,
                updated_utc    TEXT NOT NULL,
                archived_utc   TEXT,
                CHECK (
                    (scope_kind = 'general' AND set_code = 'general'
                     AND category_id IS NULL) OR
                    (scope_kind = 'category' AND set_code = 'backseatgaming'
                     AND category_id IS NULL)
                ),
                FOREIGN KEY(category_id) REFERENCES twitch_categories(category_id)
            ) STRICT;

            CREATE UNIQUE INDEX ux_policy_scope_general
                ON policy_sets(broadcaster_id)
                WHERE scope_kind = 'general';

            CREATE UNIQUE INDEX ux_policy_set_code
                ON policy_sets(broadcaster_id, set_code);

            CREATE TABLE blacklist_rules (
                rule_id                  INTEGER PRIMARY KEY,
                scope_id                 INTEGER NOT NULL,
                rule_code                TEXT NOT NULL,
                title                    TEXT NOT NULL,
                prompt_instruction       TEXT NOT NULL,
                default_action           TEXT NOT NULL CHECK(default_action IN ('delete','timeout')),
                default_timeout_seconds  INTEGER,
                public_reason            TEXT NOT NULL,
                priority                 INTEGER NOT NULL DEFAULT 100,
                enabled                  INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
                row_version              INTEGER NOT NULL DEFAULT 1 CHECK(row_version > 0),
                created_utc              TEXT NOT NULL,
                updated_utc              TEXT NOT NULL,
                archived_utc             TEXT,
                UNIQUE(scope_id, rule_code),
                CHECK (
                    (default_action = 'delete' AND default_timeout_seconds IS NULL) OR
                    (default_action = 'timeout' AND
                     default_timeout_seconds BETWEEN 1 AND 1209600)
                ),
                FOREIGN KEY(scope_id) REFERENCES policy_sets(scope_id) ON DELETE RESTRICT
            ) STRICT;

            CREATE INDEX ix_blacklist_rules_scope
                ON blacklist_rules(scope_id, enabled, archived_utc, priority, rule_code);

            CREATE TABLE phrase_rules (
                phrase_id          INTEGER PRIMARY KEY,
                rule_id            INTEGER NOT NULL,
                phrase             TEXT NOT NULL CHECK(length(phrase) BETWEEN 2 AND 500),
                normalized_phrase  TEXT NOT NULL CHECK(length(normalized_phrase) BETWEEN 2 AND 500),
                enabled            INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
                manual_override    TEXT NOT NULL DEFAULT 'auto'
                                   CHECK(manual_override IN ('auto','active','inactive')),
                positive_confirmations INTEGER NOT NULL DEFAULT 0
                                       CHECK(positive_confirmations >= 0),
                negative_confirmations INTEGER NOT NULL DEFAULT 0
                                       CHECK(negative_confirmations >= 0),
                row_version        INTEGER NOT NULL DEFAULT 1 CHECK(row_version > 0),
                created_utc        TEXT NOT NULL,
                updated_utc        TEXT NOT NULL,
                archived_utc       TEXT,
                UNIQUE(rule_id, normalized_phrase),
                FOREIGN KEY(rule_id) REFERENCES blacklist_rules(rule_id) ON DELETE RESTRICT
            ) STRICT;

            CREATE INDEX ix_phrase_rules_rule
                ON phrase_rules(rule_id, enabled, archived_utc);

            CREATE TABLE whitelist_phrases (
                allowlist_id       INTEGER PRIMARY KEY,
                broadcaster_id     TEXT NOT NULL,
                subject_kind       TEXT NOT NULL CHECK(subject_kind IN ('user_id','exact_message')),
                subject_value      TEXT NOT NULL,
                normalized_value   TEXT NOT NULL,
                enabled            INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
                hit_count          INTEGER NOT NULL DEFAULT 0 CHECK(hit_count >= 0),
                last_hit_utc       TEXT,
                row_version        INTEGER NOT NULL DEFAULT 1 CHECK(row_version > 0),
                created_utc        TEXT NOT NULL,
                updated_utc        TEXT NOT NULL,
                archived_utc       TEXT,
                UNIQUE(broadcaster_id, subject_kind, normalized_value)
            ) STRICT;

            CREATE INDEX ix_whitelist_match
                ON whitelist_phrases(broadcaster_id, subject_kind, normalized_value)
                WHERE enabled = 1 AND archived_utc IS NULL;

            CREATE TABLE chat_message_snapshots (
                twitch_message_id        TEXT PRIMARY KEY,
                eventsub_notification_id  TEXT NOT NULL UNIQUE,
                broadcaster_id           TEXT NOT NULL,
                chatter_user_id          TEXT NOT NULL,
                chatter_login_original   TEXT NOT NULL,
                chatter_name_original    TEXT NOT NULL,
                message_text_original    TEXT NOT NULL,
                lower_text                TEXT NOT NULL,
                message_text_normalized  TEXT NOT NULL,
                normalization_version    INTEGER NOT NULL,
                original_sha256           TEXT NOT NULL,
                normalized_sha256        TEXT NOT NULL,
                identity_fingerprint      TEXT NOT NULL,
                category_id              TEXT,
                category_name_snapshot   TEXT,
                channel_context_revision INTEGER NOT NULL DEFAULT 1,
                category_is_verified      INTEGER NOT NULL DEFAULT 1 CHECK(category_is_verified IN (0,1)),
                ingress_sequence          INTEGER NOT NULL DEFAULT 0,
                shared_source_channel_id  TEXT,
                badges_json               TEXT NOT NULL DEFAULT '{}',
                received_utc             TEXT NOT NULL,
                FOREIGN KEY(category_id) REFERENCES twitch_categories(category_id)
            ) STRICT;

            CREATE INDEX ix_chat_snapshots_broadcaster_received
                ON chat_message_snapshots(broadcaster_id, received_utc DESC);

            CREATE INDEX ix_chat_snapshots_normalized_hash
                ON chat_message_snapshots(broadcaster_id, normalized_sha256);

            CREATE TABLE moderation_cases (
                case_id                    INTEGER PRIMARY KEY,
                twitch_message_id          TEXT NOT NULL UNIQUE,
                broadcaster_id             TEXT NOT NULL,
                chatter_user_id            TEXT NOT NULL,
                chatter_name_snapshot      TEXT NOT NULL,
                message_text_original      TEXT NOT NULL,
                message_text_normalized    TEXT NOT NULL,
                category_id                TEXT,
                category_name_snapshot     TEXT,
                effective_policy_hash      TEXT NOT NULL,
                policy_revisions_json      TEXT NOT NULL DEFAULT '{}',
                message_snapshot_json      TEXT NOT NULL DEFAULT '{}',
                matched_allowlist_id       INTEGER,
                model_id                   TEXT NOT NULL,
                envelope_json              TEXT NOT NULL,
                proposed_action            TEXT NOT NULL CHECK(proposed_action IN ('none','delete','timeout')),
                proposed_timeout_seconds   INTEGER,
                proposed_rule_code         TEXT,
                reason                     TEXT NOT NULL DEFAULT '',
                policy_set                 TEXT NOT NULL DEFAULT 'general'
                                           CHECK(policy_set IN ('general','backseatgaming')),
                trigger_phrases_json        TEXT NOT NULL DEFAULT '[]',
                confidence                 REAL,
                status                     TEXT NOT NULL CHECK(status IN (
                    'pending','executed','discarded','allowlisted','externally_deleted',
                    'executing','failed','stale_policy','integrity_blocked',
                    'externally_completed','outcome_unknown','skipped_overload')),
                row_version                INTEGER NOT NULL DEFAULT 1 CHECK(row_version > 0),
                created_utc                TEXT NOT NULL,
                resolved_utc               TEXT,
                CHECK (
                    (proposed_action = 'timeout' AND
                     proposed_timeout_seconds BETWEEN 1 AND 1209600) OR
                    (proposed_action <> 'timeout' AND proposed_timeout_seconds IS NULL)
                ),
                FOREIGN KEY(twitch_message_id) REFERENCES chat_message_snapshots(twitch_message_id),
                FOREIGN KEY(category_id) REFERENCES twitch_categories(category_id),
                FOREIGN KEY(matched_allowlist_id) REFERENCES whitelist_phrases(allowlist_id)
            ) STRICT;

            CREATE INDEX ix_moderation_cases_status_created
                ON moderation_cases(broadcaster_id, status, created_utc DESC);

            CREATE TABLE moderation_feedback (
                feedback_id             INTEGER PRIMARY KEY,
                case_id                 INTEGER NOT NULL UNIQUE,
                operator_decision       TEXT NOT NULL CHECK(operator_decision IN ('executed','discarded')),
                actual_action           TEXT NOT NULL CHECK(actual_action IN ('none','delete','timeout')),
                actual_timeout_seconds  INTEGER,
                actual_reason           TEXT,
                discard_reason          TEXT,
                eligible_for_learning   INTEGER NOT NULL CHECK(eligible_for_learning IN (0,1)),
                human_approved          INTEGER NOT NULL DEFAULT 1 CHECK(human_approved IN (0,1)),
                twitch_executed         INTEGER NOT NULL DEFAULT 0 CHECK(twitch_executed IN (0,1)),
                learning_accepted       INTEGER NOT NULL DEFAULT 0 CHECK(learning_accepted IN (0,1)),
                decided_utc             TEXT NOT NULL,
                CHECK (
                    (actual_action = 'timeout' AND
                     actual_timeout_seconds BETWEEN 1 AND 1209600) OR
                    (actual_action <> 'timeout' AND actual_timeout_seconds IS NULL)
                ),
                FOREIGN KEY(case_id) REFERENCES moderation_cases(case_id) ON DELETE RESTRICT
            ) STRICT;

            CREATE TABLE action_attempts (
                execution_id       INTEGER PRIMARY KEY,
                case_id            INTEGER NOT NULL,
                broadcaster_id     TEXT NOT NULL,
                twitch_message_id  TEXT NOT NULL,
                target_user_id     TEXT NOT NULL,
                action             TEXT NOT NULL CHECK(action IN ('delete','timeout')),
                timeout_seconds    INTEGER,
                reason             TEXT,
                status             TEXT NOT NULL CHECK(status IN (
                    'intent','succeeded','failed','outcome_unknown')),
                requested_utc      TEXT NOT NULL,
                completed_utc      TEXT,
                http_status        INTEGER,
                error_code         TEXT,
                response_json      TEXT,
                CHECK (
                    (action = 'timeout' AND timeout_seconds BETWEEN 1 AND 1209600) OR
                    (action = 'delete' AND timeout_seconds IS NULL)
                ),
                FOREIGN KEY(case_id) REFERENCES moderation_cases(case_id) ON DELETE RESTRICT,
                FOREIGN KEY(twitch_message_id) REFERENCES chat_message_snapshots(twitch_message_id)
            ) STRICT;

            CREATE INDEX ix_action_attempt_message
                ON action_attempts(
                    broadcaster_id, twitch_message_id, action, requested_utc DESC);

            CREATE UNIQUE INDEX ux_action_attempt_case
                ON action_attempts(case_id);

            CREATE TABLE message_availability_events (
                availability_event_id INTEGER PRIMARY KEY,
                eventsub_message_id  TEXT NOT NULL UNIQUE,
                broadcaster_id       TEXT NOT NULL,
                twitch_message_id    TEXT,
                target_user_id       TEXT,
                availability         TEXT NOT NULL,
                occurred_utc         TEXT NOT NULL,
                received_utc         TEXT NOT NULL,
                correlation_kind     TEXT NOT NULL CHECK(correlation_kind IN (
                    'local','local_or_ambiguous','external_unmatched','orphan')),
                learning_state       TEXT NOT NULL CHECK(learning_state IN (
                    'unavailable','pending_review','accepted','ignored')),
                case_id              INTEGER,
                FOREIGN KEY(case_id) REFERENCES moderation_cases(case_id)
            ) STRICT;

            CREATE TABLE phrase_feedback (
                feedback_id         INTEGER PRIMARY KEY,
                case_id             INTEGER NOT NULL,
                source_kind         TEXT NOT NULL CHECK(source_kind IN (
                    'operator_execute','operator_discard','external_delete')),
                source_event_id     TEXT,
                label_action        TEXT NOT NULL CHECK(label_action IN ('none','delete','timeout')),
                rule_code           TEXT,
                trigger_phrase      TEXT NOT NULL,
                policy_set          TEXT NOT NULL CHECK(policy_set IN ('general','backseatgaming')),
                is_positive         INTEGER NOT NULL CHECK(is_positive IN (0,1)),
                category_id         TEXT,
                message_normalized  TEXT NOT NULL,
                eligible            INTEGER NOT NULL CHECK(eligible IN (0,1)),
                confirmed_utc       TEXT NOT NULL,
                FOREIGN KEY(case_id) REFERENCES moderation_cases(case_id) ON DELETE RESTRICT,
                FOREIGN KEY(category_id) REFERENCES twitch_categories(category_id),
                UNIQUE(source_kind, source_event_id)
            ) STRICT;

            CREATE INDEX ix_phrase_feedback_retrieval
                ON phrase_feedback(
                    eligible, rule_code, category_id, confirmed_utc DESC);

            CREATE TABLE configuration_audit (
                change_id       INTEGER PRIMARY KEY,
                entity_type     TEXT NOT NULL,
                entity_id       TEXT NOT NULL,
                operation       TEXT NOT NULL CHECK(operation IN (
                    'insert','update','archive','restore')),
                before_json     TEXT,
                after_json      TEXT,
                scope_id        INTEGER,
                change_reason   TEXT NOT NULL,
                committed_utc   TEXT NOT NULL,
                FOREIGN KEY(scope_id) REFERENCES policy_sets(scope_id)
            ) STRICT;

            CREATE INDEX ix_configuration_audit_entity
                ON configuration_audit(entity_type, entity_id, committed_utc DESC);

            CREATE TABLE processed_eventsub_events (
                eventsub_message_id  TEXT PRIMARY KEY,
                subscription_type    TEXT NOT NULL,
                received_utc         TEXT NOT NULL
            ) STRICT;

            CREATE INDEX ix_processed_events_received
                ON processed_eventsub_events(received_utc);

            CREATE TABLE ai_run_metrics (
                request_id          INTEGER PRIMARY KEY,
                correlation_id      TEXT NOT NULL,
                model_id            TEXT NOT NULL,
                policy_hash         TEXT NOT NULL,
                message_count       INTEGER NOT NULL CHECK(message_count > 0),
                prompt_tokens       INTEGER,
                completion_tokens   INTEGER,
                total_tokens        INTEGER,
                elapsed_ms          INTEGER NOT NULL CHECK(elapsed_ms >= 0),
                finish_reason       TEXT,
                succeeded           INTEGER NOT NULL CHECK(succeeded IN (0,1)),
                error_code          TEXT,
                requested_utc       TEXT NOT NULL
            ) STRICT;

            CREATE INDEX ix_ai_run_metrics_requested
                ON ai_run_metrics(requested_utc DESC);

            CREATE TABLE policy_category_exclusions (
                exclusion_id     INTEGER PRIMARY KEY,
                scope_id         INTEGER NOT NULL,
                category_id      TEXT NOT NULL,
                enabled          INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
                row_version      INTEGER NOT NULL DEFAULT 1 CHECK(row_version > 0),
                created_utc      TEXT NOT NULL,
                updated_utc      TEXT NOT NULL,
                archived_utc     TEXT,
                UNIQUE(scope_id, category_id),
                FOREIGN KEY(scope_id) REFERENCES policy_sets(scope_id) ON DELETE RESTRICT,
                FOREIGN KEY(category_id) REFERENCES twitch_categories(category_id)
            ) STRICT;

            CREATE TABLE message_clear_watermarks (
                watermark_id       INTEGER PRIMARY KEY,
                broadcaster_id     TEXT NOT NULL,
                target_user_id     TEXT,
                clear_kind         TEXT NOT NULL CHECK(clear_kind IN ('user','room')),
                cleared_utc        TEXT NOT NULL,
                availability       TEXT NOT NULL,
                revision           INTEGER NOT NULL DEFAULT 1 CHECK(revision > 0),
                eventsub_message_id TEXT NOT NULL UNIQUE,
                CHECK (
                    (clear_kind = 'user' AND target_user_id IS NOT NULL) OR
                    (clear_kind = 'room' AND target_user_id IS NULL)
                )
            ) STRICT;

            CREATE INDEX ix_message_clear_watermark_lookup
                ON message_clear_watermarks(
                    broadcaster_id, target_user_id, clear_kind, cleared_utc DESC);

            CREATE UNIQUE INDEX ux_message_clear_watermark_subject
                ON message_clear_watermarks(
                    broadcaster_id, IFNULL(target_user_id, ''), clear_kind);

            CREATE TABLE record_annotations (
                annotation_id    INTEGER PRIMARY KEY,
                record_type      TEXT NOT NULL,
                record_id        TEXT NOT NULL,
                annotation_kind  TEXT NOT NULL,
                annotation_text  TEXT NOT NULL,
                created_utc      TEXT NOT NULL,
                UNIQUE(record_type, record_id, annotation_kind)
            ) STRICT;

            INSERT INTO twitch_categories(
                category_id, display_name, last_seen_utc, last_verified_utc)
            VALUES
                ('32982', 'Grand Theft Auto V', strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                 NULL),
                ('509658', 'Just Chatting', strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                 NULL);

            INSERT INTO whitelist_phrases(
                broadcaster_id, subject_kind, subject_value, normalized_value,
                enabled, row_version, created_utc, updated_utc)
            VALUES(
                '*', 'exact_message', 'KEKW', 'kekw',
                1, 1, strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                strftime('%Y-%m-%dT%H:%M:%fZ','now'));
            """),
        new(
            2,
            "Track completion of externally-unavailable moderation reviews",
            """
            ALTER TABLE moderation_feedback
            ADD COLUMN review_completed INTEGER NOT NULL DEFAULT 0
                CHECK(review_completed IN (0,1));
            """),
        new(
            3,
            "Persist moderation batch provenance, local matches, and AI latency",
            """
            ALTER TABLE moderation_cases
            ADD COLUMN batch_id TEXT;

            ALTER TABLE moderation_cases
            ADD COLUMN local_match_codes_json TEXT NOT NULL DEFAULT '[]';

            ALTER TABLE moderation_cases
            ADD COLUMN ai_latency_ms INTEGER
                CHECK(ai_latency_ms IS NULL OR ai_latency_ms >= 0);
            """),
        new(
            4,
            "Use 600 seconds as the default timeout for built-in policies",
            """
            UPDATE blacklist_rules
            SET default_timeout_seconds = 600,
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE rule_code IN ('GENERAL_PHRASE', 'GTA_BACKSEAT')
              AND default_action = 'timeout'
              AND default_timeout_seconds = 300;

            UPDATE policy_sets
            SET revision = revision + 1,
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE scope_id IN (
                SELECT DISTINCT scope_id
                FROM blacklist_rules
                WHERE rule_code IN ('GENERAL_PHRASE', 'GTA_BACKSEAT')
                  AND default_action = 'timeout'
                  AND default_timeout_seconds = 600
            );
            """),
        new(
            5,
            "Share moderation configuration across channels and generalize backseatgaming",
            """
            -- Policy configuration is owned by the global '*' scope. Twitch channel ids
            -- remain on messages, cases, feedback, attempts, and audit records.
            INSERT OR IGNORE INTO policy_sets(
                broadcaster_id, scope_kind, set_code, category_id,
                revision, enabled, row_version, created_utc, updated_utc)
            VALUES(
                '*', 'general', 'general', NULL,
                1, 1, 1,
                strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                strftime('%Y-%m-%dT%H:%M:%fZ','now'));

            INSERT OR IGNORE INTO policy_sets(
                broadcaster_id, scope_kind, set_code, category_id,
                revision, enabled, row_version, created_utc, updated_utc)
            VALUES(
                '*', 'category', 'backseatgaming', NULL,
                1, 1, 1,
                strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                strftime('%Y-%m-%dT%H:%M:%fZ','now'));

            INSERT OR IGNORE INTO blacklist_rules(
                scope_id, rule_code, title, prompt_instruction,
                default_action, default_timeout_seconds, public_reason,
                priority, enabled, row_version, created_utc, updated_utc)
            SELECT
                scope_id, 'GENERAL_PHRASE', 'Allgemeine Blacklist',
                'Lokale Treffer als möglichen allgemeinen Moderationshinweis bewerten.',
                'timeout', 600, 'Verstoß gegen die allgemeinen Chatregeln',
                100, 1, 1,
                strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM policy_sets
            WHERE broadcaster_id = '*' AND set_code = 'general';

            INSERT OR IGNORE INTO blacklist_rules(
                scope_id, rule_code, title, prompt_instruction,
                default_action, default_timeout_seconds, public_reason,
                priority, enabled, row_version, created_utc, updated_utc)
            SELECT
                scope_id, 'BACKSEATGAMING', 'Backseatgaming',
                'Unaufgeforderte Spielanweisungen, Lösungen, Spoiler oder Hinweise auf kommende Gefahren als Backseatgaming bewerten; ausdrückliche Hilfeanfragen nicht bestrafen.',
                'timeout', 600, 'Unaufgefordertes Backseatgaming',
                100, 1, 1,
                strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM policy_sets
            WHERE broadcaster_id = '*' AND set_code = 'backseatgaming';

            -- Snapshot all current phrase configuration before archiving the old
            -- channel scopes. Duplicate phrases are merged without discarding their
            -- accumulated positive/negative learning counters.
            CREATE TEMP TABLE twitchai_global_phrase_merge AS
            SELECT
                s.set_code,
                p.normalized_phrase,
                MIN(p.phrase) AS phrase,
                MAX(p.enabled) AS enabled,
                CASE
                    WHEN MAX(CASE p.manual_override WHEN 'active' THEN 1 ELSE 0 END) = 1
                        THEN 'active'
                    WHEN MIN(CASE p.manual_override WHEN 'inactive' THEN 1 ELSE 0 END) = 1
                        THEN 'inactive'
                    ELSE 'auto'
                END AS manual_override,
                SUM(p.positive_confirmations) AS positive_confirmations,
                SUM(p.negative_confirmations) AS negative_confirmations,
                MIN(p.created_utc) AS created_utc,
                MAX(p.updated_utc) AS updated_utc
            FROM phrase_rules p
            INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
            INNER JOIN policy_sets s ON s.scope_id = r.scope_id
            WHERE p.archived_utc IS NULL
              AND r.archived_utc IS NULL
              AND s.archived_utc IS NULL
              AND s.set_code IN ('general', 'backseatgaming')
            GROUP BY s.set_code, p.normalized_phrase;

            INSERT OR IGNORE INTO phrase_rules(
                rule_id, phrase, normalized_phrase, enabled,
                manual_override, positive_confirmations, negative_confirmations,
                row_version, created_utc, updated_utc)
            SELECT
                r.rule_id,
                m.phrase,
                m.normalized_phrase,
                m.enabled,
                m.manual_override,
                m.positive_confirmations,
                m.negative_confirmations,
                1,
                m.created_utc,
                m.updated_utc
            FROM twitchai_global_phrase_merge m
            INNER JOIN policy_sets s
                ON s.broadcaster_id = '*' AND s.set_code = m.set_code
            INNER JOIN blacklist_rules r
                ON r.scope_id = s.scope_id
               AND r.rule_code = CASE m.set_code
                   WHEN 'general' THEN 'GENERAL_PHRASE'
                   ELSE 'BACKSEATGAMING'
               END;

            UPDATE phrase_rules
            SET
                phrase = (
                    SELECT m.phrase
                    FROM twitchai_global_phrase_merge m
                    INNER JOIN blacklist_rules r2 ON r2.rule_id = phrase_rules.rule_id
                    INNER JOIN policy_sets s2 ON s2.scope_id = r2.scope_id
                    WHERE s2.broadcaster_id = '*'
                      AND m.set_code = s2.set_code
                      AND m.normalized_phrase = phrase_rules.normalized_phrase
                ),
                enabled = (
                    SELECT m.enabled
                    FROM twitchai_global_phrase_merge m
                    INNER JOIN blacklist_rules r2 ON r2.rule_id = phrase_rules.rule_id
                    INNER JOIN policy_sets s2 ON s2.scope_id = r2.scope_id
                    WHERE s2.broadcaster_id = '*'
                      AND m.set_code = s2.set_code
                      AND m.normalized_phrase = phrase_rules.normalized_phrase
                ),
                manual_override = (
                    SELECT m.manual_override
                    FROM twitchai_global_phrase_merge m
                    INNER JOIN blacklist_rules r2 ON r2.rule_id = phrase_rules.rule_id
                    INNER JOIN policy_sets s2 ON s2.scope_id = r2.scope_id
                    WHERE s2.broadcaster_id = '*'
                      AND m.set_code = s2.set_code
                      AND m.normalized_phrase = phrase_rules.normalized_phrase
                ),
                positive_confirmations = (
                    SELECT m.positive_confirmations
                    FROM twitchai_global_phrase_merge m
                    INNER JOIN blacklist_rules r2 ON r2.rule_id = phrase_rules.rule_id
                    INNER JOIN policy_sets s2 ON s2.scope_id = r2.scope_id
                    WHERE s2.broadcaster_id = '*'
                      AND m.set_code = s2.set_code
                      AND m.normalized_phrase = phrase_rules.normalized_phrase
                ),
                negative_confirmations = (
                    SELECT m.negative_confirmations
                    FROM twitchai_global_phrase_merge m
                    INNER JOIN blacklist_rules r2 ON r2.rule_id = phrase_rules.rule_id
                    INNER JOIN policy_sets s2 ON s2.scope_id = r2.scope_id
                    WHERE s2.broadcaster_id = '*'
                      AND m.set_code = s2.set_code
                      AND m.normalized_phrase = phrase_rules.normalized_phrase
                ),
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE rule_id IN (
                SELECT r.rule_id
                FROM blacklist_rules r
                INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                WHERE s.broadcaster_id = '*'
                  AND r.rule_code IN ('GENERAL_PHRASE', 'BACKSEATGAMING')
            )
              AND EXISTS (
                  SELECT 1
                  FROM twitchai_global_phrase_merge m
                  INNER JOIN blacklist_rules r2 ON r2.rule_id = phrase_rules.rule_id
                  INNER JOIN policy_sets s2 ON s2.scope_id = r2.scope_id
                  WHERE m.set_code = s2.set_code
                    AND m.normalized_phrase = phrase_rules.normalized_phrase
              );

            DROP TABLE twitchai_global_phrase_merge;

            INSERT OR IGNORE INTO policy_category_exclusions(
                scope_id, category_id, enabled, row_version,
                created_utc, updated_utc)
            SELECT
                scope_id, '509658', 1, 1,
                strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM policy_sets
            WHERE broadcaster_id = '*' AND set_code = 'backseatgaming';

            -- Whitelist entries are also global. Existing channel rows remain as
            -- archived provenance while their values and hit counts are merged.
            CREATE TEMP TABLE twitchai_global_whitelist_merge AS
            SELECT
                subject_kind,
                normalized_value,
                MIN(subject_value) AS subject_value,
                MAX(enabled) AS enabled,
                SUM(hit_count) AS hit_count,
                MAX(last_hit_utc) AS last_hit_utc,
                MIN(created_utc) AS created_utc,
                MAX(updated_utc) AS updated_utc
            FROM whitelist_phrases
            WHERE archived_utc IS NULL
            GROUP BY subject_kind, normalized_value;

            INSERT OR IGNORE INTO whitelist_phrases(
                broadcaster_id, subject_kind, subject_value, normalized_value,
                enabled, hit_count, last_hit_utc, row_version,
                created_utc, updated_utc)
            SELECT
                '*', subject_kind, subject_value, normalized_value,
                enabled, hit_count, last_hit_utc, 1,
                created_utc, updated_utc
            FROM twitchai_global_whitelist_merge;

            UPDATE whitelist_phrases
            SET
                subject_value = (
                    SELECT m.subject_value
                    FROM twitchai_global_whitelist_merge m
                    WHERE m.subject_kind = whitelist_phrases.subject_kind
                      AND m.normalized_value = whitelist_phrases.normalized_value
                ),
                enabled = (
                    SELECT m.enabled
                    FROM twitchai_global_whitelist_merge m
                    WHERE m.subject_kind = whitelist_phrases.subject_kind
                      AND m.normalized_value = whitelist_phrases.normalized_value
                ),
                hit_count = (
                    SELECT m.hit_count
                    FROM twitchai_global_whitelist_merge m
                    WHERE m.subject_kind = whitelist_phrases.subject_kind
                      AND m.normalized_value = whitelist_phrases.normalized_value
                ),
                last_hit_utc = (
                    SELECT m.last_hit_utc
                    FROM twitchai_global_whitelist_merge m
                    WHERE m.subject_kind = whitelist_phrases.subject_kind
                      AND m.normalized_value = whitelist_phrases.normalized_value
                ),
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE broadcaster_id = '*'
              AND EXISTS (
                  SELECT 1
                  FROM twitchai_global_whitelist_merge m
                  WHERE m.subject_kind = whitelist_phrases.subject_kind
                    AND m.normalized_value = whitelist_phrases.normalized_value
              );

            DROP TABLE twitchai_global_whitelist_merge;

            -- Preserve the former channel-owned rows as archived history.
            UPDATE phrase_rules
            SET archived_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE archived_utc IS NULL
              AND rule_id IN (
                  SELECT r.rule_id
                  FROM blacklist_rules r
                  INNER JOIN policy_sets s ON s.scope_id = r.scope_id
                  WHERE s.broadcaster_id <> '*'
                     OR r.rule_code = 'GTA_BACKSEAT'
              );

            UPDATE blacklist_rules
            SET archived_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE archived_utc IS NULL
              AND (
                  scope_id IN (
                      SELECT scope_id
                      FROM policy_sets
                      WHERE broadcaster_id <> '*'
                  )
                  OR rule_code = 'GTA_BACKSEAT'
              );

            UPDATE policy_sets
            SET archived_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                enabled = 0,
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE broadcaster_id <> '*'
              AND archived_utc IS NULL;

            UPDATE whitelist_phrases
            SET archived_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE broadcaster_id <> '*'
              AND archived_utc IS NULL;

            -- Historical decisions keep their channel provenance while adopting
            -- the generalized, non-game-specific rule identifier.
            UPDATE moderation_cases
            SET proposed_rule_code = 'BACKSEATGAMING',
                row_version = row_version + 1
            WHERE proposed_rule_code = 'GTA_BACKSEAT';

            UPDATE phrase_feedback
            SET rule_code = 'BACKSEATGAMING'
            WHERE rule_code = 'GTA_BACKSEAT';

            UPDATE policy_sets
            SET revision = revision + 1,
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE broadcaster_id = '*'
              AND EXISTS (
                  SELECT 1
                  FROM policy_sets legacy
                  WHERE legacy.broadcaster_id <> '*'
              );
            """),
        new(
            6,
            "Canonicalize Backseatgaming rule storage and allow archived rule history",
            """
            -- Rebuild the rule table without the original table-level UNIQUE
            -- constraint. Active rules remain unique while archived revisions can
            -- retain their stable IDs and metadata.
            CREATE TABLE twitchai_blacklist_rules_v6 (
                rule_id                  INTEGER PRIMARY KEY,
                scope_id                 INTEGER NOT NULL,
                rule_code                TEXT NOT NULL,
                title                    TEXT NOT NULL,
                prompt_instruction       TEXT NOT NULL,
                default_action           TEXT NOT NULL CHECK(default_action IN ('delete','timeout')),
                default_timeout_seconds  INTEGER,
                public_reason            TEXT NOT NULL,
                priority                 INTEGER NOT NULL DEFAULT 100,
                enabled                  INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
                row_version              INTEGER NOT NULL DEFAULT 1 CHECK(row_version > 0),
                created_utc              TEXT NOT NULL,
                updated_utc              TEXT NOT NULL,
                archived_utc             TEXT,
                CHECK (
                    (default_action = 'delete' AND default_timeout_seconds IS NULL) OR
                    (default_action = 'timeout' AND
                     default_timeout_seconds BETWEEN 1 AND 1209600)
                ),
                FOREIGN KEY(scope_id) REFERENCES policy_sets(scope_id) ON DELETE RESTRICT
            ) STRICT;

            INSERT INTO twitchai_blacklist_rules_v6(
                rule_id, scope_id, rule_code, title, prompt_instruction,
                default_action, default_timeout_seconds, public_reason,
                priority, enabled, row_version, created_utc, updated_utc,
                archived_utc)
            SELECT
                r.rule_id,
                r.scope_id,
                CASE r.rule_code
                    WHEN 'GTA_BACKSEAT' THEN 'BACKSEATGAMING'
                    ELSE r.rule_code
                END,
                r.title,
                r.prompt_instruction,
                r.default_action,
                r.default_timeout_seconds,
                r.public_reason,
                r.priority,
                CASE
                    WHEN r.archived_utc IS NOT NULL THEN 0
                    WHEN r.rule_code = 'GTA_BACKSEAT'
                         AND EXISTS (
                             SELECT 1
                             FROM blacklist_rules canonical
                             WHERE canonical.scope_id = r.scope_id
                               AND canonical.rule_id <> r.rule_id
                               AND canonical.rule_code = 'BACKSEATGAMING'
                               AND canonical.archived_utc IS NULL)
                        THEN 0
                    ELSE r.enabled
                END,
                r.row_version,
                r.created_utc,
                r.updated_utc,
                CASE
                    WHEN r.rule_code = 'GTA_BACKSEAT'
                         AND r.archived_utc IS NULL
                         AND EXISTS (
                             SELECT 1
                             FROM blacklist_rules canonical
                             WHERE canonical.scope_id = r.scope_id
                               AND canonical.rule_id <> r.rule_id
                               AND canonical.rule_code = 'BACKSEATGAMING'
                               AND canonical.archived_utc IS NULL)
                        THEN strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    ELSE r.archived_utc
                END
            FROM blacklist_rules r;

            DROP TABLE blacklist_rules;
            ALTER TABLE twitchai_blacklist_rules_v6 RENAME TO blacklist_rules;

            CREATE UNIQUE INDEX ux_blacklist_rules_active_code
                ON blacklist_rules(scope_id, rule_code)
                WHERE archived_utc IS NULL;

            CREATE INDEX ix_blacklist_rules_scope
                ON blacklist_rules(scope_id, enabled, archived_utc, priority, rule_code);

            UPDATE phrase_rules
            SET enabled = 0,
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE archived_utc IS NOT NULL
               OR rule_id IN (
                   SELECT rule_id
                   FROM blacklist_rules
                   WHERE archived_utc IS NOT NULL
               );

            UPDATE moderation_cases
            SET proposed_rule_code = 'BACKSEATGAMING',
                row_version = row_version + 1
            WHERE proposed_rule_code = 'GTA_BACKSEAT';

            UPDATE moderation_cases
            SET local_match_codes_json = (
                SELECT json_group_array(mapped.rule_code)
                FROM (
                    SELECT
                        CASE value
                            WHEN 'GTA_BACKSEAT' THEN 'BACKSEATGAMING'
                            ELSE value
                        END AS rule_code,
                        MIN(CAST(key AS INTEGER)) AS first_position
                    FROM json_each(moderation_cases.local_match_codes_json)
                    GROUP BY
                        CASE value
                            WHEN 'GTA_BACKSEAT' THEN 'BACKSEATGAMING'
                            ELSE value
                        END
                    ORDER BY first_position
                ) mapped
            )
            WHERE json_valid(local_match_codes_json)
              AND EXISTS (
                  SELECT 1
                  FROM json_each(moderation_cases.local_match_codes_json)
                  WHERE value = 'GTA_BACKSEAT'
              );

            UPDATE phrase_feedback
            SET rule_code = 'BACKSEATGAMING'
            WHERE rule_code = 'GTA_BACKSEAT';

            UPDATE configuration_audit
            SET before_json = json_set(before_json, '$.rule_code', 'BACKSEATGAMING')
            WHERE before_json IS NOT NULL
              AND json_valid(before_json)
              AND json_type(before_json, '$.rule_code') = 'text'
              AND json_extract(before_json, '$.rule_code') = 'GTA_BACKSEAT';

            UPDATE configuration_audit
            SET after_json = json_set(after_json, '$.rule_code', 'BACKSEATGAMING')
            WHERE after_json IS NOT NULL
              AND json_valid(after_json)
              AND json_type(after_json, '$.rule_code') = 'text'
              AND json_extract(after_json, '$.rule_code') = 'GTA_BACKSEAT';
            """,
            RequiresForeignKeysDisabled: true,
            RequiresBackup: true),
        new(
            7,
            "Introduce semantic General rules, violation attribution, and deletion audit",
            """
            ALTER TABLE moderation_cases
            RENAME COLUMN proposed_rule_code TO violation_rule_code;

            ALTER TABLE moderation_feedback
            ADD COLUMN rule_code TEXT;

            ALTER TABLE action_attempts
            ADD COLUMN rule_code TEXT;

            -- Preserve historical General phrases by assigning them to the
            -- semantic fallback rule. If a scope already owns an active fallback,
            -- its legacy rule is retained as archived history.
            UPDATE phrase_rules
            SET enabled = 0,
                archived_utc = COALESCE(
                    archived_utc,
                    strftime('%Y-%m-%dT%H:%M:%fZ','now')),
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE rule_id IN (
                SELECT legacy.rule_id
                FROM blacklist_rules legacy
                WHERE legacy.rule_code = 'GENERAL_PHRASE'
                  AND legacy.archived_utc IS NULL
                  AND EXISTS (
                      SELECT 1
                      FROM blacklist_rules semantic
                      WHERE semantic.scope_id = legacy.scope_id
                        AND semantic.rule_id <> legacy.rule_id
                        AND semantic.rule_code = 'G_OTHER_ABUSE'
                        AND semantic.archived_utc IS NULL
                  )
            );

            UPDATE blacklist_rules AS legacy
            SET enabled = CASE
                    WHEN EXISTS (
                        SELECT 1
                        FROM blacklist_rules semantic
                        WHERE semantic.scope_id = legacy.scope_id
                          AND semantic.rule_id <> legacy.rule_id
                          AND semantic.rule_code = 'G_OTHER_ABUSE'
                          AND semantic.archived_utc IS NULL)
                    THEN 0
                    ELSE legacy.enabled
                END,
                archived_utc = CASE
                    WHEN legacy.archived_utc IS NULL
                         AND EXISTS (
                             SELECT 1
                             FROM blacklist_rules semantic
                             WHERE semantic.scope_id = legacy.scope_id
                               AND semantic.rule_id <> legacy.rule_id
                               AND semantic.rule_code = 'G_OTHER_ABUSE'
                               AND semantic.archived_utc IS NULL)
                    THEN strftime('%Y-%m-%dT%H:%M:%fZ','now')
                    ELSE legacy.archived_utc
                END,
                rule_code = 'G_OTHER_ABUSE',
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE legacy.rule_code = 'GENERAL_PHRASE';

            CREATE TEMP TABLE twitchai_general_rule_seed (
                rule_code          TEXT PRIMARY KEY,
                title              TEXT NOT NULL,
                prompt_instruction TEXT NOT NULL,
                public_reason      TEXT NOT NULL,
                priority           INTEGER NOT NULL
            ) STRICT;

            INSERT INTO twitchai_general_rule_seed(
                rule_code, title, prompt_instruction, public_reason, priority)
            VALUES
                ('G_SEXUALIZATION', 'Sexualisierung',
                 'Erkenne sexualisierende Aussagen über Personen; sachliche Erwähnungen sind kein Verstoß.',
                 'Sexualisierende Aussage über eine Person.', 50),
                ('G_SEXUAL_EXPLICIT', 'Explizit sexueller Inhalt',
                 'Erkenne explizite sexuelle Beschreibungen oder Aufforderungen.',
                 'Sexuell expliziter Inhalt.', 50),
                ('G_INSULT', 'Beleidigung',
                 'Erkenne direkte persönliche Beleidigungen; harmlose Neckerei nur bei klarem Kontext nicht markieren.',
                 'Direkte Beleidigung gegen einen Nutzer.', 70),
                ('G_HARASSMENT', 'Belästigung/Mobbing',
                 'Erkenne gezielte, wiederholte Belästigung oder Mobbing gegen eine Person.',
                 'Gezielte Belästigung eines Nutzers.', 60),
                ('G_RACISM', 'Rassismus',
                 'Erkenne rassistische oder ethnisch abwertende Aussagen.',
                 'Rassistische oder ethnisch abwertende Aussage.', 30),
                ('G_SEXISM', 'Sexismus',
                 'Erkenne sexistische oder frauenfeindliche Aussagen.',
                 'Sexistische oder frauenfeindliche Aussage.', 30),
                ('G_LGBTQ_HATE', 'Queerfeindlichkeit',
                 'Erkenne abwertende Aussagen gegen LGBTQ+-Personen.',
                 'Queerfeindliche Aussage.', 30),
                ('G_ABLEISM', 'Behindertenfeindlichkeit',
                 'Erkenne behindertenfeindliche Abwertung.',
                 'Behindertenfeindliche Aussage.', 30),
                ('G_HATE_OTHER', 'Sonstige Hassrede',
                 'Erkenne menschenfeindliche Hassrede gegen weitere geschützte Gruppen.',
                 'Menschenfeindliche Hassrede.', 35),
                ('G_THREAT', 'Direkte Drohung',
                 'Erkenne glaubhafte oder direkte Gewaltandrohungen gegen Personen.',
                 'Gewaltandrohung gegen eine Person.', 20),
                ('G_VIOLENCE', 'Gewaltverherrlichung',
                 'Erkenne Verherrlichung oder Aufforderung zu realer Gewalt.',
                 'Gewaltverherrlichender Inhalt.', 40),
                ('G_PERSONAL_DATA', 'Persönliche Daten/Doxxing',
                 'Erkenne Veröffentlichung oder Androhung der Veröffentlichung persönlicher Daten; keine Triggerphrase lernen.',
                 'Veröffentlichung persönlicher Daten.', 10),
                ('G_SELF_HARM_INCITEMENT', 'Aufforderung zur Selbstverletzung',
                 'Erkenne Aufforderungen zu Selbstverletzung oder Suizid.',
                 'Aufforderung zu Selbstverletzung.', 20),
                ('G_SPAM', 'Spam',
                 'Erkenne wiederholten, den Chat störenden Spam.',
                 'Wiederholter Spam.', 80),
                ('G_SCAM', 'Betrug/Phishing',
                 'Erkenne Betrugs-, Phishing- oder schädliche Täuschungsversuche.',
                 'Betrugs- oder Phishingversuch.', 80),
                ('G_DISRUPTION', 'Chatstörung',
                 'Erkenne gezielte, erhebliche Störung des Chats.',
                 'Gezielte Störung des Chats.', 80),
                ('G_OTHER_ABUSE', 'Sonstiger General-Verstoß',
                 'Nutze diese Regel nur für sonstige klare Verstöße gegen die allgemeinen Chatregeln.',
                 'Sonstiger Verstoß gegen die Chatregeln.', 100);

            INSERT INTO blacklist_rules(
                scope_id, rule_code, title, prompt_instruction,
                default_action, default_timeout_seconds, public_reason,
                priority, enabled, row_version, created_utc, updated_utc)
            SELECT
                s.scope_id, seed.rule_code, seed.title, seed.prompt_instruction,
                'timeout', 600, seed.public_reason,
                seed.priority, 1, 1,
                strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM policy_sets s
            CROSS JOIN twitchai_general_rule_seed seed
            WHERE s.broadcaster_id = '*'
              AND s.set_code = 'general'
              AND s.archived_utc IS NULL
            ON CONFLICT(scope_id, rule_code) WHERE archived_utc IS NULL
            DO UPDATE SET
                title = excluded.title,
                prompt_instruction = excluded.prompt_instruction,
                default_action = 'timeout',
                default_timeout_seconds = 600,
                public_reason = excluded.public_reason,
                priority = excluded.priority,
                enabled = 1,
                row_version = blacklist_rules.row_version + 1,
                updated_utc = excluded.updated_utc;

            DROP TABLE twitchai_general_rule_seed;

            INSERT INTO blacklist_rules(
                scope_id, rule_code, title, prompt_instruction,
                default_action, default_timeout_seconds, public_reason,
                priority, enabled, row_version, created_utc, updated_utc)
            SELECT
                s.scope_id, 'BACKSEATGAMING', 'Backseatgaming',
                'Erkenne unaufgeforderte Spielanweisungen, Lösungen, Spoiler und Hinweise auf kommende Gefahren; ausdrückliche Hilfeanfragen nicht bestrafen.',
                'timeout', 600, 'Unaufgefordertes Backseatgaming.',
                100, 1, 1,
                strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM policy_sets s
            WHERE s.broadcaster_id = '*'
              AND s.set_code = 'backseatgaming'
              AND s.archived_utc IS NULL
            ON CONFLICT(scope_id, rule_code) WHERE archived_utc IS NULL
            DO UPDATE SET
                title = excluded.title,
                prompt_instruction = excluded.prompt_instruction,
                default_action = 'timeout',
                default_timeout_seconds = 600,
                public_reason = excluded.public_reason,
                priority = 100,
                enabled = 1,
                row_version = blacklist_rules.row_version + 1,
                updated_utc = excluded.updated_utc;

            INSERT INTO phrase_rules(
                rule_id, phrase, normalized_phrase, enabled,
                manual_override, positive_confirmations, negative_confirmations,
                row_version, created_utc, updated_utc)
            SELECT
                r.rule_id,
                'sie sieht sexy aus',
                'sie sieht sexy aus',
                1,
                'active',
                0,
                0,
                1,
                strftime('%Y-%m-%dT%H:%M:%fZ','now'),
                strftime('%Y-%m-%dT%H:%M:%fZ','now')
            FROM blacklist_rules r
            INNER JOIN policy_sets s ON s.scope_id = r.scope_id
            WHERE s.broadcaster_id = '*'
              AND s.set_code = 'general'
              AND s.archived_utc IS NULL
              AND r.rule_code = 'G_SEXUALIZATION'
              AND r.archived_utc IS NULL
            ON CONFLICT(rule_id, normalized_phrase)
            DO UPDATE SET
                phrase = excluded.phrase,
                enabled = 1,
                manual_override = 'active',
                archived_utc = NULL,
                row_version = phrase_rules.row_version + 1,
                updated_utc = excluded.updated_utc;

            UPDATE moderation_cases
            SET violation_rule_code = CASE
                    WHEN violation_rule_code = 'GTA_BACKSEAT'
                        THEN 'BACKSEATGAMING'
                    WHEN violation_rule_code = 'GENERAL_PHRASE'
                        THEN 'G_OTHER_ABUSE'
                    WHEN violation_rule_code IS NULL
                         AND policy_set = 'backseatgaming'
                        THEN 'BACKSEATGAMING'
                    WHEN violation_rule_code IS NULL
                         AND policy_set = 'general'
                        THEN 'G_OTHER_ABUSE'
                    ELSE violation_rule_code
                END,
                row_version = row_version + 1
            WHERE violation_rule_code IN ('GTA_BACKSEAT', 'GENERAL_PHRASE')
               OR violation_rule_code IS NULL;

            UPDATE moderation_cases
            SET local_match_codes_json = (
                SELECT json_group_array(mapped.rule_code)
                FROM (
                    SELECT
                        CASE value
                            WHEN 'GENERAL_PHRASE' THEN 'G_OTHER_ABUSE'
                            WHEN 'GTA_BACKSEAT' THEN 'BACKSEATGAMING'
                            ELSE value
                        END AS rule_code,
                        MIN(CAST(key AS INTEGER)) AS first_position
                    FROM json_each(moderation_cases.local_match_codes_json)
                    GROUP BY
                        CASE value
                            WHEN 'GENERAL_PHRASE' THEN 'G_OTHER_ABUSE'
                            WHEN 'GTA_BACKSEAT' THEN 'BACKSEATGAMING'
                            ELSE value
                        END
                    ORDER BY first_position
                ) mapped
            )
            WHERE json_valid(local_match_codes_json)
              AND EXISTS (
                  SELECT 1
                  FROM json_each(moderation_cases.local_match_codes_json)
                  WHERE value IN ('GENERAL_PHRASE', 'GTA_BACKSEAT')
              );

            UPDATE phrase_feedback
            SET rule_code = CASE rule_code
                    WHEN 'GENERAL_PHRASE' THEN 'G_OTHER_ABUSE'
                    WHEN 'GTA_BACKSEAT' THEN 'BACKSEATGAMING'
                    ELSE rule_code
                END
            WHERE rule_code IN ('GENERAL_PHRASE', 'GTA_BACKSEAT');

            UPDATE moderation_feedback
            SET rule_code = (
                SELECT c.violation_rule_code
                FROM moderation_cases c
                WHERE c.case_id = moderation_feedback.case_id
            );

            UPDATE action_attempts
            SET rule_code = (
                SELECT c.violation_rule_code
                FROM moderation_cases c
                WHERE c.case_id = action_attempts.case_id
            );

            UPDATE configuration_audit
            SET before_json = json_set(before_json, '$.rule_code', 'G_OTHER_ABUSE')
            WHERE before_json IS NOT NULL
              AND json_valid(before_json)
              AND json_type(before_json, '$.rule_code') = 'text'
              AND json_extract(before_json, '$.rule_code') = 'GENERAL_PHRASE';

            UPDATE configuration_audit
            SET after_json = json_set(after_json, '$.rule_code', 'G_OTHER_ABUSE')
            WHERE after_json IS NOT NULL
              AND json_valid(after_json)
              AND json_type(after_json, '$.rule_code') = 'text'
              AND json_extract(after_json, '$.rule_code') = 'GENERAL_PHRASE';

            UPDATE policy_sets
            SET revision = revision + 1,
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE broadcaster_id = '*'
              AND set_code IN ('general', 'backseatgaming')
              AND archived_utc IS NULL;

            CREATE TABLE data_deletion_audit (
                deletion_id                 INTEGER PRIMARY KEY,
                request_id                  TEXT NOT NULL UNIQUE,
                target_table                TEXT NOT NULL,
                target_id_sha256             TEXT NOT NULL CHECK(length(target_id_sha256) = 64),
                deletion_kind               TEXT NOT NULL CHECK(deletion_kind IN (
                    'case_package','safe_log')),
                deleted_counts_json          TEXT NOT NULL,
                positive_feedback_reverted   INTEGER NOT NULL DEFAULT 0
                                             CHECK(positive_feedback_reverted >= 0),
                negative_feedback_reverted   INTEGER NOT NULL DEFAULT 0
                                             CHECK(negative_feedback_reverted >= 0),
                preview_fingerprint          TEXT NOT NULL,
                requested_utc                TEXT NOT NULL,
                completed_utc                TEXT NOT NULL
            ) STRICT;

            CREATE INDEX ix_data_deletion_audit_completed
                ON data_deletion_audit(completed_utc DESC);
            """,
            RequiresBackup: true),
        new(
            8,
            "Remove negative-only phrase candidates created from allowed reviews",
            """
            CREATE TEMP TABLE twitchai_negative_only_scopes AS
            SELECT DISTINCT r.scope_id
            FROM phrase_rules p
            INNER JOIN blacklist_rules r ON r.rule_id = p.rule_id
            WHERE p.manual_override = 'auto'
              AND p.positive_confirmations = 0
              AND p.negative_confirmations > 0;

            DELETE FROM phrase_rules
            WHERE manual_override = 'auto'
              AND positive_confirmations = 0
              AND negative_confirmations > 0;

            UPDATE policy_sets
            SET revision = revision + 1,
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE scope_id IN (
                SELECT scope_id
                FROM twitchai_negative_only_scopes
            );

            DROP TABLE twitchai_negative_only_scopes;
            """),
        new(
            9,
            "Require concrete contextual evidence for Backseatgaming moderation",
            """
            UPDATE blacklist_rules
            SET prompt_instruction =
                    'BACKSEATGAMING nur bei klarer, unaufgeforderter Spielinformation: konkrete Handlungsanweisung, Missions-/Rätsellösung oder Spoiler zu kommendem Ereignis/versteckter Gefahr. Sonst none: einzelne Wörter/Fragmente (z. B. zorro, nichts), allgemeine Reaktionen, Fragen, Scherze, Meinungen, Zustimmung/Ablehnung, Streamer-/Chat-Meta oder Text ohne klaren spielbezogenen Handlungs-/Informationsgehalt; Zitat, Verneinung und Kontext beachten.',
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE rule_code = 'BACKSEATGAMING'
              AND archived_utc IS NULL
              AND scope_id IN (
                  SELECT scope_id
                  FROM policy_sets
                  WHERE broadcaster_id = '*'
                    AND set_code = 'backseatgaming'
                    AND archived_utc IS NULL
              );

            UPDATE policy_sets
            SET revision = revision + 1,
                row_version = row_version + 1,
                updated_utc = strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE broadcaster_id = '*'
              AND set_code = 'backseatgaming'
              AND archived_utc IS NULL;
            """),
        new(
            10,
            "Support exact Twitch login entries in the global whitelist",
            """
            CREATE TABLE whitelist_phrases_v10 (
                allowlist_id       INTEGER PRIMARY KEY,
                broadcaster_id     TEXT NOT NULL,
                subject_kind       TEXT NOT NULL CHECK(subject_kind IN (
                    'user_id','user_login','exact_message')),
                subject_value      TEXT NOT NULL,
                normalized_value   TEXT NOT NULL,
                enabled            INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
                hit_count          INTEGER NOT NULL DEFAULT 0 CHECK(hit_count >= 0),
                last_hit_utc       TEXT,
                row_version        INTEGER NOT NULL DEFAULT 1 CHECK(row_version > 0),
                created_utc        TEXT NOT NULL,
                updated_utc        TEXT NOT NULL,
                archived_utc       TEXT,
                UNIQUE(broadcaster_id, subject_kind, normalized_value)
            ) STRICT;

            INSERT INTO whitelist_phrases_v10(
                allowlist_id, broadcaster_id, subject_kind,
                subject_value, normalized_value, enabled,
                hit_count, last_hit_utc, row_version,
                created_utc, updated_utc, archived_utc)
            SELECT
                allowlist_id, broadcaster_id, subject_kind,
                subject_value, normalized_value, enabled,
                hit_count, last_hit_utc, row_version,
                created_utc, updated_utc, archived_utc
            FROM whitelist_phrases;

            DROP TABLE whitelist_phrases;
            ALTER TABLE whitelist_phrases_v10
                RENAME TO whitelist_phrases;

            CREATE INDEX ix_whitelist_match
                ON whitelist_phrases(
                    broadcaster_id, subject_kind, normalized_value)
                WHERE enabled = 1 AND archived_utc IS NULL;
            """,
            RequiresForeignKeysDisabled: true,
            RequiresBackup: true),
        new(
            11,
            "Add semantic chat reply templates and reviewable reply proposals",
            """
            CREATE TABLE chat_reply_templates (
                template_id             INTEGER PRIMARY KEY,
                broadcaster_id          TEXT NOT NULL DEFAULT '*',
                example_message         TEXT NOT NULL CHECK(
                                            length(example_message) BETWEEN 1 AND 500),
                normalized_example      TEXT NOT NULL CHECK(
                                            length(normalized_example) BETWEEN 1 AND 500),
                response_message        TEXT NOT NULL CHECK(
                                            length(response_message) BETWEEN 1 AND 500),
                enabled                 INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN (0,1)),
                hit_count               INTEGER NOT NULL DEFAULT 0 CHECK(hit_count >= 0),
                last_hit_utc            TEXT,
                row_version             INTEGER NOT NULL DEFAULT 1 CHECK(row_version > 0),
                created_utc             TEXT NOT NULL,
                updated_utc             TEXT NOT NULL,
                UNIQUE(broadcaster_id, normalized_example)
            ) STRICT;

            CREATE INDEX ix_chat_reply_templates_active
                ON chat_reply_templates(broadcaster_id, template_id)
                WHERE enabled = 1;

            CREATE TABLE chat_reply_proposals (
                proposal_id             INTEGER PRIMARY KEY,
                source_message_id        TEXT NOT NULL UNIQUE,
                broadcaster_id          TEXT NOT NULL,
                chatter_user_id          TEXT NOT NULL,
                chatter_login            TEXT NOT NULL,
                chatter_display_name     TEXT NOT NULL,
                original_message         TEXT NOT NULL,
                identity_fingerprint     TEXT NOT NULL CHECK(
                                            length(identity_fingerprint) = 64),
                category_id              TEXT,
                category_name            TEXT NOT NULL,
                message_received_utc     TEXT NOT NULL,
                template_id              INTEGER,
                template_row_version     INTEGER NOT NULL CHECK(
                                            template_row_version > 0),
                configured_response      TEXT NOT NULL CHECK(
                                            length(configured_response) BETWEEN 1 AND 500),
                rendered_response        TEXT NOT NULL CHECK(
                                            length(rendered_response) BETWEEN 1 AND 500),
                confidence               REAL NOT NULL CHECK(
                                            confidence >= 0 AND confidence <= 1),
                batch_id                 TEXT NOT NULL,
                ai_latency_ms            INTEGER CHECK(ai_latency_ms >= 0),
                status                   TEXT NOT NULL DEFAULT 'pending' CHECK(status IN (
                                            'pending','executing','executed',
                                            'discarded','failed','outcome_unknown')),
                twitch_response_id       TEXT,
                twitch_drop_code         TEXT,
                twitch_drop_message      TEXT,
                row_version              INTEGER NOT NULL DEFAULT 1 CHECK(row_version > 0),
                created_utc              TEXT NOT NULL,
                resolved_utc             TEXT,
                FOREIGN KEY(template_id) REFERENCES chat_reply_templates(template_id)
                    ON DELETE SET NULL
            ) STRICT;

            CREATE INDEX ix_chat_reply_proposals_status_created
                ON chat_reply_proposals(status, created_utc DESC);
            """),
    ];
}
