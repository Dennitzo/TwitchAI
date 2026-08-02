using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using TwitchAI.App.Services;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.App.ViewModels;

public enum LearningDatasetViewMode
{
    GeneralModeration,
    BackseatgamingModeration,
    Allowed,
}

public sealed record LearningRuleOption(
    string Code,
    PolicySetCode PolicySet,
    string DisplayName);

public sealed record LearningActionOption(
    string Code,
    string DisplayName);

public readonly record struct LearningDatasetSaveSnapshot(
    long EditRevision,
    long ExpectedRowVersion,
    LearningDatasetDraft Draft);

public sealed partial class LearningDatasetItem : ObservableObject
{
    private string _persistedOriginalText;
    private PolicySetCode _persistedPolicySet;
    private string _persistedRuleCode;
    private string _persistedActionCode;
    private int? _persistedTimeoutSeconds;
    private string _persistedReason;
    private long _editRevision;
    private bool _isApplyingPersistedState;

    public LearningDatasetItem(
        LearningDatasetRow row,
        IReadOnlyList<LearningRuleOption> ruleOptions,
        IReadOnlyList<LearningActionOption> actionOptions)
    {
        Source = row.Source;
        DisplayId = row.DisplayId;
        Decision = row.Decision;
        OccurredAtUtc = row.OccurredAtUtc;
        SourceLabel = row.SourceLabel;
        RuleOptions = EnsureCurrentRuleOption(row, ruleOptions);
        ActionOptions = actionOptions;

        OriginalText = row.OriginalText;
        PolicySet = row.PolicySet;
        RuleCode = row.RuleCode;
        ActionCode = ToActionCode(row.Action);
        TimeoutSeconds = row.TimeoutSeconds;
        Reason = row.Reason;
        RowVersion = row.RowVersion;

        _persistedOriginalText = OriginalText;
        _persistedPolicySet = PolicySet;
        _persistedRuleCode = RuleCode;
        _persistedActionCode = ActionCode;
        _persistedTimeoutSeconds = TimeoutSeconds;
        _persistedReason = Reason;
        _editRevision = 0;
    }

    public LearningSourceKey Source { get; }

    public string DisplayId { get; }

