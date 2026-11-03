using System.Globalization;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.Persistence;
using CoreChatTextNormalizer = TwitchAI.Core.Contracts.IChatTextNormalizer;
using CoreDatabaseRow = TwitchAI.Core.Models.DatabaseRow;

namespace TwitchAI.App.Services;

public sealed record WhitelistConfigurationRow(
    long AllowlistId,
    long RowVersion,
    string BroadcasterId,
    string OriginalPhrase,
    string LowerPhrase,
    bool IsUser,
    bool IsEnabled,
    int HitCount,
    DateTimeOffset? LastHitAtUtc,
    bool IsGlobalSeed);

public sealed record PolicyPhraseConfigurationRow(
    long PhraseId,
    long RuleId,
    long RowVersion,
    string PolicySet,
    string RuleCode,
    string RuleTitle,
    string NormalizedPhrase,
    string ManualOverride,
    bool IsEnabled,
    int PositiveConfirmations,
    int NegativeConfirmations);

public sealed record PolicyRuleConfigurationRow(
    long RuleId,
    long RowVersion,
    string PolicySet,
    string RuleCode,
    string DisplayName,
    string PublicReason,
    string DefaultAction,
    int? DefaultTimeoutSeconds);

public sealed record ArchivedPhraseRestoreCandidate(
    long Id,
    long RowVersion,
    string NormalizedPhrase,
    string? PolicySet = null,
    long? RuleId = null,
    string? RuleCode = null,
    int HitCount = 0,
    int PositiveConfirmations = 0,
    int NegativeConfirmations = 0);

public sealed class ArchivedPhraseExistsException
    : InvalidOperationException
{
    public ArchivedPhraseExistsException(
        string message,
        ArchivedPhraseRestoreCandidate candidate)
        : base(message)
    {
        Candidate = candidate
            ?? throw new ArgumentNullException(nameof(candidate));
    }

    public ArchivedPhraseRestoreCandidate Candidate { get; }
}

public interface IConfigurationDataService
{
    string? BroadcasterId { get; }

    Task<IReadOnlyList<WhitelistConfigurationRow>> LoadWhitelistAsync(
        CancellationToken cancellationToken = default);

    Task AddWhitelistPhraseAsync(
        string phrase,
        CancellationToken cancellationToken = default);

    Task<long> UpdateWhitelistPhraseAsync(
        long allowlistId,
        long expectedRowVersion,
        string phrase,
        bool isUser,
        CancellationToken cancellationToken = default);

