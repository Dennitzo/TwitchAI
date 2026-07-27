using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using TwitchAI.Core.Services;

namespace TwitchAI.App.ViewModels;

public sealed partial class AppState : ObservableObject
{
    public ObservableCollection<ModerationCaseItem> ModerationCases { get; } = [];

    public LocalAiRunItem CurrentLocalAiRun { get; } = new();

    public ObservableCollection<LocalAiRunItem> QueuedLocalAiRuns { get; } = [];

    public ObservableCollection<PolicyPhraseItem> PolicyPhrases { get; } = [];

    public ObservableCollection<WhitelistPhraseItem> WhitelistPhrases { get; } = [];

    public ObservableCollection<AuditItem> AuditItems { get; } = [];

    [ObservableProperty]
    public partial string ChannelDisplayName { get; set; } = "Nicht verbunden";

    [ObservableProperty]
    public partial string CategoryName { get; set; } = "Kategorie unbekannt";

    [ObservableProperty]
    public partial string ActivePolicies { get; set; } = "Nur General";

    [ObservableProperty]
    public partial string TwitchStatus { get; set; } = "Twitch offline";

    [ObservableProperty]
    public partial string EventSubStatus { get; set; } = "EventSub offline";

    [ObservableProperty]
    public partial string LmStudioStatus { get; set; } = "LM Studio prüfen";

    [ObservableProperty]
    public partial string DatabaseStatus { get; set; } = "SQLite startet";

    [ObservableProperty]
    public partial bool IsAnalysisRunning { get; set; }

    [ObservableProperty]
    public partial bool IsTwitchConnectionReady { get; set; }

    [ObservableProperty]
    public partial int SkippedOverloadCount { get; set; }

    [ObservableProperty]
    public partial int SkippedWhitelistCount { get; set; }

    [ObservableProperty]
    public partial int QueuedLocalAiRunCount { get; set; }

    [ObservableProperty]
    public partial int QueuedLocalAiMessageCount { get; set; }
}

public sealed partial class ModerationCaseItem : ObservableObject
{
    private bool _suppressViolationRuleFallback;

    public required long CaseId { get; init; }

    public required string ChannelId { get; init; }

    public required string UserName { get; init; }

    public required string OriginalMessage { get; init; }

    public required string CategoryName { get; init; }

    public string? CategoryId { get; init; }

    public required DateTimeOffset ReceivedAt { get; init; }

    public string ReceivedAtDisplay =>
        $"Empfangen · {ReceivedAt.ToLocalTime():dd.MM.yyyy HH:mm:ss} (lokal)";

    public required string MessageId { get; init; }

    public string MessageIdLabel => $"Message-ID: {MessageId}";

    public required string UserId { get; init; }

    public required string IdentityFingerprint { get; init; }

    public required long RowVersion { get; set; }

    [ObservableProperty]
    public partial string Action { get; set; } = "timeout";

    public Visibility TimeoutEditorVisibility =>
        Action == "timeout"
            ? Visibility.Visible
            : Visibility.Collapsed;

    [ObservableProperty]
    public partial double TimeoutSeconds { get; set; } =
        ModerationActionRules.DefaultTimeoutSeconds;

    [ObservableProperty]
    public partial string Reason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PolicySet { get; set; } = "General";

    [ObservableProperty]
    public partial string? RuleCode { get; set; }

    [ObservableProperty]
    public partial PolicyRuleOptionItem? SelectedViolationRule { get; set; }

    public ObservableCollection<PolicyRuleOptionItem> ViolationRuleOptions { get; } = [];

    [ObservableProperty]
    public partial double Confidence { get; set; }

    public string ConfidenceLabel => $"Konfidenz {Confidence:P0}";

    public IReadOnlyList<string> LocalMatchCodes { get; init; } = [];

    public long? AiLatencyMilliseconds { get; init; }

    public string AiDurationLabel => AiLatencyMilliseconds is { } duration
        ? $"AI {duration:N0} ms"
        : "AI-Dauer unbekannt";

    partial void OnConfidenceChanged(double value) =>
        OnPropertyChanged(nameof(ConfidenceLabel));

    partial void OnActionChanged(string value) =>
        OnPropertyChanged(nameof(TimeoutEditorVisibility));