    public LearningDecision Decision { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string OccurredAtLocal => OccurredAtUtc
        .ToLocalTime()
        .ToString("g", CultureInfo.CurrentCulture);

    public string SourceLabel { get; }

    public IReadOnlyList<LearningRuleOption> RuleOptions { get; }

    public IReadOnlyList<LearningActionOption> ActionOptions { get; }

    public long RowVersion { get; private set; }

    public long EditRevision => _editRevision;

    [ObservableProperty]
    public partial string OriginalText { get; set; }

    [ObservableProperty]
    public partial PolicySetCode PolicySet { get; set; }

    [ObservableProperty]
    public partial string RuleCode { get; set; }

    [ObservableProperty]
    public partial string ActionCode { get; set; }

    [ObservableProperty]
    public partial int? TimeoutSeconds { get; set; }

    [ObservableProperty]
    public partial string Reason { get; set; }

    [ObservableProperty]
    public partial bool IsSaving { get; set; }

    public string CategoryTitle => RuleOptions
        .FirstOrDefault(option => string.Equals(
            option.Code,
            RuleCode,
            StringComparison.OrdinalIgnoreCase))
        ?.DisplayName
        ?? ViolationCategoryOption.GetFallbackDisplayName(RuleCode);

    public string ActionLabel => ActionCode switch
    {
        "delete" => "Nachricht löschen",
        "timeout" => "Timeout",
        "ban" => "Ban",
        _ => "Erlaubt",
    };

    public bool IsTimeoutAction =>
        Decision == LearningDecision.Moderate
        && string.Equals(ActionCode, "timeout", StringComparison.OrdinalIgnoreCase);

    public double TimeoutSecondsInput
    {
        get => TimeoutSeconds ?? double.NaN;
        set
        {
            if (double.IsNaN(value))
            {
                TimeoutSeconds = null;
                return;
            }

            if (double.IsInfinity(value)
                || value != Math.Truncate(value)
                || value is < 1 or > 1_209_600)
            {
                OnPropertyChanged();
                return;
            }

            TimeoutSeconds = checked((int)value);
        }
    }

    public bool HasEditableChanges =>
        !string.Equals(
            OriginalText,
            _persistedOriginalText,
            StringComparison.Ordinal)
        || !string.Equals(
            RuleCode,
            _persistedRuleCode,
            StringComparison.OrdinalIgnoreCase)
        || PolicySet != _persistedPolicySet
        || !string.Equals(
            ActionCode,
            _persistedActionCode,
            StringComparison.OrdinalIgnoreCase)
        || TimeoutSeconds != _persistedTimeoutSeconds
        || !string.Equals(Reason, _persistedReason, StringComparison.Ordinal);

    partial void OnOriginalTextChanged(string value) => MarkEdited();

    partial void OnPolicySetChanged(PolicySetCode value) => MarkEdited();

    partial void OnRuleCodeChanged(string value)
    {
        MarkEdited();
        var selected = RuleOptions.FirstOrDefault(option => string.Equals(
            option.Code,
            value,
            StringComparison.OrdinalIgnoreCase));
        if (selected is not null)
        {
            PolicySet = selected.PolicySet;
        }

        OnPropertyChanged(nameof(CategoryTitle));
    }

    partial void OnActionCodeChanged(string value)
    {
        MarkEdited();
        var normalized = value.Trim().ToLowerInvariant();
        if (!string.Equals(value, normalized, StringComparison.Ordinal))
        {
            ActionCode = normalized;
            return;
        }

        if (normalized == "timeout" && TimeoutSeconds is null)
        {
            TimeoutSeconds = ModerationActionRules.DefaultTimeoutSeconds;
        }
        else if (normalized is "delete" or "ban" or "none")
        {
            TimeoutSeconds = null;
        }

        OnPropertyChanged(nameof(ActionLabel));
        OnPropertyChanged(nameof(IsTimeoutAction));
        OnPropertyChanged(nameof(TimeoutSecondsInput));
    }

    partial void OnTimeoutSecondsChanged(int? value)
    {
        MarkEdited();
        OnPropertyChanged(nameof(TimeoutSecondsInput));
    }

    partial void OnReasonChanged(string value) => MarkEdited();

    public LearningDatasetDraft CreateDraft()
    {
        var action = Decision == LearningDecision.Allow
            ? ModerationActionKind.None
            : ParseAction(ActionCode);
        return new LearningDatasetDraft(
            OriginalText,
            Decision,
            PolicySet,
            RuleCode,
            action,
            action == ModerationActionKind.Timeout
                ? TimeoutSeconds
                : null,
            Reason);
    }

    public LearningDatasetSaveSnapshot CaptureSaveSnapshot() =>
        new(_editRevision, RowVersion, CreateDraft());

    public void AcceptSaved(
        LearningDatasetRow row,
        long savedEditRevision)
    {
        RowVersion = row.RowVersion;
        _persistedOriginalText = row.OriginalText;
        _persistedPolicySet = row.PolicySet;
        _persistedRuleCode = row.RuleCode;
        _persistedActionCode = ToActionCode(row.Action);
        _persistedTimeoutSeconds = row.TimeoutSeconds;
        _persistedReason = row.Reason;

        if (_editRevision != savedEditRevision)
        {
            return;
        }

        _isApplyingPersistedState = true;
        try
        {
            OriginalText = row.OriginalText;
            PolicySet = row.PolicySet;
            RuleCode = row.RuleCode;
            ActionCode = ToActionCode(row.Action);
            TimeoutSeconds = row.TimeoutSeconds;
            Reason = row.Reason;
        }
        finally
        {
            _isApplyingPersistedState = false;
        }
    }

    public void Restore()
    {
        _isApplyingPersistedState = true;
        try
        {
            OriginalText = _persistedOriginalText;
            RuleCode = _persistedRuleCode;
            PolicySet = _persistedPolicySet;
            ActionCode = _persistedActionCode;
            TimeoutSeconds = _persistedTimeoutSeconds;
            Reason = _persistedReason;
        }
        finally
        {
            _isApplyingPersistedState = false;
        }

        unchecked
        {
            _editRevision++;
        }
    }

    private void MarkEdited()
    {
        if (_isApplyingPersistedState)
        {
            return;
        }

        unchecked
        {
            _editRevision++;
        }
    }

    private static IReadOnlyList<LearningRuleOption> EnsureCurrentRuleOption(
        LearningDatasetRow row,
        IReadOnlyList<LearningRuleOption> options)
    {
        if (options.Any(option => string.Equals(
                option.Code,
                row.RuleCode,
                StringComparison.OrdinalIgnoreCase)))
        {
            return options;
        }

        return
        [
            .. options,
            new LearningRuleOption(
                row.RuleCode,
                row.PolicySet,
                ViolationCategoryOption.GetFallbackDisplayName(row.RuleCode)),
        ];
    }

    private static ModerationActionKind ParseAction(string action) =>
        action.Trim().ToLowerInvariant() switch
        {
            "delete" => ModerationActionKind.Delete,
            "timeout" => ModerationActionKind.Timeout,
            "ban" => ModerationActionKind.Ban,
            "none" => ModerationActionKind.None,
            _ => throw new InvalidOperationException(
                "Die ausgewählte Moderationsaktion ist ungültig."),
        };

    private static string ToActionCode(ModerationActionKind action) =>
        action switch
        {
            ModerationActionKind.Delete => "delete",
            ModerationActionKind.Timeout => "timeout",
            ModerationActionKind.Ban => "ban",
            _ => "none",
        };
}

public sealed partial class LearningDatasetViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 100;
    private static readonly HashSet<string> SortableColumns =
    [
        "DisplayId",
        "OriginalText",
        "RuleCode",
        "Action",
        "TimeoutSeconds",
        "Reason",
        "SourceLabel",
        "OccurredAtUtc",
    ];