    Task<long> SetWhitelistEnabledAsync(
        long allowlistId,
        long expectedRowVersion,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<long> ArchiveWhitelistPhraseAsync(
        long allowlistId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default);

    Task<long> RestoreWhitelistPhraseAsync(
        long allowlistId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PolicyPhraseConfigurationRow>> LoadPolicyPhrasesAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PolicyRuleConfigurationRow>> LoadPolicyRulesAsync(
        CancellationToken cancellationToken = default);

    Task AddPolicyPhraseAsync(
        string policySet,
        long ruleId,
        string phrase,
        CancellationToken cancellationToken = default);

    Task<long> UpdatePolicyPhraseAsync(
        long phraseId,
        long expectedRowVersion,
        string policySet,
        string phrase,
        string manualOverride,
        CancellationToken cancellationToken = default);

    Task<long> SetPolicyPhraseEnabledAsync(
        long phraseId,
        long expectedRowVersion,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<long[]> SetPolicyPhrasesEnabledAsync(
        IReadOnlyList<(long PhraseId, long ExpectedRowVersion)> entries,
        bool isEnabled,
        CancellationToken cancellationToken = default);

    Task<long> ArchivePolicyPhraseAsync(
        long phraseId,
        long expectedRowVersion,
        string policySet,
        CancellationToken cancellationToken = default);

    Task<long> RestorePolicyPhraseAsync(
        long phraseId,
        long expectedRowVersion,
        string policySet,
        CancellationToken cancellationToken = default);
}

public sealed class ConfigurationDataService : IConfigurationDataService, IDisposable
{
    private const int QueryPageSize = 200;
    private const string GeneralPolicySet = "General";
    private const string BackseatgamingPolicySet = "Backseatgaming";
    private const string ExactMessageSubjectKind = "exact_message";
    private const string UserLoginSubjectKind = "user_login";
    private const string GeneralRuleCode = GlobalPolicyStorage.GeneralRuleCode;
    private const string BackseatgamingRuleCode =
        GlobalPolicyStorage.BackseatgamingRuleCode;

    private readonly IDatabaseEditorService _database;
    private readonly IPolicyStore _policyStore;
    private readonly CoreChatTextNormalizer _textNormalizer;
    private readonly InfrastructureOptions _options;
    private readonly SemaphoreSlim _policySetupGate = new(1, 1);
    private bool _disposed;

    public ConfigurationDataService(
        IDatabaseEditorService database,
        IPolicyStore policyStore,
        CoreChatTextNormalizer textNormalizer,
        InfrastructureOptions options)
    {
        _database = database;
        _policyStore = policyStore;
        _textNormalizer = textNormalizer;
        _options = options;
    }

    public string? BroadcasterId =>
        string.IsNullOrWhiteSpace(_options.TwitchApi.BroadcasterId)
            ? null
            : _options.TwitchApi.BroadcasterId;

    public async Task<IReadOnlyList<WhitelistConfigurationRow>> LoadWhitelistAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var rows = await QueryAllAsync(
            "whitelist_phrases",
            "allowlist_id",
            cancellationToken).ConfigureAwait(false);

        return rows
            .Where(row =>
                IsCurrent(row)
                && IsSupportedWhitelistSubjectKind(
                    Text(row, "subject_kind"))
                && Text(row, "broadcaster_id")
                    == GlobalPolicyStorage.BroadcasterId)
            .Select(row =>
            {
                var owner = Text(row, "broadcaster_id");
                return new WhitelistConfigurationRow(
                    Integer(row, "allowlist_id"),
                    Integer(row, "row_version"),
                    owner,
                    Text(row, "subject_value"),
                    Text(row, "normalized_value"),
                    Text(row, "subject_kind") == UserLoginSubjectKind,
                    Boolean(row, "enabled"),
                    checked((int)Integer(row, "hit_count")),
                    NullableTimestamp(row, "last_hit_utc"),
                    owner == "*");
            })
            .OrderByDescending(item => item.IsGlobalSeed)
            .ThenBy(item => item.LowerPhrase, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task AddWhitelistPhraseAsync(
        string phrase,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(phrase);
        var lowerPhrase = NormalizeWhitelistPhrase(phrase);

        var rows = await QueryAllAsync(
            "whitelist_phrases",
            "allowlist_id",
            cancellationToken).ConfigureAwait(false);
        var duplicate = rows.SingleOrDefault(row =>
            Text(row, "broadcaster_id") == GlobalPolicyStorage.BroadcasterId
            && Text(row, "subject_kind") == ExactMessageSubjectKind
            && Text(row, "normalized_value") == lowerPhrase);
        if (duplicate is not null)
        {
            if (!IsCurrent(duplicate))
            {
                throw new ArchivedPhraseExistsException(
                    "Diese exakte Lowercase-Phrase ist archiviert und kann mit ihrer bisherigen ID wiederhergestellt werden.",
                    new ArchivedPhraseRestoreCandidate(
                        Integer(duplicate, "allowlist_id"),
                        Integer(duplicate, "row_version"),
                        lowerPhrase,
                        HitCount: checked(
                            (int)Integer(duplicate, "hit_count"))));
            }

            throw new InvalidOperationException(
                "Diese exakte Lowercase-Phrase ist bereits in der Whitelist vorhanden.");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["broadcaster_id"] = GlobalPolicyStorage.BroadcasterId,
            ["subject_kind"] = ExactMessageSubjectKind,
            ["subject_value"] = phrase,
            ["enabled"] = true,
        };
        await CommitSingleAsync(
            "whitelist_phrases",
            primaryKey: null,
            expectedRowVersion: null,
            values,
            "Whitelist-Phrase hinzugefügt",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> UpdateWhitelistPhraseAsync(
        long allowlistId,
        long expectedRowVersion,
        string phrase,
        bool isUser,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(allowlistId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRowVersion);
        ArgumentNullException.ThrowIfNull(phrase);
        var lowerPhrase = NormalizeWhitelistPhrase(phrase);
        var subjectKind = isUser
            ? UserLoginSubjectKind
            : ExactMessageSubjectKind;
        var rows = await QueryAllAsync(
            "whitelist_phrases",
            "allowlist_id",
            cancellationToken).ConfigureAwait(false);
        var duplicate = rows.FirstOrDefault(row =>
            Integer(row, "allowlist_id") != allowlistId
            && Text(row, "broadcaster_id")
                == GlobalPolicyStorage.BroadcasterId
            && Text(row, "subject_kind") == subjectKind
            && Text(row, "normalized_value") == lowerPhrase);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                IsCurrent(duplicate)
                    ? "Dieser Whitelist-Eintrag ist für den ausgewählten Typ bereits vorhanden."
                    : "Dieser Whitelist-Eintrag ist für den ausgewählten Typ bereits archiviert.");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["subject_kind"] = subjectKind,
            ["subject_value"] = phrase,
        };
        await CommitSingleAsync(
            "whitelist_phrases",
            allowlistId,
            expectedRowVersion,
            values,
            "Whitelist-Phrase inline bearbeitet",
            cancellationToken).ConfigureAwait(false);
        return checked(expectedRowVersion + 1);
    }

    public async Task<long> SetWhitelistEnabledAsync(
        long allowlistId,
        long expectedRowVersion,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await SetEnabledAsync(
            "whitelist_phrases",
            allowlistId,
            expectedRowVersion,
            isEnabled,
            "Whitelist-Phrase aktiviert oder deaktiviert",
            cancellationToken).ConfigureAwait(false);
    }

    public Task<long> ArchiveWhitelistPhraseAsync(
        long allowlistId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default) =>
        SetWhitelistArchivedStateAsync(
            allowlistId,
            expectedRowVersion,
            isArchived: true,
            cancellationToken);

    public Task<long> RestoreWhitelistPhraseAsync(
        long allowlistId,
        long expectedRowVersion,
        CancellationToken cancellationToken = default) =>
        SetWhitelistArchivedStateAsync(
            allowlistId,
            expectedRowVersion,
            isArchived: false,
            cancellationToken);

    private async Task<long> SetWhitelistArchivedStateAsync(
        long allowlistId,
        long expectedRowVersion,
        bool isArchived,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(allowlistId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRowVersion);

        var rows = await QueryAllAsync(
            "whitelist_phrases",
            "allowlist_id",
            cancellationToken).ConfigureAwait(false);
        var row = rows.SingleOrDefault(
            item => Integer(item, "allowlist_id") == allowlistId);
        if (row is null
            || Text(row, "broadcaster_id") != GlobalPolicyStorage.BroadcasterId
            || !IsSupportedWhitelistSubjectKind(
                Text(row, "subject_kind")))
        {
            throw new InvalidOperationException(
                "Die Whitelist-Phrase wurde nicht gefunden oder gehört nicht zur globalen Nachrichten-Whitelist.");
        }

        if (Integer(row, "row_version") != expectedRowVersion)
        {
            throw new DatabaseConcurrencyException(
                "whitelist_phrases",
                allowlistId);
        }

        if (IsCurrent(row) == !isArchived)
        {
            return expectedRowVersion;
        }

        await CommitArchiveSingleAsync(
            "whitelist_phrases",
            allowlistId,
            expectedRowVersion,
            isArchived,
            isArchived
                ? "Whitelist-Phrase archiviert"
                : "Whitelist-Phrase wiederhergestellt",
            cancellationToken).ConfigureAwait(false);
        return checked(expectedRowVersion + 1);
    }

    public async Task<IReadOnlyList<PolicyPhraseConfigurationRow>> LoadPolicyPhrasesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsurePolicyConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);

        var snapshot = await ReadPolicyTablesAsync(cancellationToken)
            .ConfigureAwait(false);
        return snapshot.PhraseRows
            .OrderBy(item => item.PolicySet, StringComparer.Ordinal)
            .ThenBy(item => item.NormalizedPhrase, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<IReadOnlyList<PolicyRuleConfigurationRow>> LoadPolicyRulesAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsurePolicyConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        var snapshot = await ReadPolicyTablesAsync(cancellationToken)
            .ConfigureAwait(false);
        return snapshot.RuleRows
            .OrderBy(item => item.PolicySet, StringComparer.Ordinal)
            .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.RuleCode, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task AddPolicyPhraseAsync(
        string policySet,
        long ruleId,
        string phrase,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var normalizedPolicySet = ValidatePolicySet(policySet);
        ArgumentNullException.ThrowIfNull(phrase);
        var normalizedPhrase = NormalizePolicyPhrase(phrase);

        await EnsurePolicyConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        var snapshot = await ReadPolicyTablesAsync(cancellationToken)
            .ConfigureAwait(false);
        var selectedRule = snapshot.RuleRows.SingleOrDefault(
            item => item.RuleId == ruleId);
        if (selectedRule is null
            || !string.Equals(
                selectedRule.PolicySet,
                normalizedPolicySet,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Die ausgewählte Verstoßkategorie ist nicht aktiv oder gehört nicht zum angezeigten Regelset.");
        }

        var phraseRows = await QueryAllAsync(
            "phrase_rules",
            "phrase_id",
            cancellationToken).ConfigureAwait(false);
        var duplicate = phraseRows.SingleOrDefault(row =>
            Integer(row, "rule_id") == ruleId
            && Text(row, "normalized_phrase") == normalizedPhrase);
        if (duplicate is not null)
        {
            if (!IsCurrent(duplicate))
            {
                throw new ArchivedPhraseExistsException(
                    "Diese Phrase ist in der ausgewählten Verstoßkategorie archiviert und kann mit ihren bisherigen Lernzählern wiederhergestellt werden.",
                    new ArchivedPhraseRestoreCandidate(
                        Integer(duplicate, "phrase_id"),
                        Integer(duplicate, "row_version"),
                        normalizedPhrase,
                        normalizedPolicySet,
                        ruleId,
                        selectedRule.RuleCode,
                        PositiveConfirmations: checked(
                            (int)Integer(
                                duplicate,
                                "positive_confirmations")),
                        NegativeConfirmations: checked(
                            (int)Integer(
                                duplicate,
                                "negative_confirmations"))));
            }

            throw new InvalidOperationException(
                "Diese normalisierte Phrase ist in der ausgewählten Verstoßkategorie bereits vorhanden.");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rule_id"] = ruleId,
            ["phrase"] = normalizedPhrase,
            ["manual_override"] = "active",
            ["enabled"] = true,
        };
        await CommitSingleAsync(
            "phrase_rules",
            primaryKey: null,
            expectedRowVersion: null,
            values,
            $"{normalizedPolicySet}-Phrase hinzugefügt",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> UpdatePolicyPhraseAsync(
        long phraseId,
        long expectedRowVersion,
        string policySet,
        string phrase,
        string manualOverride,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(phraseId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRowVersion);
        var normalizedPolicySet = ValidatePolicySet(policySet);
        ArgumentNullException.ThrowIfNull(phrase);
        var normalizedPhrase = NormalizePolicyPhrase(phrase);
        var normalizedOverride = ValidateManualOverride(manualOverride);

        await EnsurePolicyConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        var snapshot = await ReadPolicyTablesAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = snapshot.PhraseRows.SingleOrDefault(
            item => item.PhraseId == phraseId);
        if (current is null
            || !string.Equals(
                current.PolicySet,
                normalizedPolicySet,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Die Blacklist-Phrase gehört nicht zum angezeigten Regelset.");
        }

        if (snapshot.PhraseRows.Any(item =>
                item.PhraseId != phraseId
                && item.RuleId == current.RuleId
                && item.NormalizedPhrase == normalizedPhrase))
        {
            throw new InvalidOperationException(
                "Diese normalisierte Phrase ist in der Verstoßkategorie bereits vorhanden.");
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["phrase"] = normalizedPhrase,
            ["manual_override"] = normalizedOverride,
        };
        await CommitSingleAsync(
            "phrase_rules",
            phraseId,
            expectedRowVersion,
            values,
            $"{normalizedPolicySet}-Phrase inline bearbeitet",
            cancellationToken).ConfigureAwait(false);
        return checked(expectedRowVersion + 1);
    }

    public async Task<long> SetPolicyPhraseEnabledAsync(
        long phraseId,
        long expectedRowVersion,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await SetEnabledAsync(
            "phrase_rules",
            phraseId,
            expectedRowVersion,
            isEnabled,
            "Blacklist-Phrase aktiviert oder deaktiviert",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<long[]> SetPolicyPhrasesEnabledAsync(
        IReadOnlyList<(long PhraseId, long ExpectedRowVersion)> entries,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return [];
        }

        var drafts = entries.Select(entry =>
            new DatabaseRowDraft(
                "phrase_rules",
                entry.PhraseId,
                entry.ExpectedRowVersion,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["enabled"] = isEnabled,
                },
                ArchiveState: null)).ToArray();
        var result = await _database.CommitAsync(
            new DatabaseChangeSet(
                drafts,
                "Blacklist-Phrasen aktiviert oder deaktiviert"),
            cancellationToken).ConfigureAwait(false);
        if (result.ChangedRows != drafts.Length)
        {
            throw new InvalidOperationException(
                "Die Konfigurationsänderung wurde nicht eindeutig gespeichert.");
        }

        return entries.Select(entry => checked(entry.ExpectedRowVersion + 1)).ToArray();
    }

    public Task<long> ArchivePolicyPhraseAsync(
        long phraseId,
        long expectedRowVersion,
        string policySet,
        CancellationToken cancellationToken = default) =>
        SetPolicyPhraseArchivedStateAsync(
            phraseId,
            expectedRowVersion,
            policySet,
            isArchived: true,
            cancellationToken);

    public Task<long> RestorePolicyPhraseAsync(
        long phraseId,
        long expectedRowVersion,
        string policySet,
        CancellationToken cancellationToken = default) =>
        SetPolicyPhraseArchivedStateAsync(
            phraseId,
            expectedRowVersion,
            policySet,
            isArchived: false,
            cancellationToken);

    private async Task<long> SetPolicyPhraseArchivedStateAsync(
        long phraseId,
        long expectedRowVersion,
        string policySet,
        bool isArchived,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(phraseId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRowVersion);
        var normalizedPolicySet = ValidatePolicySet(policySet);

        await EnsurePolicyConfigurationAsync(cancellationToken)
            .ConfigureAwait(false);
        var phraseRows = await QueryAllAsync(
            "phrase_rules",
            "phrase_id",
            cancellationToken).ConfigureAwait(false);
        var phraseRow = phraseRows.SingleOrDefault(
            item => Integer(item, "phrase_id") == phraseId);
        if (phraseRow is null)
        {
            throw new InvalidOperationException(
                "Die Blacklist-Phrase wurde nicht gefunden.");
        }

        var ruleId = Integer(phraseRow, "rule_id");
        var ruleRows = await QueryAllAsync(
            "blacklist_rules",
            "rule_id",
            cancellationToken).ConfigureAwait(false);
        var ruleRow = ruleRows.SingleOrDefault(
            item => Integer(item, "rule_id") == ruleId);
        if (ruleRow is null || !IsCurrent(ruleRow))
        {
            throw new InvalidOperationException(
                "Die übergeordnete Blacklist-Regel ist nicht verfügbar.");
        }

        var scopeId = Integer(ruleRow, "scope_id");
        var scopeRows = await QueryAllAsync(
            "policy_sets",
            "scope_id",
            cancellationToken).ConfigureAwait(false);
        var scopeRow = scopeRows.SingleOrDefault(
            item => Integer(item, "scope_id") == scopeId);
        if (scopeRow is null
            || !IsCurrent(scopeRow)
            || Text(scopeRow, "broadcaster_id")
                != GlobalPolicyStorage.BroadcasterId
            || ToDisplayPolicySet(Text(scopeRow, "set_code"))
                != normalizedPolicySet)
        {
            throw new InvalidOperationException(
                "Die Blacklist-Phrase gehört nicht zum angezeigten Regelset.");
        }

        if (Integer(phraseRow, "row_version") != expectedRowVersion)
        {
            throw new DatabaseConcurrencyException("phrase_rules", phraseId);
        }

        if (IsCurrent(phraseRow) == !isArchived)
        {
            return expectedRowVersion;
        }

        await CommitArchiveSingleAsync(
            "phrase_rules",
            phraseId,
            expectedRowVersion,
            isArchived,
            isArchived
                ? $"{normalizedPolicySet}-Phrase archiviert"
                : $"{normalizedPolicySet}-Phrase wiederhergestellt",
            cancellationToken).ConfigureAwait(false);
        return checked(expectedRowVersion + 1);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _policySetupGate.Dispose();
        _disposed = true;
    }

    private async Task EnsurePolicyConfigurationAsync(
        CancellationToken cancellationToken)
    {
        await _policySetupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _policyStore.EnsureBroadcasterSeededAsync(
                    GlobalPolicyStorage.BroadcasterId,
                    cancellationToken)
                .ConfigureAwait(false);
            var snapshot = await ReadPolicyTablesAsync(cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.RuleIds.ContainsKey(GeneralPolicySet))
            {
                return;
            }

            if (!snapshot.ScopeIds.TryGetValue(GeneralPolicySet, out var generalScopeId))
            {
                throw new InvalidOperationException(
                    "Das verpflichtende General-Regelset fehlt in SQLite.");
            }

            var values = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["scope_id"] = generalScopeId,
                ["rule_code"] = GeneralRuleCode,
                ["title"] = "Allgemeine Blacklist",
                ["prompt_instruction"] =
                    "Bewerte lokale Treffer dieser Regel als möglichen allgemeinen Chatverstoß.",
                ["default_action"] = "timeout",
                ["default_timeout_seconds"] = 600,
                ["public_reason"] = "Verstoß gegen die allgemeinen Chatregeln",
                ["priority"] = 100,
                ["enabled"] = true,
            };

            try
            {
                await CommitSingleAsync(
                    "blacklist_rules",
                    primaryKey: null,
                    expectedRowVersion: null,
                    values,
                    "Initialisierung der editierbaren General-Blacklist",
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                var concurrentSnapshot = await ReadPolicyTablesAsync(
                    cancellationToken).ConfigureAwait(false);
                if (!concurrentSnapshot.RuleIds.ContainsKey(GeneralPolicySet))
                {
                    throw;
                }
            }
        }
        finally
        {
            _policySetupGate.Release();
        }
    }

    private async Task<PolicyTableSnapshot> ReadPolicyTablesAsync(
        CancellationToken cancellationToken)
    {
        var scopeRows = await QueryAllAsync(
            "policy_sets",
            "scope_id",
            cancellationToken).ConfigureAwait(false);
        var scopes = scopeRows
            .Where(row =>
                IsCurrent(row)
                && Text(row, "broadcaster_id")
                    == GlobalPolicyStorage.BroadcasterId)
            .Select(row => new
            {
                Id = Integer(row, "scope_id"),
                PolicySet = ToDisplayPolicySet(Text(row, "set_code")),
            })
            .ToArray();
        var policyByScopeId = scopes.ToDictionary(
            item => item.Id,
            item => item.PolicySet);
        var scopeIds = scopes.ToDictionary(
            item => item.PolicySet,
            item => item.Id,
            StringComparer.Ordinal);

        var ruleRows = await QueryAllAsync(
            "blacklist_rules",
            "rule_id",
            cancellationToken).ConfigureAwait(false);
        var rules = ruleRows
            .Where(row =>
                IsCurrent(row)
                && policyByScopeId.ContainsKey(Integer(row, "scope_id"))
                && !GlobalPolicyStorage.IsRetiredRuleCode(
                    Text(row, "rule_code")))
            .Select(row => new
            {
                Id = Integer(row, "rule_id"),
                RowVersion = Integer(row, "row_version"),
                PolicySet = policyByScopeId[Integer(row, "scope_id")],
                Code = Text(row, "rule_code"),
                Title = Text(row, "title"),
                PublicReason = Text(row, "public_reason"),
                DefaultAction = Text(row, "default_action"),
                DefaultTimeoutSeconds = NullableInteger(
                    row,
                    "default_timeout_seconds"),
                Enabled = Boolean(row, "enabled"),
            })
            .ToArray();
        var ruleDefinitions = rules
            .Where(item => item.Enabled)
            .Select(item => new PolicyRuleConfigurationRow(
                item.Id,
                item.RowVersion,
                item.PolicySet,
                item.Code,
                item.Title,
                item.PublicReason,
                item.DefaultAction,
                item.DefaultTimeoutSeconds is { } timeout
                    ? checked((int)timeout)
                    : null))
            .ToArray();
        var ruleById = rules.ToDictionary(item => item.Id);
        var ruleIds = rules
            .Where(item => item.Enabled)
            .GroupBy(item => item.PolicySet, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(item => PreferredRuleOrder(item.PolicySet, item.Code))
                    .ThenBy(item => item.Id)
                    .First()
                    .Id,
                StringComparer.Ordinal);

        var phraseRows = await QueryAllAsync(
            "phrase_rules",
            "phrase_id",
            cancellationToken).ConfigureAwait(false);
        var phrases = phraseRows
            .Where(row =>
                IsCurrent(row)
                && ruleById.ContainsKey(Integer(row, "rule_id")))
            .Select(row =>
            {
                var rule = ruleById[Integer(row, "rule_id")];
                return new PolicyPhraseConfigurationRow(
                    Integer(row, "phrase_id"),
                    rule.Id,
                    Integer(row, "row_version"),
                    rule.PolicySet,
                    rule.Code,
                    rule.Title,
                    Text(row, "normalized_phrase"),
                    Text(row, "manual_override"),
                    Boolean(row, "enabled"),
                    checked((int)Integer(row, "positive_confirmations")),
                    checked((int)Integer(row, "negative_confirmations")));
            })
            .ToArray();

        return new PolicyTableSnapshot(
            scopeIds,
            ruleIds,
            ruleDefinitions,
            phrases);
    }

    private async Task<IReadOnlyList<CoreDatabaseRow>> QueryAllAsync(
        string tableName,
        string sortColumn,
        CancellationToken cancellationToken)
    {
        var rows = new List<CoreDatabaseRow>();
        var offset = 0;
        while (true)
        {
            var page = await _database.QueryAsync(
                tableName,
                searchText: null,
                sortColumn,
                descending: false,
                offset,
                QueryPageSize,
                cancellationToken).ConfigureAwait(false);
            rows.AddRange(page.Rows);
            offset += page.Rows.Count;
            if (page.Rows.Count == 0 || offset >= page.TotalCount)
            {
                return rows;
            }
        }
    }

    private async Task<long> SetEnabledAsync(
        string tableName,
        long primaryKey,
        long expectedRowVersion,
        bool isEnabled,
        string changeReason,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(primaryKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedRowVersion);
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["enabled"] = isEnabled,
        };
        await CommitSingleAsync(
            tableName,
            primaryKey,
            expectedRowVersion,
            values,
            changeReason,
            cancellationToken).ConfigureAwait(false);
        return checked(expectedRowVersion + 1);
    }

    private async Task CommitSingleAsync(
        string tableName,
        object? primaryKey,
        long? expectedRowVersion,
        IReadOnlyDictionary<string, object?> values,
        string changeReason,
        CancellationToken cancellationToken)
    {
        var draft = new DatabaseRowDraft(
            tableName,
            primaryKey,
            expectedRowVersion,
            values,
            ArchiveState: null);
        var result = await _database.CommitAsync(
            new DatabaseChangeSet([draft], changeReason),
            cancellationToken).ConfigureAwait(false);
        if (result.ChangedRows != 1)
        {
            throw new InvalidOperationException(
                "Die Konfigurationsänderung wurde nicht eindeutig gespeichert.");
        }
    }

    private async Task CommitArchiveSingleAsync(
        string tableName,
        object primaryKey,
        long expectedRowVersion,
        bool isArchived,
        string changeReason,
        CancellationToken cancellationToken)
    {
        var draft = new DatabaseRowDraft(
            tableName,
            primaryKey,
            expectedRowVersion,
            new Dictionary<string, object?>(StringComparer.Ordinal),
            ArchiveState: isArchived);
        var result = await _database.CommitAsync(
            new DatabaseChangeSet([draft], changeReason),
            cancellationToken).ConfigureAwait(false);
        if (result.ChangedRows != 1)
        {
            throw new InvalidOperationException(
                "Die Konfigurationsänderung wurde nicht eindeutig gespeichert.");
        }
    }

    private string RequireBroadcasterId()
    {
        return BroadcasterId
            ?? throw new InvalidOperationException(
                "Noch keine Broadcaster-ID verfügbar. Bitte zuerst in den Einstellungen mit Twitch verbinden.");
    }

    private static string ValidatePolicySet(string value)
    {
        return value switch
        {
            GeneralPolicySet => GeneralPolicySet,
            BackseatgamingPolicySet => BackseatgamingPolicySet,
            _ => throw new ArgumentException(
                "Das Regelset muss General oder Backseatgaming sein.",
                nameof(value)),
        };
    }

    private string NormalizePolicyPhrase(string phrase)
    {
        var normalizedPhrase = _textNormalizer.Process(phrase).RuleNormalizedText;
        if (normalizedPhrase.Length is < 2 or > 500)
        {
            throw new ArgumentException(
                "Die normalisierte Blacklist-Phrase muss zwischen 2 und 500 Zeichen enthalten.",
                nameof(phrase));
        }

        return normalizedPhrase;
    }

    private static string NormalizeWhitelistPhrase(string phrase)
    {
        var lowerPhrase = phrase.ToLowerInvariant();
        if (lowerPhrase.Length is < 1 or > 500)
        {
            throw new ArgumentException(
                "Die Whitelist-Phrase muss zwischen 1 und 500 Zeichen enthalten.",
                nameof(phrase));
        }

        return lowerPhrase;
    }

    private static bool IsSupportedWhitelistSubjectKind(string subjectKind) =>
        subjectKind is ExactMessageSubjectKind or UserLoginSubjectKind;

    private static string ValidateManualOverride(string value)
    {
        return value switch
        {
            "auto" => "auto",
            "active" => "active",
            "inactive" => "inactive",
            _ => throw new ArgumentException(
                "Der Override muss Auto, Aktiv oder Inaktiv sein.",
                nameof(value)),
        };
    }

    private static string ToDisplayPolicySet(string value)
    {
        return value switch
        {
            "general" => GeneralPolicySet,
            "backseatgaming" => BackseatgamingPolicySet,
            _ => throw new InvalidDataException($"Unbekanntes Regelset '{value}'."),
        };
    }

    private static int PreferredRuleOrder(string policySet, string ruleCode)
    {
        return policySet switch
        {
            GeneralPolicySet when ruleCode == GeneralRuleCode => 0,
            BackseatgamingPolicySet when ruleCode == BackseatgamingRuleCode => 0,
            _ => 1,
        };
    }

    private static bool IsCurrent(CoreDatabaseRow row)
    {
        return !row.Values.TryGetValue("archived_utc", out var value) || value is null;
    }

    private static string Text(CoreDatabaseRow row, string column)
    {
        if (!row.Values.TryGetValue(column, out var value) || value is null)
        {
            return string.Empty;
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static long Integer(CoreDatabaseRow row, string column)
    {
        if (!row.Values.TryGetValue(column, out var value) || value is null)
        {
            throw new InvalidDataException($"SQLite-Spalte '{column}' fehlt.");
        }

        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static long? NullableInteger(
        CoreDatabaseRow row,
        string column)
    {
        return row.Values.TryGetValue(column, out var value) && value is not null
            ? Convert.ToInt64(value, CultureInfo.InvariantCulture)
            : null;
    }

    private static bool Boolean(CoreDatabaseRow row, string column)
    {
        return Integer(row, column) != 0;
    }

    private static DateTimeOffset? NullableTimestamp(CoreDatabaseRow row, string column)
    {
        var value = Text(row, column);
        return value.Length == 0
            ? null
            : DateTimeOffset.Parse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record PolicyTableSnapshot(
        IReadOnlyDictionary<string, long> ScopeIds,
        IReadOnlyDictionary<string, long> RuleIds,
        IReadOnlyList<PolicyRuleConfigurationRow> RuleRows,
        IReadOnlyList<PolicyPhraseConfigurationRow> PhraseRows);
}