    [ObservableProperty]
    public partial string AvailabilityLabel { get; set; } = "Nachricht verfügbar";

    [ObservableProperty]
    public partial string Status { get; set; } = "Pending";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool CanExecute =>
        !IsBusy
        && HasUsableViolationRule
        && Status is "Pending" or "Extern erledigt · Lernentscheidung offen";

    public bool CanDiscard => !IsBusy && Status == "Pending";

    private bool HasUsableViolationRule =>
        SelectedViolationRule is not null
        || (!string.IsNullOrWhiteSpace(RuleCode)
            && PolicySet is "General" or "Backseatgaming");

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanDiscard));
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanDiscard));
    }

    partial void OnRuleCodeChanged(string? value)
    {
        OnPropertyChanged(nameof(CanExecute));
        if (_suppressViolationRuleFallback)
        {
            return;
        }

        var matchingOption = ViolationRuleOptions.FirstOrDefault(
            option => string.Equals(
                option.Code,
                value,
                StringComparison.OrdinalIgnoreCase));
        if (!ReferenceEquals(SelectedViolationRule, matchingOption))
        {
            SelectedViolationRule = matchingOption;
        }
    }

    partial void OnPolicySetChanged(string value) =>
        OnPropertyChanged(nameof(CanExecute));

    partial void OnSelectedViolationRuleChanged(PolicyRuleOptionItem? value)
    {
        if (value is null)
        {
            // WinUI temporarily clears SelectedItem while the option source is
            // refreshed. Keep the validated/persisted rule code so this visual
            // transition cannot disable the operator actions.
            OnPropertyChanged(nameof(CanExecute));
            return;
        }

        if (!string.Equals(
                RuleCode,
                value.Code,
                StringComparison.Ordinal))
        {
            RuleCode = value.Code;
        }

        if (_suppressViolationRuleFallback)
        {
            return;
        }

        PolicySet = value.PolicySet;
        Reason = value.PublicReason;
    }

    public void ReplaceViolationRuleOptions(
        IReadOnlyList<PolicyRuleOptionItem> options,
        string? selectedRuleCode,
        bool applyFallbackDefaults = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        _suppressViolationRuleFallback = true;
        try
        {
            SynchronizeViolationRuleOptions(options);

            var selectedOption = ViolationRuleOptions.FirstOrDefault(
                option => !string.IsNullOrWhiteSpace(selectedRuleCode)
                    && string.Equals(
                        option.Code,
                        selectedRuleCode.Trim(),
                        StringComparison.OrdinalIgnoreCase));

            if (selectedOption is null && ViolationRuleOptions.Count > 0)
            {
                selectedOption = ViolationRuleOptions[0];
                applyFallbackDefaults = true;
            }

            SelectedViolationRule = selectedOption;
            RuleCode = selectedOption?.Code;
        }
        finally
        {
            _suppressViolationRuleFallback = false;
        }

        if (applyFallbackDefaults && SelectedViolationRule is { } fallback)
        {
            RuleCode = fallback.Code;
            PolicySet = fallback.PolicySet;
            Reason = fallback.PublicReason;
            OnPropertyChanged(nameof(CanExecute));
        }
    }

    private void SynchronizeViolationRuleOptions(
        IReadOnlyList<PolicyRuleOptionItem> options)
    {
        var desiredCodes = options
            .Select(option => option.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var index = ViolationRuleOptions.Count - 1; index >= 0; index--)
        {
            if (!desiredCodes.Contains(ViolationRuleOptions[index].Code))
            {
                ViolationRuleOptions.RemoveAt(index);
            }
        }

        for (var desiredIndex = 0; desiredIndex < options.Count; desiredIndex++)
        {
            var desired = options[desiredIndex];
            var existingIndex = -1;
            for (var index = 0; index < ViolationRuleOptions.Count; index++)
            {
                if (string.Equals(
                        ViolationRuleOptions[index].Code,
                        desired.Code,
                        StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                ViolationRuleOptions.Insert(desiredIndex, desired);
                continue;
            }

            var existing = ViolationRuleOptions[existingIndex];
            if (existingIndex != desiredIndex)
            {
                ViolationRuleOptions.Move(existingIndex, desiredIndex);
            }

            if (!existing.Equals(desired))
            {
                ViolationRuleOptions[desiredIndex] = desired;
            }
        }
    }

    public ObservableCollection<TriggerPhraseItem> TriggerPhrases { get; } = [];
}

public sealed partial class TriggerPhraseItem : ObservableObject
{
    public TriggerPhraseItem(string phrase, bool isSelected = true)
    {
        Phrase = phrase;
        IsSelected = isSelected;
    }

    public string Phrase { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

public sealed partial class PolicyPhraseItem : ObservableObject
{
    public PolicyPhraseItem(
        long phraseId,
        long ruleId,
        long rowVersion,
        string policySet,
        string code,
        string ruleTitle,
        string phrase,
        string @override,
        bool isEnabled,
        int positiveCount,
        int negativeCount)
    {
        PhraseId = phraseId;
        RuleId = ruleId;
        RowVersion = rowVersion;
        PolicySet = policySet;
        Code = code;
        RuleTitle = ruleTitle;
        Phrase = phrase;
        PersistedPhrase = phrase;
        Override = @override;
        PersistedOverride = @override;
        IsEnabled = isEnabled;
        PersistedIsEnabled = isEnabled;
        PositiveCount = positiveCount;
        NegativeCount = negativeCount;
    }

    public long PhraseId { get; }

    [ObservableProperty]
    public partial long RuleId { get; set; }

    public long RowVersion { get; private set; }

    public string Code { get; }

    public string RuleTitle { get; }

    public string RuleDisplayName => $"{RuleTitle} · {Code}";

    public ObservableCollection<PolicyRuleOptionItem> RuleOptions { get; } = [];

    [ObservableProperty]
    public partial PolicyRuleOptionItem? SelectedRuleOption { get; set; }

    public void ReplaceRuleOptions(
        IEnumerable<PolicyRuleOptionItem> options)
    {
        RuleOptions.Clear();
        foreach (var option in options)
        {
            RuleOptions.Add(option);
        }

        SelectedRuleOption = RuleOptions.FirstOrDefault(
            option => option.RuleId == RuleId);
    }

    [ObservableProperty]
    public partial string PolicySet { get; set; }

    [ObservableProperty]
    public partial string Phrase { get; set; }

    public string PersistedPhrase { get; private set; }

    [ObservableProperty]
    public partial string Override { get; set; }

    public string PersistedOverride { get; private set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public bool PersistedIsEnabled { get; private set; }

    [ObservableProperty]
    public partial int PositiveCount { get; set; }

    [ObservableProperty]
    public partial int NegativeCount { get; set; }

    public double ApprovalRatio =>
        PositiveCount + NegativeCount == 0
            ? 0
            : (double)PositiveCount / (PositiveCount + NegativeCount);

    public string FeedbackLabel => $"{PositiveCount} / {NegativeCount}";

    partial void OnPositiveCountChanged(int value) =>
        OnPropertyChanged(nameof(FeedbackLabel));

    partial void OnNegativeCountChanged(int value) =>
        OnPropertyChanged(nameof(FeedbackLabel));

    public void AcceptEnabled(bool isEnabled, long rowVersion)
    {
        RowVersion = rowVersion;
        PersistedIsEnabled = isEnabled;
        IsEnabled = isEnabled;
    }

    public void RestoreEnabled()
    {
        IsEnabled = PersistedIsEnabled;
    }

    public bool HasEditableChanges =>
        !string.Equals(Phrase, PersistedPhrase, StringComparison.Ordinal)
        || !string.Equals(Override, PersistedOverride, StringComparison.Ordinal);

    public void AcceptEditableValues(
        string phrase,
        string @override,
        long rowVersion)
    {
        RowVersion = rowVersion;
        Phrase = phrase;
        PersistedPhrase = phrase;
        Override = @override;
        PersistedOverride = @override;
    }

    public void RestoreEditableValues()
    {
        Phrase = PersistedPhrase;
        Override = PersistedOverride;
    }
}

public sealed record PolicyRuleOptionItem(
    long RuleId,
    string PolicySet,
    string Code,
    string DisplayName,
    string PublicReason,
    string DefaultAction,
    int? DefaultTimeoutSeconds)
{
    public string DisplayLabel => $"{DisplayName} · {Code}";
}

public sealed partial class WhitelistPhraseItem : ObservableObject
{
    public WhitelistPhraseItem(
        long allowlistId,
        long rowVersion,
        string broadcasterId,
        string originalPhrase,
        string normalizedPhrase,
        string source,
        bool isUser,
        bool isEnabled,
        int hitCount,
        DateTimeOffset? lastHitAt)
    {
        AllowlistId = allowlistId;
        RowVersion = rowVersion;
        BroadcasterId = broadcasterId;
        OriginalPhrase = originalPhrase;
        PersistedOriginalPhrase = originalPhrase;
        NormalizedPhrase = normalizedPhrase;
        Source = source;
        IsUser = isUser;
        PersistedIsUser = isUser;
        IsEnabled = isEnabled;
        PersistedIsEnabled = isEnabled;
        HitCount = hitCount;
        LastHitAt = lastHitAt;
    }

    public long AllowlistId { get; }

    public long RowVersion { get; private set; }

    public string BroadcasterId { get; }

    [ObservableProperty]
    public partial string OriginalPhrase { get; set; }

    public string PersistedOriginalPhrase { get; private set; }

    [ObservableProperty]
    public partial string NormalizedPhrase { get; set; }

    public string Source { get; }

    [ObservableProperty]
    public partial bool IsUser { get; set; }

    public bool PersistedIsUser { get; private set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public bool PersistedIsEnabled { get; private set; }

    [ObservableProperty]
    public partial int HitCount { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? LastHitAt { get; set; }

    public void AcceptEnabled(bool isEnabled, long rowVersion)
    {
        RowVersion = rowVersion;
        PersistedIsEnabled = isEnabled;
        IsEnabled = isEnabled;
    }

    public void RestoreEnabled()
    {
        IsEnabled = PersistedIsEnabled;
    }

    public void AcceptUser(bool isUser, long rowVersion)
    {
        RowVersion = rowVersion;
        PersistedIsUser = isUser;
        IsUser = isUser;
    }

    public void RestoreUser()
    {
        IsUser = PersistedIsUser;
    }

    public bool HasEditableChanges =>
        !string.Equals(
            OriginalPhrase,
            PersistedOriginalPhrase,
            StringComparison.Ordinal);

    public void AcceptEditableValue(
        string originalPhrase,
        string normalizedPhrase,
        long rowVersion)
    {
        RowVersion = rowVersion;
        OriginalPhrase = originalPhrase;
        PersistedOriginalPhrase = originalPhrase;
        NormalizedPhrase = normalizedPhrase;
    }

    public void RestoreEditableValue()
    {
        OriginalPhrase = PersistedOriginalPhrase;
    }
}

// The audit ID is the persistent moderation_cases.case_id. It is available
// for every terminal moderation outcome, unlike action_attempts.execution_id,
// and SQLite assigns it monotonically for the lifetime of the database.
public sealed record AuditItem(
    long Id,
    DateTimeOffset OccurredAt,
    string UserName,
    string OriginalMessage,
    string Decision,
    string TwitchResult,
    string CategoryName,
    string PolicySet,
    int? TimeoutSeconds,
    string Reason)
{
    public string OccurredAtDisplay =>
        OccurredAt.ToLocalTime().ToString(
            "dd.MM.yyyy HH:mm:ss",
            System.Globalization.CultureInfo.CurrentCulture);

    public string DecisionDetail =>
        TimeoutSeconds is { } seconds
            ? $"{Decision} · {seconds:N0} s"
            : Decision;

    public Visibility ReasonVisibility =>
        string.IsNullOrWhiteSpace(Reason)
            ? Visibility.Collapsed
            : Visibility.Visible;
}

public sealed partial class DatabaseTableItem : ObservableObject
{
    public DatabaseTableItem(
        string name,
        string displayName,
        string access,
        string primaryKey,
        bool isEditable,
        bool supportsSoftArchive)
    {
        Name = name;
        DisplayName = displayName;
        Access = access;
        PrimaryKey = primaryKey;
        IsEditable = isEditable;
        SupportsSoftArchive = supportsSoftArchive;
    }

    public string Name { get; }

    public string DisplayName { get; }

    public string Access { get; }

    public string PrimaryKey { get; }

    public bool IsEditable { get; }

    public bool SupportsSoftArchive { get; }

    [ObservableProperty]
    public partial string RowCountLabel { get; set; } = "Nicht geladen";
}

public enum DatabaseDraftState
{
    None,
    Insert,
    Update,
    Archive,
    Restore,
}

public sealed partial class DatabaseCellItem : ObservableObject
{
    private string _originalDisplayValue;

    public DatabaseCellItem(
        string columnName,
        object? originalValue,
        string displayValue,
        bool isEditable)
    {
        ColumnName = columnName;
        OriginalValue = originalValue;
        _originalDisplayValue = displayValue;
        DisplayValue = displayValue;
        IsEditable = isEditable;
    }

    public string ColumnName { get; }

    public object? OriginalValue { get; private set; }

    public bool IsEditable { get; }

    public bool IsChanged =>
        !StringComparer.Ordinal.Equals(_originalDisplayValue, DisplayValue);

    [ObservableProperty]
    public partial string DisplayValue { get; set; } = string.Empty;

    public event EventHandler? DraftChanged;

    partial void OnDisplayValueChanged(string value)
    {
        OnPropertyChanged(nameof(IsChanged));
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        DisplayValue = _originalDisplayValue;
    }

    public void Accept(object? value, string displayValue)
    {
        OriginalValue = value;
        _originalDisplayValue = displayValue;
        DisplayValue = displayValue;
        OnPropertyChanged(nameof(IsChanged));
    }
}

public sealed partial class DatabaseRowItem : ObservableObject
{
    private readonly bool _originalIsArchived;
    private bool _targetIsArchived;

    public DatabaseRowItem(
        IReadOnlyList<DatabaseCellItem> cells,
        object? primaryKeyValue,
        long? entityId,
        long? rowVersion,
        bool isArchived,
        bool isNew)
    {
        Cells = new ObservableCollection<DatabaseCellItem>(cells);
        PrimaryKeyValue = primaryKeyValue;
        EntityId = entityId;
        RowVersion = rowVersion;
        IsNew = isNew;
        _originalIsArchived = isArchived;
        _targetIsArchived = isArchived;

        foreach (var cell in Cells)
        {
            cell.DraftChanged += OnCellDraftChanged;
        }
    }

    public ObservableCollection<DatabaseCellItem> Cells { get; }

    public object? PrimaryKeyValue { get; }

    public long? EntityId { get; }

    public long? RowVersion { get; }

    public bool IsNew { get; }

    public bool IsArchived => _targetIsArchived;

    public DatabaseDraftState DraftState
    {
        get
        {
            if (IsNew)
            {
                return DatabaseDraftState.Insert;
            }

            if (_targetIsArchived != _originalIsArchived)
            {
                return _targetIsArchived
                    ? DatabaseDraftState.Archive
                    : DatabaseDraftState.Restore;
            }

            return Cells.Any(cell => cell.IsEditable && cell.IsChanged)
                ? DatabaseDraftState.Update
                : DatabaseDraftState.None;
        }
    }

    public string DraftLabel => DraftState switch
    {
        DatabaseDraftState.Insert => "Neu",
        DatabaseDraftState.Update => "Geändert",
        DatabaseDraftState.Archive => "Wird gelöscht (historisch archiviert)",
        DatabaseDraftState.Restore => "Wird wiederhergestellt",
        _ when IsArchived => "Archiviert",
        _ => "Unverändert",
    };

    public event EventHandler? DraftChanged;

    public void ToggleArchived()
    {
        if (IsNew)
        {
            return;
        }

        var targetIsArchived = !_targetIsArchived;
        foreach (var cell in Cells)
        {
            cell.Reset();
        }

        _targetIsArchived = targetIsArchived;
        NotifyDraftChanged();
    }

    public void ResetDraft()
    {
        _targetIsArchived = _originalIsArchived;
        foreach (var cell in Cells)
        {
            cell.Reset();
        }

        NotifyDraftChanged();
    }

    private void OnCellDraftChanged(object? sender, EventArgs e)
    {
        if (_targetIsArchived != _originalIsArchived)
        {
            _targetIsArchived = _originalIsArchived;
            OnPropertyChanged(nameof(IsArchived));
        }

        NotifyDraftChanged();
    }

    private void NotifyDraftChanged()
    {
        OnPropertyChanged(nameof(DraftState));
        OnPropertyChanged(nameof(DraftLabel));
        OnPropertyChanged(nameof(IsArchived));
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }
}