    private static readonly IReadOnlyList<LearningActionOption>
        ModerationActions =
    [
        new("delete", "Nachricht löschen"),
        new("timeout", "Timeout"),
        new("ban", "Ban"),
    ];

    private static readonly IReadOnlyList<LearningActionOption> AllowActions =
    [
        new("none", "Erlaubt"),
    ];

    private readonly ILearningDatasetService _datasetService;
    private readonly IConfigurationDataService _configuration;
    private readonly ITableSortPreferenceStore _sortPreferences;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private CancellationTokenSource? _searchCancellation;
    private LearningDatasetViewMode? _mode;
    private PolicySetCode? _queryPolicySet;
    private LearningDecision _decision;
    private string _sortScope = string.Empty;
    private int _pageIndex;
    private bool _disposed;

    public LearningDatasetViewModel(
        ILearningDatasetService datasetService,
        IConfigurationDataService configuration,
        ITableSortPreferenceStore sortPreferences)
    {
        _datasetService = datasetService;
        _configuration = configuration;
        _sortPreferences = sortPreferences;
    }

    public ObservableCollection<LearningDatasetItem> Items { get; } = [];

    public LearningDatasetViewMode Mode => _mode
        ?? LearningDatasetViewMode.GeneralModeration;

    public bool IsAllowedView => _mode == LearningDatasetViewMode.Allowed;

    public string SortColumn { get; private set; } = "OccurredAtUtc";

    public bool SortDescending { get; private set; } = true;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    public bool CanRemoveSelected => SelectedCount > 0 && !IsBusy;

    public bool CanGoPrevious => _pageIndex > 0 && !IsBusy;

    public bool CanGoNext => (_pageIndex + 1) * PageSize < TotalCount
        && !IsBusy;

    public string PageLabel => TotalCount == 0
        ? "Keine Learning-Beispiele"
        : $"Seite {_pageIndex + 1:N0} von {Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize)):N0} · {TotalCount:N0} Einträge";

