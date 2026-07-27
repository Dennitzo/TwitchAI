namespace TwitchAI.Infrastructure.Persistence;

public enum DatabaseTableAccessClass
{
    UserEditable,
    AppendOnly,
    ApplicationManaged,
    AuditReadOnly,
}

public enum DatabaseColumnKind
{
    Text,
    WholeNumber,
    Real,
    Boolean,
}

public sealed record DatabaseColumnDefinition(
    string Name,
    DatabaseColumnKind Kind,
    bool Required = false,
    int? MaximumLength = null,
    IReadOnlySet<string>? AllowedValues = null);

public sealed record DatabaseTableDefinition(
    string Name,
    string PrimaryKey,
    DatabaseTableAccessClass AccessClass,
    IReadOnlyDictionary<string, DatabaseColumnDefinition> EditableColumns,
    bool SupportsSoftArchive,
    bool UsesRowVersion);

public interface IDatabaseCatalog
{
    IReadOnlyList<DatabaseTableDefinition> Tables { get; }

    DatabaseTableDefinition GetRequired(string tableName);
}

public sealed class DatabaseCatalog : IDatabaseCatalog
{
    private static readonly IReadOnlyDictionary<string, DatabaseTableDefinition> Definitions =
        CreateDefinitions();

    public IReadOnlyList<DatabaseTableDefinition> Tables { get; } =
        Definitions.Values.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();

    public DatabaseTableDefinition GetRequired(string tableName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        return Definitions.TryGetValue(tableName, out var definition)
            ? definition
            : throw new UnauthorizedAccessException(
                $"Table '{tableName}' is not exposed by the protected database catalog.");
    }

    private static Dictionary<string, DatabaseTableDefinition> CreateDefinitions()
    {
        var actionValues = Set("delete", "timeout");
        var subjectValues = Set("user_id", "exact_message");
        var definitions = new[]
        {
            Editable(
                "blacklist_rules",
                "rule_id",
                [
                    Integer("scope_id", required: true),
                    Text("rule_code", true, 64),
                    Text("title", true, 128),
                    Text("prompt_instruction", true, 512),
                    Text("default_action", true, 16, actionValues),
                    Integer("default_timeout_seconds"),
                    Text("public_reason", true, 120),
                    Integer("priority", required: true),
                    Boolean("enabled", required: true),
                ]),
            Editable(
                "phrase_rules",
                "phrase_id",
                [
                    Integer("rule_id", required: true),
                    Text("phrase", true, 500),
                    Text(
                        "manual_override",
                        true,
                        16,
                        Set("auto", "active", "inactive")),
                    Boolean("enabled", required: true),
                ]),
            Editable(
                "whitelist_phrases",
                "allowlist_id",
                [
                    Text(
                        "broadcaster_id",
                        true,
                        64,
                        Set(GlobalPolicyStorage.BroadcasterId)),
                    Text("subject_kind", true, 32, subjectValues),
                    Text("subject_value", true, 500),
                    Boolean("enabled", required: true),
                ]),
            Editable(
                "policy_sets",
                "scope_id",
                [
                    Text(
                        "broadcaster_id",
                        true,
                        64,
                        Set(GlobalPolicyStorage.BroadcasterId)),
                    Text("scope_kind", true, 16, Set("general", "category")),
                    Text("set_code", true, 32, Set("general", "backseatgaming")),
                    Text("category_id", maximumLength: 64),
                    Boolean("enabled", required: true),
                ]),
            Editable(
                "policy_category_exclusions",
                "exclusion_id",
                [
                    Integer("scope_id", required: true),
                    Text("category_id", true, 64),
                    Boolean("enabled", required: true),
                ]),
            AppendOnly(
                "record_annotations",
                "annotation_id",
                [
                    Text("record_type", true, 64),
                    Text("record_id", true, 128),
                    Text("annotation_kind", true, 64),
                    Text("annotation_text", true, 2_000),
                ]),
            Managed("twitch_categories", "category_id"),
            Managed("channel_context", "broadcaster_id"),
            Managed("schema_migrations", "version"),
            Managed("processed_eventsub_events", "eventsub_message_id"),
            Audit("chat_message_snapshots", "twitch_message_id"),
            Audit("moderation_cases", "case_id"),
            Audit("moderation_feedback", "feedback_id"),
            Audit("action_attempts", "execution_id"),
            Audit("message_availability_events", "availability_event_id"),
            Audit("message_clear_watermarks", "watermark_id"),
            Audit("phrase_feedback", "feedback_id"),
            Audit("configuration_audit", "change_id"),
            Audit("ai_run_metrics", "request_id"),
            Audit("data_deletion_audit", "deletion_id"),
        };

        return definitions.ToDictionary(item => item.Name, StringComparer.Ordinal);
    }

    private static DatabaseTableDefinition Editable(
        string name,
        string primaryKey,
        IReadOnlyList<DatabaseColumnDefinition> columns)
    {
        return new DatabaseTableDefinition(
            name,
            primaryKey,
            DatabaseTableAccessClass.UserEditable,
            columns.ToDictionary(item => item.Name, StringComparer.Ordinal),
            SupportsSoftArchive: true,
            UsesRowVersion: true);
    }

    private static DatabaseTableDefinition Managed(string name, string primaryKey)
    {
        return new DatabaseTableDefinition(
            name,
            primaryKey,
            DatabaseTableAccessClass.UserEditable,
            new Dictionary<string, DatabaseColumnDefinition>(StringComparer.Ordinal),
            SupportsSoftArchive: false,
            UsesRowVersion: false);
    }

    private static DatabaseTableDefinition AppendOnly(
        string name,
        string primaryKey,
        IReadOnlyList<DatabaseColumnDefinition> columns)
    {
        return new DatabaseTableDefinition(
            name,
            primaryKey,
            DatabaseTableAccessClass.UserEditable,
            columns.ToDictionary(item => item.Name, StringComparer.Ordinal),
            SupportsSoftArchive: false,
            UsesRowVersion: false);
    }

    private static DatabaseTableDefinition Audit(string name, string primaryKey)
    {
        return new DatabaseTableDefinition(
            name,
            primaryKey,
            DatabaseTableAccessClass.UserEditable,
            new Dictionary<string, DatabaseColumnDefinition>(StringComparer.Ordinal),
            SupportsSoftArchive: false,
            UsesRowVersion: false);
    }

    private static DatabaseColumnDefinition Text(
        string name,
        bool required = false,
        int? maximumLength = null,
        IReadOnlySet<string>? values = null)
    {
        return new DatabaseColumnDefinition(
            name,
            DatabaseColumnKind.Text,
            required,
            maximumLength,
            values);
    }

    private static DatabaseColumnDefinition Integer(string name, bool required = false)
    {
        return new DatabaseColumnDefinition(name, DatabaseColumnKind.WholeNumber, required);
    }

    private static DatabaseColumnDefinition Boolean(string name, bool required = false)
    {
        return new DatabaseColumnDefinition(name, DatabaseColumnKind.Boolean, required);
    }

    private static HashSet<string> Set(params string[] values)
    {
        return new HashSet<string>(values, StringComparer.Ordinal);
    }
}