    public string Description => IsAllowedView
        ? "Menschlich als erlaubt bestätigte Beispiele. Sie dienen der AI als semantischer Kontext und überspringen die Analyse nicht."
        : "Menschlich bestätigte Moderationsbeispiele, die als Kontext an die lokale AI übergeben werden.";

    public void Configure(LearningDatasetViewMode mode)
    {
        if (_mode is { } configuredMode)
        {
            if (configuredMode != mode)
            {
                throw new InvalidOperationException(
                    "Eine Learning-Ansicht kann nicht nachträglich umkonfiguriert werden.");
            }

            return;
        }

        _mode = mode;
        switch (mode)
        {
            case LearningDatasetViewMode.GeneralModeration:
                _decision = LearningDecision.Moderate;
                _queryPolicySet = PolicySetCode.General;
                _sortScope = "learning.blacklist";
                break;
            case LearningDatasetViewMode.BackseatgamingModeration:
                _decision = LearningDecision.Moderate;
                _queryPolicySet = PolicySetCode.Backseatgaming;
                _sortScope = "learning.backseatgaming";
                break;
            case LearningDatasetViewMode.Allowed:
                _decision = LearningDecision.Allow;
                _queryPolicySet = null;
                _sortScope = "learning.whitelist";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }

        var preference = _sortPreferences.GetOrDefault(
            _sortScope,
            "OccurredAtUtc",
            defaultDescending: true,
            SortableColumns);
        SortColumn = preference.Column;
        SortDescending = preference.Descending;
        OnPropertyChanged(nameof(IsAllowedView));
        OnPropertyChanged(nameof(Description));
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        EnsureConfigured();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            IsBusy = true;
            HasError = false;
            await LoadCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer real-time search superseded this query.
        }
        catch (Exception exception)
        {
            StatusMessage = FriendlyError(exception);
            HasError = true;
        }
        finally
        {
            IsBusy = false;
            _operationGate.Release();
        }
    }

    public async Task SortByColumnAsync(
        string columnName,
        CancellationToken cancellationToken = default)
    {
        if (!SortableColumns.Contains(columnName))
        {
            return;
        }

        if (string.Equals(SortColumn, columnName, StringComparison.Ordinal))
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = columnName;
            SortDescending = false;
        }

        _sortPreferences.Save(_sortScope, SortColumn, SortDescending);
        _pageIndex = 0;
        await LoadAsync(cancellationToken);
    }

    public async Task SaveAsync(
        LearningDatasetItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.HasEditableChanges)
        {
            return;
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            IsBusy = true;
            HasError = false;
            item.IsSaving = true;
            while (item.HasEditableChanges)
            {
                var snapshot = item.CaptureSaveSnapshot();
                var updated = await _datasetService.UpdateAsync(
                    item.Source,
                    snapshot.ExpectedRowVersion,
                    snapshot.Draft,
                    cancellationToken);
                item.AcceptSaved(updated, snapshot.EditRevision);
            }

            StatusMessage = $"{item.DisplayId} wurde automatisch gespeichert.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Keep the newest unsaved edit visible so it can be retried.
        }
        catch (Exception exception)
        {
            StatusMessage = FriendlyError(exception);
            HasError = true;
        }
        finally
        {
            item.IsSaving = false;
            IsBusy = false;
            _operationGate.Release();
        }
    }

    public async Task RemoveAsync(
        IReadOnlyList<LearningDatasetItem> items,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
        {
            return;
        }

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            IsBusy = true;
            HasError = false;
            var removed = 0;
            foreach (var item in items
                         .DistinctBy(row => row.Source))
            {
                await _datasetService.RemoveAsync(
                    item.Source,
                    item.RowVersion,
                    cancellationToken);
                removed++;
            }

            SelectedCount = 0;
            if (_pageIndex > 0 && TotalCount - removed <= _pageIndex * PageSize)
            {
                _pageIndex--;
            }

            await LoadCoreAsync(cancellationToken);
            StatusMessage = removed == 1
                ? "Das Learning-Beispiel wurde aus dem AI-Kontext entfernt."
                : $"{removed:N0} Learning-Beispiele wurden aus dem AI-Kontext entfernt.";
        }
        catch (Exception exception)
        {
            StatusMessage = FriendlyError(exception);
            HasError = true;
            await TryReloadAfterFailureAsync();
        }
        finally
        {
            IsBusy = false;
            _operationGate.Release();
        }
    }

    public async Task PreviousPageAsync(CancellationToken cancellationToken = default)
    {
        if (!CanGoPrevious)
        {
            return;
        }

        _pageIndex--;
        await LoadAsync(cancellationToken);
    }

    public async Task NextPageAsync(CancellationToken cancellationToken = default)
    {
        if (!CanGoNext)
        {
            return;
        }

        _pageIndex++;
        await LoadAsync(cancellationToken);
    }

    public void SetSelectedCount(int count)
    {
        SelectedCount = Math.Max(0, count);
    }

    partial void OnSearchTextChanged(string value)
    {
        if (_disposed)
        {
            return;
        }

        _pageIndex = 0;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        _ = ReloadAfterSearchDelayAsync(_searchCancellation.Token);
    }

    partial void OnSelectedCountChanged(int value) =>
        OnPropertyChanged(nameof(CanRemoveSelected));

    partial void OnTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(PageLabel));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanRemoveSelected));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
    }

    private async Task ReloadAfterSearchDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(180), cancellationToken);
            await LoadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected while the user continues typing.
        }
    }

    private async Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        var ruleOptions = await GetRuleOptionsAsync(cancellationToken);

        var page = await _datasetService.QueryAsync(
            new LearningDatasetQuery(
                _decision,
                _queryPolicySet,
                SearchText,
                SortColumn,
                SortDescending,
                checked(_pageIndex * PageSize),
                PageSize),
            cancellationToken);

        Items.Clear();
        var actionOptions = IsAllowedView ? AllowActions : ModerationActions;
        foreach (var row in page.Rows)
        {
            Items.Add(new LearningDatasetItem(row, ruleOptions, actionOptions));
        }

        TotalCount = page.TotalCount;
        SelectedCount = 0;
        StatusMessage = page.TotalCount == 0
            ? "Noch keine bestätigten Learning-Beispiele vorhanden."
            : $"{page.TotalCount:N0} bestätigte Learning-Beispiele verfügbar.";
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(PageLabel));
    }

    private async Task<IReadOnlyList<LearningRuleOption>> GetRuleOptionsAsync(
        CancellationToken cancellationToken)
    {
        var rules = await _configuration.LoadPolicyRulesAsync(cancellationToken);
        return rules
            .Where(rule => ModerationRuleCodes.IsSemanticAiRule(rule.RuleCode)
                && (_queryPolicySet is null
                    || string.Equals(
                        rule.PolicySet,
                        _queryPolicySet.Value.ToString(),
                        StringComparison.Ordinal)))
            .Select(rule => new LearningRuleOption(
                rule.RuleCode,
                ParsePolicySet(rule.PolicySet),
                rule.DisplayName))
            .DistinctBy(rule => rule.Code, StringComparer.OrdinalIgnoreCase)
            .OrderBy(rule => rule.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private async Task TryReloadAfterFailureAsync()
    {
        var error = StatusMessage;
        try
        {
            await LoadCoreAsync(CancellationToken.None);
        }
        catch
        {
            // Preserve the actionable error from the failed write.
        }

        StatusMessage = error;
    }

    private void EnsureConfigured()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_mode is null)
        {
            throw new InvalidOperationException(
                "Die Learning-Ansicht wurde noch nicht konfiguriert.");
        }
    }

    private static PolicySetCode ParsePolicySet(string value) =>
        Enum.TryParse<PolicySetCode>(value, ignoreCase: true, out var result)
            ? result
            : throw new InvalidOperationException(
                $"Unbekanntes Regelset: {value}");

    private static string FriendlyError(Exception exception) =>
        exception switch
        {
            ArgumentException => exception.Message,
            InvalidOperationException => exception.Message,
            _ => $"Learning-Daten konnten nicht gespeichert werden: {exception.Message}",
        };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _operationGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
