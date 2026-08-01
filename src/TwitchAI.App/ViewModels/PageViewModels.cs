using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using TwitchAI.App.Services;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;
using TwitchAI.Infrastructure;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Authentication;
using TwitchAI.Infrastructure.Persistence;
using TwitchAI.Infrastructure.Pipeline;
using TwitchAI.Infrastructure.Twitch;

namespace TwitchAI.App.ViewModels;

public sealed partial class ModerationQueueViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly ITwitchModerationExecutor _executor;
    private readonly IModerationReviewFeedbackService _feedbackService;
    private readonly IModerationCoordinator _coordinator;
    private readonly IConfigurationDataService _configuration;
    private readonly IPolicyStore _policyStore;
    private readonly ITwitchChatReplyExecutor _chatReplyExecutor;
    private readonly HashSet<ModerationCaseItem> _observedCases = [];

    public ModerationQueueViewModel(
        AppState state,
        ITwitchModerationExecutor executor,
        IModerationReviewFeedbackService feedbackService,
        IModerationCoordinator coordinator,
        IConfigurationDataService configuration,
        IPolicyStore policyStore,
        ITwitchChatReplyExecutor chatReplyExecutor)
    {
        _state = state;
        _executor = executor;
        _feedbackService = feedbackService;
        _coordinator = coordinator;
        _configuration = configuration;
        _policyStore = policyStore;
        _chatReplyExecutor = chatReplyExecutor;
        Cases = state.ModerationCases;
        Cases.CollectionChanged += OnCasesCollectionChanged;
        foreach (var item in Cases)
        {
            ObserveCase(item);
        }

        state.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AppState.SkippedWhitelistCount))
            {
                OnPropertyChanged(nameof(SkippedWhitelistCount));
            }
            else if (args.PropertyName == nameof(AppState.SkippedOverloadCount))
            {
                OnPropertyChanged(nameof(SkippedOverloadCount));
            }
        };
    }

    public ObservableCollection<ModerationCaseItem> Cases { get; }

    public bool HasCases => Cases.Count > 0;

    public int PendingCount => Cases.Count(item => item.Status == "Pending");

    public int SkippedWhitelistCount => _state.SkippedWhitelistCount;

    public int SkippedOverloadCount => _state.SkippedOverloadCount;

    private void OnCasesCollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args)
    {
        if (args.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in _observedCases.ToArray())
            {
                StopObservingCase(item);
            }

            foreach (var item in Cases)
            {
                ObserveCase(item);
            }
        }
        else
        {
            if (args.OldItems is not null)
            {
                foreach (var item in args.OldItems.OfType<ModerationCaseItem>())
                {
                    StopObservingCase(item);
                }
            }

            if (args.NewItems is not null)
            {
                foreach (var item in args.NewItems.OfType<ModerationCaseItem>())
                {
                    ObserveCase(item);
                }
            }
        }

        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasCases));
    }

    private void ObserveCase(ModerationCaseItem item)
    {
        if (_observedCases.Add(item))
        {
            item.PropertyChanged += OnCasePropertyChanged;
        }
    }

    private void StopObservingCase(ModerationCaseItem item)
    {
        if (_observedCases.Remove(item))
        {
            item.PropertyChanged -= OnCasePropertyChanged;
        }
    }

    private void OnCasePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ModerationCaseItem.Status))
        {
            OnPropertyChanged(nameof(PendingCount));
        }
    }

    [RelayCommand]
    private static void SelectCase(ModerationCaseItem item)
    {
        // Selection is owned by the view so the immutable CaseId is always passed onward.
    }

    public static ModerationExecutionRequest CreateExecutionRequest(ModerationCaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var action = item.Action switch
        {
            "delete" => ModerationActionKind.Delete,
            "timeout" => ModerationActionKind.Timeout,
            "ban" => ModerationActionKind.Ban,
            _ => throw new InvalidOperationException("Bitte eine gültige Moderationsaktion auswählen."),
        };
        int? timeoutSeconds = null;
        if (action == ModerationActionKind.Timeout)
        {
            if (double.IsNaN(item.TimeoutSeconds)
                || double.IsInfinity(item.TimeoutSeconds)
                || item.TimeoutSeconds != Math.Truncate(item.TimeoutSeconds)
                || item.TimeoutSeconds is < 1 or > 1_209_600)
            {
                throw new InvalidOperationException(
                    "Die Timeout-Dauer muss eine ganze Zahl zwischen 1 und 1.209.600 Sekunden sein.");
            }

            timeoutSeconds = checked((int)item.TimeoutSeconds);
        }

        var reason = action is ModerationActionKind.Timeout or ModerationActionKind.Ban
            ? item.Reason.Trim()
            : string.Empty;
        if (action is ModerationActionKind.Timeout or ModerationActionKind.Ban
            && reason.Length is < 1 or > 120)
        {
            throw new InvalidOperationException(
                "Die Begründung muss zwischen 1 und 120 Zeichen enthalten.");
        }

        var selectedRule = ResolveViolationRule(item);

        return new ModerationExecutionRequest(
            item.CaseId,
            item.RowVersion,
            action,
            timeoutSeconds,
            reason,
            selectedRule.PolicySet,
            item.TriggerPhrases
                .Where(phrase => phrase.IsSelected)
                .Select(phrase => phrase.Phrase)
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            selectedRule.RuleCode);
    }

    public async Task LoadViolationRulesAsync(
        IReadOnlyList<ModerationCaseItem>? targetCases = null,
        CancellationToken cancellationToken = default)
    {
        var items = (targetCases ?? Cases.ToArray())
            .Where(item => !item.IsChatReply && !item.HasViolationRuleSnapshot)
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        // Legacy/restored cases do not carry the live policy snapshot. Load
        // configuration once and resolve the effective policy once per
        // channel/category instead of performing N+1 serialized database
        // queries for every visible card.
        var definitions = await _configuration
            .LoadPolicyRulesAsync(cancellationToken);
        var groups = items.GroupBy(
            item => (item.ChannelId, item.CategoryId));
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effectivePolicy = await _policyStore.GetEffectivePolicyAsync(
                group.Key.ChannelId,
                group.Key.CategoryId,
                cancellationToken);
            var activeCodes = effectivePolicy.Rules
                .Select(rule => rule.RuleCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var options = definitions
                .Where(definition => activeCodes.Contains(definition.RuleCode))
                .Select(
                    definition => new ViolationCategoryOption(
                        definition.PolicySet,
                        definition.RuleCode,
                        definition.DisplayName,
                        definition.PublicReason,
                        definition.DefaultAction,
                        definition.DefaultTimeoutSeconds))
                .ToArray();

            foreach (var item in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var persistedRuleCode = item.RuleCode?.Trim();
                var selectedRuleCode = persistedRuleCode;
                selectedRuleCode ??= options.FirstOrDefault(
                        option => item.LocalMatchCodes.Contains(
                            option.Code,
                            StringComparer.OrdinalIgnoreCase))
                    ?.Code;
                var baseRuleCode = item.PolicySet == "Backseatgaming"
                    ? ModerationRuleCodes.Backseatgaming
                    : ModerationRuleCodes.OtherAbuse;
                selectedRuleCode ??= options.FirstOrDefault(
                        option => string.Equals(
                            option.Code,
                            baseRuleCode,
                            StringComparison.OrdinalIgnoreCase))
                    ?.Code;
                selectedRuleCode ??= options.FirstOrDefault(
                        option => string.Equals(
                            option.PolicySet,
                            item.PolicySet,
                            StringComparison.OrdinalIgnoreCase))
                    ?.Code;
                selectedRuleCode ??= options.FirstOrDefault()?.Code;
                var applyFallbackDefaults = !string.Equals(
                    selectedRuleCode,
                    persistedRuleCode,
                    StringComparison.OrdinalIgnoreCase);
                item.ReplaceViolationRuleOptions(
                    options,
                    selectedRuleCode,
                    applyFallbackDefaults);
                if (!item.HasViolationRuleSnapshot)
                {
                    item.AvailabilityLabel =
                        "Vorschlagskategorie ist nicht mehr aktiv";
                }
            }
        }
    }

    public async Task<ModerationExecutionResult> ExecuteAsync(
        ModerationCaseItem item,
        ModerationExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(request);
        EnsureRequestTargetsItem(item, request);

        if (item.IsBusy)
        {
            return Rejected("ui_busy", "Dieser Fall wird bereits verarbeitet.");
        }

        item.IsBusy = true;
        item.Status = "Identität und Verfügbarkeit werden geprüft";
        try
        {
            var result = await _executor.ExecuteAsync(request, cancellationToken);
            ApplyExecutionResult(item, result);
            if (result.Outcome == ModerationExecutionOutcome.StalePolicy)
            {
                try
                {
                    await _coordinator
                        .ReanalyzeCaseAsync(item.CaseId, cancellationToken);
                    item.Status =
                        "Regelstand veraltet · mit aktueller Revision neu eingereiht";
                    result = result with
                    {
                        StatusCode = "stale_policy_requeued",
                        Message =
                            "Die ausgewählte Verstoßkategorie war nicht mehr aktiv. "
                            + "Der Vorschlag wurde automatisch neu analysiert.",
                    };
                }
                catch (InvalidOperationException)
                {
                    item.Status =
                        "Regelstand veraltet · Analyse starten, um neu zu bewerten";
                }
            }
            else if (result.Outcome == ModerationExecutionOutcome.Succeeded)
            {
                RemoveResolvedCase(item);
            }

            return result;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    public async Task<ChatReplyExecutionResult> ExecuteChatReplyAsync(
        ModerationCaseItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsChatReply)
        {
            throw new InvalidOperationException(
                "Der ausgewählte Vorschlag ist keine Chat-Antwort.");
        }

        if (item.IsBusy)
        {
            return new ChatReplyExecutionResult(
                ChatReplyExecutionOutcome.Rejected,
                "ui_busy",
                "Dieser Vorschlag wird bereits verarbeitet.");
        }

        item.IsBusy = true;
        item.Status = "Chat-Antwort wird gesendet";
        try
        {
            var result = await _chatReplyExecutor
                .ExecuteAsync(item.CaseId, item.RowVersion, cancellationToken);
            if (result.RowVersion is { } rowVersion)
            {
                item.RowVersion = rowVersion;
            }

            switch (result.Outcome)
            {
                case ChatReplyExecutionOutcome.Succeeded:
                    RemoveResolvedCase(item);
                    break;
                case ChatReplyExecutionOutcome.Rejected:
                    item.Status = "Pending";
                    break;
                case ChatReplyExecutionOutcome.OutcomeUnknown:
                    item.Status = "Ergebnis unbekannt · nicht erneut ausführen";
                    break;
                default:
                    item.Status = "Fehlgeschlagen";
                    break;
            }

            return result;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    public async Task<ChatReplyExecutionResult> DiscardChatReplyAsync(
        ModerationCaseItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.IsChatReply)
        {
            throw new InvalidOperationException(
                "Der ausgewählte Vorschlag ist keine Chat-Antwort.");
        }

        if (item.IsBusy)
        {
            return new ChatReplyExecutionResult(
                ChatReplyExecutionOutcome.Rejected,
                "ui_busy",
                "Dieser Vorschlag wird bereits verarbeitet.");
        }

        item.IsBusy = true;
        try
        {
            var result = await _chatReplyExecutor
                .DiscardAsync(item.CaseId, item.RowVersion, cancellationToken);
            if (result.Outcome == ChatReplyExecutionOutcome.Succeeded)
            {
                RemoveResolvedCase(item);
            }
            else
            {
                item.Status = "Pending";
            }

            return result;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    public async Task<ModerationExecutionResult> CompleteUnavailableAsync(
        ModerationCaseItem item,
        ModerationExecutionRequest request,
        bool learn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(request);
        EnsureRequestTargetsItem(item, request);

        if (item.IsBusy)
        {
            return Rejected("ui_busy", "Dieser Fall wird bereits verarbeitet.");
        }

        item.IsBusy = true;
        try
        {
            var result = await _feedbackService
                .CompleteUnavailableAsync(request, learn, cancellationToken);
            if (result.Outcome == ModerationExecutionOutcome.AlreadyUnavailable)
            {
                item.Status = result.LearningAccepted
                    ? "Extern erledigt · als bestätigt gelernt"
                    : "Extern erledigt · ohne Lernen abgeschlossen";
                AddAudit(
                    item,
                    result.LearningAccepted
                        ? "Extern bestätigt"
                        : "Extern abgeschlossen",
                    "Keine Twitch-Aktion");
                RemoveResolvedCase(item);
            }
            else
            {
                ApplyExecutionResult(item, result);
            }

            return result;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    public async Task<ModerationExecutionResult> DiscardAsync(
        ModerationCaseItem item,
        string discardReason,
        bool learnAsAllowed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsBusy)
        {
            return Rejected("ui_busy", "Dieser Fall wird bereits verarbeitet.");
        }

        // Resolve every fallible UI value before changing the case status. A
        // missing ComboBox selection must not leave the card permanently in a
        // non-pending state and thereby disable both operator actions.
        var selectedRule = learnAsAllowed
            ? ResolveViolationRule(item)
            : ((PolicySetCode PolicySet, string RuleCode)?)null;
        var previousStatus = item.Status;
        item.IsBusy = true;
        item.Status = "Verwerfung wird gespeichert";
        try
        {
            var result = await _feedbackService
                .DiscardAsync(
                    item.CaseId,
                    item.RowVersion,
                    discardReason,
                    selectedRule?.PolicySet,
                    selectedRule?.RuleCode,
                    item.TriggerPhrases
                        .Where(phrase => phrase.IsSelected)
                        .Select(phrase => phrase.Phrase)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    learnAsAllowed,
                    cancellationToken);
            if (result.Outcome == ModerationExecutionOutcome.Succeeded)
            {
                item.RowVersion++;
                AddAudit(
                    item,
                    "Verworfen",
                    "Keine Twitch-Aktion",
                    includeActionDetails: false);
                RemoveResolvedCase(item);
            }
            else
            {
                ApplyExecutionResult(item, result);
            }

            return result;
        }
        catch
        {
            item.Status = previousStatus;
            throw;
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private static (PolicySetCode PolicySet, string RuleCode)
        ResolveViolationRule(ModerationCaseItem item)
    {
        var selected = item.FindViolationRule(item.RuleCode);
        if (selected is not null)
        {
            return (ParsePolicySet(selected.PolicySet), selected.Code);
        }

        throw new InvalidOperationException(
            "Bitte eine gültige Verstoßkategorie auswählen.");
    }

    private static PolicySetCode ParsePolicySet(string policySet)
    {
        return policySet switch
        {
            "General" => PolicySetCode.General,
            "Backseatgaming" => PolicySetCode.Backseatgaming,
            _ => throw new InvalidOperationException(
                "Bitte eine gültige Verstoßkategorie auswählen."),
        };
    }

    private void ApplyExecutionResult(
        ModerationCaseItem item,
        ModerationExecutionResult result)
    {
        switch (result.Outcome)
        {
            case ModerationExecutionOutcome.Succeeded:
                item.Status = result.TwitchExecuted
                    ? "Ausgeführt · Twitch bestätigt"
                    : result.LearningAccepted
                        ? "Read-Only · lokal gelernt"
                        : "Abgeschlossen";
                AddAudit(
                    item,
                    item.Action switch
                    {
                        "delete" => "Löschen",
                        "timeout" => "Timeout",
                        "ban" => "Ban",
                        _ => "Moderation",
                    },
                    result.TwitchExecuted
                        ? "Twitch ausgeführt"
                        : result.LearningAccepted
                            ? "Read-Only · lokal gelernt"
                            : "Keine Twitch-Aktion");
                break;
            case ModerationExecutionOutcome.AlreadyUnavailable:
                item.Status = "Extern erledigt · Lernentscheidung offen";
                item.AvailabilityLabel = "Nicht mehr verfügbar";
                break;
            case ModerationExecutionOutcome.IntegrityBlocked:
                item.Status = "Integritätsprüfung blockiert";
                break;
            case ModerationExecutionOutcome.StalePolicy:
                item.Status = "Regelstand veraltet · Neuanalyse erforderlich";
                break;
            case ModerationExecutionOutcome.OutcomeUnknown:
                item.Status = "Ergebnis unbekannt · nicht erneut ausführen";
                break;
            case ModerationExecutionOutcome.Failed:
                item.Status = "Twitch-Aktion fehlgeschlagen";
                break;
            case ModerationExecutionOutcome.Rejected:
                // A transient preflight problem (for example EventSub still
                // connecting) must leave the proposal actionable.
                item.Status = "Pending";
                break;
            default:
                item.Status = $"Nicht ausgeführt · {result.StatusCode}";
                break;
        }
    }

    private static void EnsureRequestTargetsItem(
        ModerationCaseItem item,
        ModerationExecutionRequest request)
    {
        if (request.CaseId != item.CaseId
            || request.ExpectedRowVersion != item.RowVersion)
        {
            throw new InvalidOperationException(
                "Der Freigabeauftrag gehört nicht zum angezeigten Moderationsfall.");
        }
    }

    private void AddAudit(
        ModerationCaseItem item,
        string decision,
        string twitchResult,
        bool includeActionDetails = true)
    {
        var auditItem = new AuditItem(
            item.CaseId,
            DateTimeOffset.UtcNow,
            item.UserName,
            item.OriginalMessage,
            decision,
            twitchResult,
            item.CategoryName,
            item.PolicySet,
            includeActionDetails && item.Action == "timeout"
                ? checked((int)item.TimeoutSeconds)
                : null,
            includeActionDetails && item.Action is "timeout" or "ban"
                ? item.Reason
                : string.Empty);
        try
        {
            _state.AuditItems.Insert(0, auditItem);
        }
        catch (COMException) when (_state.AuditItems.Contains(auditItem))
        {
            // WinUI can surface a late binding failure after the collection
            // mutation already succeeded. The persisted outcome remains valid.
        }
    }

    private void RemoveResolvedCase(ModerationCaseItem item)
    {
        try
        {
            Cases.Remove(item);
        }
        catch (COMException) when (!Cases.Contains(item))
        {
            // Do not turn an already persisted moderation outcome into a
            // misleading error if WinUI fails while projecting the removal.
        }
    }

    private static ModerationExecutionResult Rejected(
        string statusCode,
        string message)
    {
        return new ModerationExecutionResult(
            ModerationExecutionOutcome.Rejected,
            HumanApproved: false,
            TwitchExecuted: false,
            LearningAccepted: false,
            statusCode,
            message);
    }
}

public sealed partial class PoliciesViewModel : ObservableObject
{
    private const string GeneralSortScope = "policies.general";
    private const string BackseatgamingSortScope =
        "policies.backseatgaming";

    private static readonly HashSet<string> SortableColumns =
    [
        "PhraseId",
        "Phrase",
        "Code",
        "Override",
        "Feedback",
        "IsEnabled",
    ];

    private readonly IConfigurationDataService _configuration;
    private readonly ITableSortPreferenceStore _sortPreferences;

    public PoliciesViewModel(
        IConfigurationDataService configuration,
        ITableSortPreferenceStore sortPreferences)
    {
        _configuration = configuration;
        _sortPreferences = sortPreferences;
        Phrases = [];
        RuleOptions = [];
        CategoryFilters = [];
        RestoreSortPreference();
    }

    public ObservableCollection<PolicyPhraseItem> Phrases { get; }

    public ObservableCollection<PolicyRuleOptionItem> RuleOptions { get; }

    public ObservableCollection<PolicyCategoryFilterItem> CategoryFilters { get; }

    public IEnumerable<PolicyPhraseItem> SortedPhrases =>
        ApplySort(
            (SelectedCategoryFilter?.RuleId is { } ruleId
                ? Phrases.Where(item => item.RuleId == ruleId)
                : Phrases)
            .Where(MatchesSearch));

    public IReadOnlyList<string> OverrideOptions { get; } =
        ["Auto", "Aktiv", "Inaktiv"];

    public string SortColumn { get; private set; } = "Phrase";

    public bool SortDescending { get; private set; }

    [ObservableProperty]
    public partial string SelectedPolicySet { get; set; } = "General";

    [ObservableProperty]
    public partial string NewPhrase { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial PolicyPhraseItem? SelectedPhrase { get; set; }

    [ObservableProperty]
    public partial PolicyRuleOptionItem? SelectedRule { get; set; }

    [ObservableProperty]
    public partial PolicyCategoryFilterItem? SelectedCategoryFilter { get; set; }

    public bool HasStatus => StatusMessage.Length > 0;

    public bool HasBroadcaster => _configuration is not null;

    public bool CanDeleteSelected => SelectedPhrase is not null && !IsBusy;

    public bool CanAddPhrase => SelectedRule is not null && !IsBusy;

    public string BroadcasterStatus =>
        _configuration.BroadcasterId is { } broadcasterId
            ? $"Global für alle Kanäle · aktuell dokumentierter Kanal: {broadcasterId}"
            : "Global für alle Kanäle · aktuell ist kein Kanal verbunden.";

    public void SelectPolicySet(string policySet)
    {
        SelectedPolicySet = policySet switch
        {
            "General" => "General",
            "Backseatgaming" => "Backseatgaming",
            _ => throw new ArgumentOutOfRangeException(
                nameof(policySet),
                policySet,
                "Unbekanntes Regelset."),
        };
        SelectedPhrase = null;
        SelectedRule = null;
        RestoreSortPreference();
    }

    public void SortByColumn(string columnName)
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

        _sortPreferences.Save(
            GetSortScope(),
            SortColumn,
            SortDescending);
        OnPropertyChanged(nameof(SortedPhrases));
    }

    private void RestoreSortPreference()
    {
        var preference = _sortPreferences.GetOrDefault(
            GetSortScope(),
            defaultColumn: "Phrase",
            defaultDescending: false,
            validColumns: SortableColumns);
        SortColumn = preference.Column;
        SortDescending = preference.Descending;
        OnPropertyChanged(nameof(SortedPhrases));
    }

    private string GetSortScope() =>
        string.Equals(
            SelectedPolicySet,
            "Backseatgaming",
            StringComparison.Ordinal)
            ? BackseatgamingSortScope
            : GeneralSortScope;

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatus));
    }

    partial void OnSelectedPhraseChanged(PolicyPhraseItem? value) =>
        OnPropertyChanged(nameof(CanDeleteSelected));

    partial void OnSelectedRuleChanged(PolicyRuleOptionItem? value) =>
        OnPropertyChanged(nameof(CanAddPhrase));

    partial void OnSearchTextChanged(string value)
    {
        SelectedPhrase = null;
        OnPropertyChanged(nameof(SortedPhrases));
    }

    partial void OnSelectedCategoryFilterChanged(PolicyCategoryFilterItem? value)
    {
        SelectedPhrase = null;
        OnPropertyChanged(nameof(SortedPhrases));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanAddPhrase));
    }

    public async Task<ArchivedPhraseRestoreCandidate?> AddPhraseAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            var selectedRule = SelectedRule
                ?? throw new InvalidOperationException(
                    "Bitte zuerst eine aktive Verstoßkategorie auswählen.");
            await _configuration.AddPolicyPhraseAsync(
                SelectedPolicySet,
                selectedRule.RuleId,
                NewPhrase,
                cancellationToken);
            NewPhrase = string.Empty;
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Blacklist-Phrase atomar gespeichert; Regelrevision und Cache wurden aktualisiert.";
            return null;
        }
        catch (ArchivedPhraseExistsException exception)
        {
            StatusMessage = exception.Message;
            return exception.Candidate;
        }
        catch (Exception exception)
        {
            StatusMessage = FriendlyError(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Blacklist-Phrasen werden aus SQLite geladen …";
        try
        {
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                $"{Phrases.Count} {SelectedPolicySet}-Phrasen aus SQLite geladen. {BroadcasterStatus}";
        }
        catch (Exception exception)
        {
            Phrases.Clear();
            RuleOptions.Clear();
            SelectedRule = null;
            OnPropertyChanged(nameof(SortedPhrases));
            StatusMessage = FriendlyError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SetEnabledAsync(
        PolicyPhraseItem item,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.PersistedIsEnabled == isEnabled)
        {
            return;
        }

        if (IsBusy)
        {
            item.RestoreEnabled();
            return;
        }

        IsBusy = true;
        try
        {
            var rowVersion = await _configuration.SetPolicyPhraseEnabledAsync(
                item.PhraseId,
                item.RowVersion,
                isEnabled,
                cancellationToken);
            item.AcceptEnabled(isEnabled, rowVersion);
            OnPropertyChanged(nameof(SortedPhrases));
            StatusMessage = isEnabled
                ? $"„{item.Phrase}“ ist aktiv."
                : $"„{item.Phrase}“ ist deaktiviert.";
        }
        catch (DatabaseConcurrencyException)
        {
            item.RestoreEnabled();
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Die Zeile wurde zwischenzeitlich geändert und deshalb nicht überschrieben. Die SQLite-Daten wurden neu geladen.";
        }
        catch (Exception exception)
        {
            item.RestoreEnabled();
            OnPropertyChanged(nameof(SortedPhrases));
            StatusMessage = FriendlyError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UpdateEditableValuesAsync(
        PolicyPhraseItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.HasEditableChanges)
        {
            return;
        }

        if (IsBusy)
        {
            item.RestoreEditableValues();
            return;
        }

        IsBusy = true;
        try
        {
            var normalizedPhrase = item.Phrase;
            var displayOverride = item.Override;
            var rowVersion = await _configuration.UpdatePolicyPhraseAsync(
                item.PhraseId,
                item.RowVersion,
                item.PolicySet,
                normalizedPhrase,
                ToStorageOverride(displayOverride),
                cancellationToken);
            item.AcceptEditableValues(
                normalizedPhrase,
                displayOverride,
                rowVersion);
            await ReloadCoreAsync(cancellationToken);
            StatusMessage = $"„{normalizedPhrase}“ wurde gespeichert.";
        }
        catch (DatabaseConcurrencyException)
        {
            item.RestoreEditableValues();
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Die Zeile wurde zwischenzeitlich geändert und deshalb nicht überschrieben. Die SQLite-Daten wurden neu geladen.";
        }
        catch (Exception exception)
        {
            item.RestoreEditableValues();
            OnPropertyChanged(nameof(SortedPhrases));
            StatusMessage = FriendlyError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> ArchivePhraseAsync(
        PolicyPhraseItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsBusy
            || SelectedPhrase is null
            || SelectedPhrase.PhraseId != item.PhraseId
            || SelectedPhrase.RowVersion != item.RowVersion)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            await _configuration.ArchivePolicyPhraseAsync(
                item.PhraseId,
                item.RowVersion,
                item.PolicySet,
                cancellationToken);
            SelectedPhrase = null;
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                $"„{item.Phrase}“ wurde global archiviert und aus dem aktiven Regelset entfernt.";
            return true;
        }
        catch (DatabaseConcurrencyException)
        {
            SelectedPhrase = null;
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Die Zeile wurde zwischenzeitlich geändert. Es wurde nichts gelöscht und die SQLite-Daten wurden neu geladen.";
            return false;
        }
        catch (Exception exception)
        {
            StatusMessage = FriendlyError(exception);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> RestorePhraseAsync(
        ArchivedPhraseRestoreCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (IsBusy
            || candidate.PolicySet is null
            || candidate.RuleId is null)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            await _configuration.RestorePolicyPhraseAsync(
                candidate.Id,
                candidate.RowVersion,
                candidate.PolicySet,
                cancellationToken);
            NewPhrase = string.Empty;
            await ReloadCoreAsync(cancellationToken);
            SelectedPhrase = Phrases.FirstOrDefault(
                item => item.PhraseId == candidate.Id);
            StatusMessage =
                $"„{candidate.NormalizedPhrase}“ wurde mit ID {candidate.Id} und den bisherigen Lernzählern wiederhergestellt.";
            return true;
        }
        catch (DatabaseConcurrencyException)
        {
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Die archivierte Zeile wurde zwischenzeitlich geändert. Es wurde nichts wiederhergestellt; die SQLite-Daten wurden neu geladen.";
            return false;
        }
        catch (Exception exception)
        {
            StatusMessage = FriendlyError(exception);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadCoreAsync(CancellationToken cancellationToken = default)
    {
        if (!HasBroadcaster)
        {
            Phrases.Clear();
            RuleOptions.Clear();
            SelectedRule = null;
            OnPropertyChanged(nameof(SortedPhrases));
            return;
        }

        var selectedRuleId = SelectedRule?.RuleId;
        var rules = await _configuration.LoadPolicyRulesAsync(cancellationToken);
        RuleOptions.Clear();
        CategoryFilters.Clear();
        CategoryFilters.Add(new PolicyCategoryFilterItem(null, "Alle"));
        foreach (var rule in rules.Where(
                     row => string.Equals(
                         row.PolicySet,
                         SelectedPolicySet,
                         StringComparison.Ordinal)))
        {
            RuleOptions.Add(
                new PolicyRuleOptionItem(
                    rule.RuleId,
                    rule.PolicySet,
                    rule.RuleCode,
                    rule.DisplayName,
                    rule.PublicReason,
                    rule.DefaultAction,
                    rule.DefaultTimeoutSeconds));
            CategoryFilters.Add(
                new PolicyCategoryFilterItem(
                    rule.RuleId,
                    rule.DisplayName));
        }

        var previousFilterRuleId = SelectedCategoryFilter?.RuleId;
        SelectedCategoryFilter = CategoryFilters.FirstOrDefault(
                item => item.RuleId == previousFilterRuleId)
            ?? CategoryFilters[0];
        SelectedRule = RuleOptions.FirstOrDefault(
                item => item.RuleId == selectedRuleId)
            ?? RuleOptions.FirstOrDefault();

        var rows = await _configuration.LoadPolicyPhrasesAsync(cancellationToken);
        Phrases.Clear();
        foreach (var row in rows.Where(
                     row => string.Equals(
                         row.PolicySet,
                         SelectedPolicySet,
                         StringComparison.Ordinal)))
        {
            var phraseItem = new PolicyPhraseItem(
                    row.PhraseId,
                    row.RuleId,
                    row.RowVersion,
                    row.PolicySet,
                    row.RuleCode,
                    row.RuleTitle,
                    row.NormalizedPhrase,
                    ToDisplayOverride(row.ManualOverride),
                    row.IsEnabled,
                    row.PositiveConfirmations,
                    row.NegativeConfirmations);
            phraseItem.ReplaceRuleOptions(RuleOptions);
            Phrases.Add(phraseItem);
        }

        OnPropertyChanged(nameof(SortedPhrases));
    }

public sealed record PolicyCategoryFilterItem(
    long? RuleId,
    string DisplayName);

    private IEnumerable<PolicyPhraseItem> ApplySort(
        IEnumerable<PolicyPhraseItem> source)
    {
        var textComparer = StringComparer.CurrentCultureIgnoreCase;
        return SortColumn switch
        {
            "PhraseId" => SortDescending
                ? source.OrderByDescending(item => item.PhraseId)
                : source.OrderBy(item => item.PhraseId),
            "Phrase" => SortDescending
                ? source.OrderByDescending(item => item.Phrase, textComparer)
                : source.OrderBy(item => item.Phrase, textComparer),
            "Code" => SortDescending
                ? source.OrderByDescending(item => item.Code, textComparer)
                : source.OrderBy(item => item.Code, textComparer),
            "Override" => SortDescending
                ? source.OrderByDescending(item => item.Override, textComparer)
                : source.OrderBy(item => item.Override, textComparer),
            "Feedback" => SortDescending
                ? source
                    .OrderByDescending(item => item.PositiveCount)
                    .ThenByDescending(item => item.NegativeCount)
                : source
                    .OrderBy(item => item.PositiveCount)
                    .ThenBy(item => item.NegativeCount),
            "IsEnabled" => SortDescending
                ? source.OrderByDescending(item => item.IsEnabled)
                : source.OrderBy(item => item.IsEnabled),
            _ => source.OrderBy(item => item.Phrase, textComparer),
        };
    }

    private bool MatchesSearch(PolicyPhraseItem item)
    {
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        var enabledLabel = item.IsEnabled ? "aktiv" : "inaktiv";
        return item.PhraseId.ToString(CultureInfo.InvariantCulture).Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || item.Phrase.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.RuleTitle.Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || item.Code.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Override.Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || item.FeedbackLabel.Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || enabledLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string ToDisplayOverride(string value)
    {
        return value switch
        {
            "auto" => "Auto",
            "active" => "Aktiv",
            "inactive" => "Inaktiv",
            _ => value,
        };
    }

    private static string ToStorageOverride(string value)
    {
        return value switch
        {
            "Auto" => "auto",
            "Aktiv" => "active",
            "Inaktiv" => "inactive",
            _ => throw new ArgumentException(
                "Der Override muss Auto, Aktiv oder Inaktiv sein.",
                nameof(value)),
        };
    }

    private static string FriendlyError(Exception exception)
    {
        return exception switch
        {
            ArgumentException => exception.Message,
            InvalidOperationException => exception.Message,
            _ => $"SQLite-Konfiguration fehlgeschlagen: {exception.Message}",
        };
    }
}

public sealed partial class WhitelistViewModel : ObservableObject
{
    private const string SortScope = "whitelist";

    private static readonly HashSet<string> SortableColumns =
    [
        "AllowlistId",
        "OriginalPhrase",
        "IsUser",
        "HitCount",
        "IsEnabled",
    ];

    private readonly IConfigurationDataService _configuration;
    private readonly ITableSortPreferenceStore _sortPreferences;

    public WhitelistViewModel(
        AppState state,
        IConfigurationDataService configuration,
        ITableSortPreferenceStore sortPreferences)
    {
        _configuration = configuration;
        _sortPreferences = sortPreferences;
        Phrases = state.WhitelistPhrases;
        var preference = _sortPreferences.GetOrDefault(
            SortScope,
            defaultColumn: "OriginalPhrase",
            defaultDescending: false,
            validColumns: SortableColumns);
        SortColumn = preference.Column;
        SortDescending = preference.Descending;
    }

    public ObservableCollection<WhitelistPhraseItem> Phrases { get; }

    public IEnumerable<WhitelistPhraseItem> SortedPhrases =>
        ApplySort(Phrases.Where(MatchesSearch));

    public string SortColumn { get; private set; } = "OriginalPhrase";

    public bool SortDescending { get; private set; }

    [ObservableProperty]
    public partial string NewPhrase { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial WhitelistPhraseItem? SelectedPhrase { get; set; }

    public bool HasStatus => StatusMessage.Length > 0;

    public bool HasBroadcaster => _configuration is not null;

    public bool CanAddPhrase => HasBroadcaster && !IsBusy;

    public bool CanDeleteSelected => SelectedPhrase is not null && !IsBusy;

    public string BroadcasterStatus =>
        _configuration.BroadcasterId is { } broadcasterId
            ? $"Global für alle Kanäle · aktuell dokumentierter Kanal: {broadcasterId}"
            : "Global für alle Kanäle · aktuell ist kein Kanal verbunden.";

    partial void OnStatusMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasStatus));
    }

    partial void OnSelectedPhraseChanged(WhitelistPhraseItem? value) =>
        OnPropertyChanged(nameof(CanDeleteSelected));

    partial void OnSearchTextChanged(string value)
    {
        SelectedPhrase = null;
        OnPropertyChanged(nameof(SortedPhrases));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAddPhrase));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    public void SortByColumn(string columnName)
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

        _sortPreferences.Save(
            SortScope,
            SortColumn,
            SortDescending);
        OnPropertyChanged(nameof(SortedPhrases));
    }

    public async Task<ArchivedPhraseRestoreCandidate?> AddPhraseAsync(
        CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return null;
        }

        IsBusy = true;
        try
        {
            await _configuration.AddWhitelistPhraseAsync(
                NewPhrase,
                cancellationToken);
            NewPhrase = string.Empty;
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Whitelist-Phrase atomar gespeichert und der aktive Filtercache aktualisiert.";
            return null;
        }
        catch (ArchivedPhraseExistsException exception)
        {
            StatusMessage = exception.Message;
            return exception.Candidate;
        }
        catch (Exception exception)
        {
            StatusMessage = FriendlyError(exception);
            return null;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Whitelist wird aus SQLite geladen …";
        try
        {
            await ReloadCoreAsync(cancellationToken);
            StatusMessage = $"{Phrases.Count} Whitelist-Phrasen geladen. {BroadcasterStatus}";
        }
        catch (Exception exception)
        {
            Phrases.Clear();
            OnPropertyChanged(nameof(SortedPhrases));
            StatusMessage = FriendlyError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SetEnabledAsync(
        WhitelistPhraseItem item,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.PersistedIsEnabled == isEnabled)
        {
            return;
        }

        if (IsBusy)
        {
            item.RestoreEnabled();
            return;
        }

        IsBusy = true;
        try
        {
            var rowVersion = await _configuration.SetWhitelistEnabledAsync(
                item.AllowlistId,
                item.RowVersion,
                isEnabled,
                cancellationToken);
            item.AcceptEnabled(isEnabled, rowVersion);
            OnPropertyChanged(nameof(SortedPhrases));
            StatusMessage = isEnabled
                ? $"„{item.NormalizedPhrase}“ ist aktiv."
                : $"„{item.NormalizedPhrase}“ ist deaktiviert.";
        }
        catch (DatabaseConcurrencyException)
        {
            item.RestoreEnabled();
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Die Zeile wurde zwischenzeitlich geändert und deshalb nicht überschrieben. Die SQLite-Daten wurden neu geladen.";
        }
        catch (Exception exception)
        {
            item.RestoreEnabled();
            OnPropertyChanged(nameof(SortedPhrases));
            StatusMessage = FriendlyError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UpdateEditableValueAsync(
        WhitelistPhraseItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.HasEditableChanges)
        {
            return;
        }

        if (IsBusy)
        {
            item.RestoreEditableValue();
            return;
        }

        IsBusy = true;
        try
        {
            var lowerPhrase = item.OriginalPhrase.ToLowerInvariant();
            var rowVersion = await _configuration.UpdateWhitelistPhraseAsync(
                item.AllowlistId,
                item.RowVersion,
                item.OriginalPhrase,
                item.IsUser,
                cancellationToken);
            item.AcceptEditableValue(
                item.OriginalPhrase,
                lowerPhrase,
                rowVersion);
            await ReloadCoreAsync(cancellationToken);
            StatusMessage = $"„{lowerPhrase}“ wurde gespeichert.";
        }
        catch (DatabaseConcurrencyException)
        {
            item.RestoreEditableValue();
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Die Zeile wurde zwischenzeitlich geändert und deshalb nicht überschrieben. Die SQLite-Daten wurden neu geladen.";
        }
        catch (Exception exception)
        {
            item.RestoreEditableValue();
            OnPropertyChanged(nameof(SortedPhrases));
            StatusMessage = FriendlyError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SetUserAsync(
        WhitelistPhraseItem item,
        bool isUser,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.PersistedIsUser == isUser)
        {
            return;
        }

        if (IsBusy)
        {
            item.RestoreUser();
            return;
        }

        IsBusy = true;
        try
        {
            var rowVersion = await _configuration.UpdateWhitelistPhraseAsync(
                item.AllowlistId,
                item.RowVersion,
                item.OriginalPhrase,
                isUser,
                cancellationToken);
            item.AcceptUser(isUser, rowVersion);
            OnPropertyChanged(nameof(SortedPhrases));
            StatusMessage = isUser
                ? $"„{item.OriginalPhrase}“ wird als User-Login behandelt."
                : $"„{item.OriginalPhrase}“ wird als exakte Nachricht behandelt.";
        }
        catch (DatabaseConcurrencyException)
        {
            item.RestoreUser();
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Die Zeile wurde zwischenzeitlich geändert und deshalb nicht überschrieben. Die SQLite-Daten wurden neu geladen.";
        }
        catch (Exception exception)
        {
            item.RestoreUser();
            OnPropertyChanged(nameof(SortedPhrases));
            StatusMessage = FriendlyError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> ArchivePhraseAsync(
        WhitelistPhraseItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsBusy
            || SelectedPhrase is null
            || SelectedPhrase.AllowlistId != item.AllowlistId
            || SelectedPhrase.RowVersion != item.RowVersion)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            await _configuration.ArchiveWhitelistPhraseAsync(
                item.AllowlistId,
                item.RowVersion,
                cancellationToken);
            SelectedPhrase = null;
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                $"„{item.NormalizedPhrase}“ wurde global archiviert und aus der aktiven Whitelist entfernt.";
            return true;
        }
        catch (DatabaseConcurrencyException)
        {
            SelectedPhrase = null;
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Die Zeile wurde zwischenzeitlich geändert. Es wurde nichts gelöscht und die SQLite-Daten wurden neu geladen.";
            return false;
        }
        catch (Exception exception)
        {
            StatusMessage = FriendlyError(exception);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> RestorePhraseAsync(
        ArchivedPhraseRestoreCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (IsBusy || candidate.PolicySet is not null)
        {
            return false;
        }

        IsBusy = true;
        try
        {
            await _configuration.RestoreWhitelistPhraseAsync(
                candidate.Id,
                candidate.RowVersion,
                cancellationToken);
            NewPhrase = string.Empty;
            await ReloadCoreAsync(cancellationToken);
            SelectedPhrase = Phrases.FirstOrDefault(
                item => item.AllowlistId == candidate.Id);
            StatusMessage =
                $"„{candidate.NormalizedPhrase}“ wurde mit ID {candidate.Id} und dem bisherigen Trefferzähler wiederhergestellt.";
            return true;
        }
        catch (DatabaseConcurrencyException)
        {
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Die archivierte Zeile wurde zwischenzeitlich geändert. Es wurde nichts wiederhergestellt; die SQLite-Daten wurden neu geladen.";
            return false;
        }
        catch (Exception exception)
        {
            StatusMessage = FriendlyError(exception);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadCoreAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _configuration.LoadWhitelistAsync(cancellationToken);
        Phrases.Clear();
        foreach (var row in rows)
        {
            Phrases.Add(
                new WhitelistPhraseItem(
                    row.AllowlistId,
                    row.RowVersion,
                    row.BroadcasterId,
                    row.OriginalPhrase,
                    row.LowerPhrase,
                    "Global",
                    row.IsUser,
                    row.IsEnabled,
                    row.HitCount,
                    row.LastHitAtUtc));
        }

        OnPropertyChanged(nameof(SortedPhrases));
    }

    private IEnumerable<WhitelistPhraseItem> ApplySort(
        IEnumerable<WhitelistPhraseItem> source)
    {
        var textComparer = StringComparer.CurrentCultureIgnoreCase;
        return SortColumn switch
        {
            "AllowlistId" => SortDescending
                ? source.OrderByDescending(item => item.AllowlistId)
                : source.OrderBy(item => item.AllowlistId),
            "OriginalPhrase" => SortDescending
                ? source.OrderByDescending(
                    item => item.OriginalPhrase,
                    textComparer)
                : source.OrderBy(item => item.OriginalPhrase, textComparer),
            "IsUser" => SortDescending
                ? source.OrderByDescending(item => item.IsUser)
                : source.OrderBy(item => item.IsUser),
            "HitCount" => SortDescending
                ? source.OrderByDescending(item => item.HitCount)
                : source.OrderBy(item => item.HitCount),
            "IsEnabled" => SortDescending
                ? source.OrderByDescending(item => item.IsEnabled)
                : source.OrderBy(item => item.IsEnabled),
            _ => source.OrderBy(item => item.OriginalPhrase, textComparer),
        };
    }

    private bool MatchesSearch(WhitelistPhraseItem item)
    {
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        var enabledLabel = item.IsEnabled ? "aktiv" : "inaktiv";
        var subjectLabel = item.IsUser
            ? "user benutzer login"
            : "nachricht phrase";
        return item.AllowlistId.ToString(CultureInfo.InvariantCulture).Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || item.OriginalPhrase.Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || item.HitCount.ToString(CultureInfo.InvariantCulture).Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || subjectLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            || enabledLabel.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string FriendlyError(Exception exception)
    {
        return exception switch
        {
            ArgumentException => exception.Message,
            InvalidOperationException => exception.Message,
            _ => $"SQLite-Konfiguration fehlgeschlagen: {exception.Message}",
        };
    }
}

public sealed partial class ChatReplyTemplateItem : ObservableObject
{
    public ChatReplyTemplateItem(ChatReplyTemplateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        TemplateId = record.TemplateId;
        RowVersion = record.RowVersion;
        ChatMessage = record.ExampleMessage;
        PersistedChatMessage = record.ExampleMessage;
        ResponseMessage = record.ResponseMessage;
        PersistedResponseMessage = record.ResponseMessage;
        IsEnabled = record.IsEnabled;
        PersistedIsEnabled = record.IsEnabled;
        HitCount = record.HitCount;
    }

    public long TemplateId { get; }

    public long RowVersion { get; private set; }

    [ObservableProperty]
    public partial string ChatMessage { get; set; }

    public string PersistedChatMessage { get; private set; }

    [ObservableProperty]
    public partial string ResponseMessage { get; set; }

    public string PersistedResponseMessage { get; private set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public bool PersistedIsEnabled { get; private set; }

    public int HitCount { get; }

    public bool HasTextChanges =>
        !string.Equals(
            ChatMessage,
            PersistedChatMessage,
            StringComparison.Ordinal)
        || !string.Equals(
            ResponseMessage,
            PersistedResponseMessage,
            StringComparison.Ordinal);

    public void AcceptTexts(long rowVersion)
    {
        RowVersion = rowVersion;
        PersistedChatMessage = ChatMessage;
        PersistedResponseMessage = ResponseMessage;
    }

    public void RestoreTexts()
    {
        ChatMessage = PersistedChatMessage;
        ResponseMessage = PersistedResponseMessage;
    }

    public void AcceptEnabled(bool isEnabled, long rowVersion)
    {
        RowVersion = rowVersion;
        PersistedIsEnabled = isEnabled;
        IsEnabled = isEnabled;
    }

    public void RestoreEnabled() => IsEnabled = PersistedIsEnabled;
}

public sealed partial class ChatViewModel : ObservableObject
{
    private const string SortScope = "chat";

    private static readonly HashSet<string> SortableColumns =
    [
        "TemplateId",
        "ChatMessage",
        "ResponseMessage",
        "HitCount",
        "IsEnabled",
    ];

    private readonly IChatReplyStore _chatReplyStore;
    private readonly ITableSortPreferenceStore _sortPreferences;

    public ChatViewModel(
        IChatReplyStore chatReplyStore,
        ITableSortPreferenceStore sortPreferences)
    {
        _chatReplyStore = chatReplyStore;
        _sortPreferences = sortPreferences;
        var preference = _sortPreferences.GetOrDefault(
            SortScope,
            defaultColumn: "TemplateId",
            defaultDescending: true,
            validColumns: SortableColumns);
        SortColumn = preference.Column;
        SortDescending = preference.Descending;
    }

    public ObservableCollection<ChatReplyTemplateItem> Templates { get; } = [];

    public IEnumerable<ChatReplyTemplateItem> SortedTemplates =>
        ApplySort(Templates.Where(MatchesSearch));

    public string SortColumn { get; private set; }

    public bool SortDescending { get; private set; }

    [ObservableProperty]
    public partial string NewChatMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewResponseMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    public bool CanAdd =>
        !IsBusy
        && !string.IsNullOrWhiteSpace(NewChatMessage)
        && !string.IsNullOrWhiteSpace(NewResponseMessage);

    public bool CanDeleteSelected => !IsBusy && SelectedCount > 0;

    partial void OnNewChatMessageChanged(string value) =>
        OnPropertyChanged(nameof(CanAdd));

    partial void OnNewResponseMessageChanged(string value) =>
        OnPropertyChanged(nameof(CanAdd));

    partial void OnSearchTextChanged(string value)
    {
        SelectedCount = 0;
        OnPropertyChanged(nameof(SortedTemplates));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    partial void OnSelectedCountChanged(int value) =>
        OnPropertyChanged(nameof(CanDeleteSelected));

    public void SortByColumn(string columnName)
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

        _sortPreferences.Save(SortScope, SortColumn, SortDescending);
        OnPropertyChanged(nameof(SortedTemplates));
    }

    public async Task AddTemplateAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanAdd)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _chatReplyStore.AddTemplateAsync(
                NewChatMessage,
                NewResponseMessage,
                cancellationToken);
            NewChatMessage = string.Empty;
            NewResponseMessage = string.Empty;
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Chat-Vorlage gespeichert. Ähnliche Nachrichten können jetzt einen Antwortvorschlag erzeugen.";
        }
        catch (Exception exception)
        {
            StatusMessage = FriendlyError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusMessage = "Chat-Vorlagen werden aus SQLite geladen …";
        try
        {
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                $"{Templates.Count:N0} Chat-Vorlage(n) geladen.";
        }
        catch (Exception exception)
        {
            Templates.Clear();
            OnPropertyChanged(nameof(SortedTemplates));
            StatusMessage = FriendlyError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task UpdateTemplateAsync(
        ChatReplyTemplateItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.HasTextChanges)
        {
            return;
        }

        if (IsBusy)
        {
            item.RestoreTexts();
            return;
        }

        IsBusy = true;
        try
        {
            var rowVersion = await _chatReplyStore.UpdateTemplateAsync(
                item.TemplateId,
                item.RowVersion,
                item.ChatMessage,
                item.ResponseMessage,
                cancellationToken);
            item.AcceptTexts(rowVersion);
            OnPropertyChanged(nameof(SortedTemplates));
            StatusMessage =
                $"Chat-Vorlage {item.TemplateId:N0} wurde automatisch gespeichert.";
        }
        catch (Exception exception)
        {
            item.RestoreTexts();
            await ReloadCoreAsync(cancellationToken);
            StatusMessage = FriendlyError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task SetEnabledAsync(
        ChatReplyTemplateItem item,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.PersistedIsEnabled == isEnabled)
        {
            return;
        }

        if (IsBusy)
        {
            item.RestoreEnabled();
            return;
        }

        IsBusy = true;
        try
        {
            var rowVersion = await _chatReplyStore.SetTemplateEnabledAsync(
                item.TemplateId,
                item.RowVersion,
                isEnabled,
                cancellationToken);
            item.AcceptEnabled(isEnabled, rowVersion);
            OnPropertyChanged(nameof(SortedTemplates));
            StatusMessage = isEnabled
                ? $"Chat-Vorlage {item.TemplateId:N0} ist aktiv."
                : $"Chat-Vorlage {item.TemplateId:N0} ist deaktiviert.";
        }
        catch (Exception exception)
        {
            item.RestoreEnabled();
            await ReloadCoreAsync(cancellationToken);
            StatusMessage = FriendlyError(exception);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadCoreAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _chatReplyStore.LoadTemplatesAsync(cancellationToken);
        Templates.Clear();
        foreach (var row in rows)
        {
            Templates.Add(new ChatReplyTemplateItem(row));
        }

        SelectedCount = 0;
        OnPropertyChanged(nameof(SortedTemplates));
    }

    private IEnumerable<ChatReplyTemplateItem> ApplySort(
        IEnumerable<ChatReplyTemplateItem> source)
    {
        var textComparer = StringComparer.CurrentCultureIgnoreCase;
        return SortColumn switch
        {
            "TemplateId" => SortDescending
                ? source.OrderByDescending(item => item.TemplateId)
                : source.OrderBy(item => item.TemplateId),
            "ChatMessage" => SortDescending
                ? source.OrderByDescending(item => item.ChatMessage, textComparer)
                : source.OrderBy(item => item.ChatMessage, textComparer),
            "ResponseMessage" => SortDescending
                ? source.OrderByDescending(item => item.ResponseMessage, textComparer)
                : source.OrderBy(item => item.ResponseMessage, textComparer),
            "HitCount" => SortDescending
                ? source.OrderByDescending(item => item.HitCount)
                : source.OrderBy(item => item.HitCount),
            "IsEnabled" => SortDescending
                ? source.OrderByDescending(item => item.IsEnabled)
                : source.OrderBy(item => item.IsEnabled),
            _ => source.OrderByDescending(item => item.TemplateId),
        };
    }

    private bool MatchesSearch(ChatReplyTemplateItem item)
    {
        var query = SearchText.Trim();
        if (query.Length == 0)
        {
            return true;
        }

        var state = item.IsEnabled ? "aktiv" : "inaktiv";
        return item.TemplateId.ToString(CultureInfo.InvariantCulture).Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || item.ChatMessage.Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || item.ResponseMessage.Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || item.HitCount.ToString(CultureInfo.InvariantCulture).Contains(
                query,
                StringComparison.OrdinalIgnoreCase)
            || state.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string FriendlyError(Exception exception) =>
        exception switch
        {
            ArgumentException => exception.Message,
            InvalidOperationException => exception.Message,
            _ => $"Chat-Konfiguration fehlgeschlagen: {exception.Message}",
        };
}

public sealed partial class AuditViewModel : ObservableObject
{
    private const string SortScope = "audit";

    private static readonly HashSet<string> SortableColumns =
    [
        "Id",
        "OccurredAt",
        "UserName",
        "OriginalMessage",
        "DecisionDetail",
        "Reason",
        "TwitchResult",
        "CategoryName",
        "PolicySet",
    ];

    private readonly ITableSortPreferenceStore _sortPreferences;

    public AuditViewModel(
        AppState state,
        ITableSortPreferenceStore sortPreferences)
    {
        _sortPreferences = sortPreferences;
        Items = state.AuditItems;
        Items.CollectionChanged += (_, _) => NotifyFilteredState();
        var preference = _sortPreferences.GetOrDefault(
            SortScope,
            defaultColumn: "OccurredAt",
            defaultDescending: true,
            validColumns: SortableColumns);
        SortColumn = preference.Column;
        SortDescending = preference.Descending;
    }

    public ObservableCollection<AuditItem> Items { get; }

    public IEnumerable<AuditItem> FilteredItems =>
        ApplySort(
            Items.Where(item =>
                MatchesText(item)
                && MatchesResult(item)
                && (PolicyFilter == "Alle" || item.PolicySet == PolicyFilter)));

    public bool CanExport => FilteredItems.Any();

    public Visibility EmptyVisibility =>
        CanExport
            ? Visibility.Collapsed
            : Visibility.Visible;

    public string SortColumn { get; private set; } = "OccurredAt";

    public bool SortDescending { get; private set; } = true;

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultFilter { get; set; } = "Alle";

    [ObservableProperty]
    public partial string PolicyFilter { get; set; } = "Alle";

    partial void OnSearchTextChanged(string value) => NotifyFilteredState();

    partial void OnResultFilterChanged(string value) => NotifyFilteredState();

    partial void OnPolicyFilterChanged(string value) => NotifyFilteredState();

    public void SortByColumn(string columnName)
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

        _sortPreferences.Save(
            SortScope,
            SortColumn,
            SortDescending);
        NotifyFilteredState();
    }

    private void NotifyFilteredState()
    {
        OnPropertyChanged(nameof(FilteredItems));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    private IEnumerable<AuditItem> ApplySort(IEnumerable<AuditItem> source)
    {
        var textComparer = StringComparer.CurrentCultureIgnoreCase;
        return SortColumn switch
        {
            "Id" => SortDescending
                ? source.OrderByDescending(item => item.Id)
                : source.OrderBy(item => item.Id),
            "OccurredAt" => SortDescending
                ? source.OrderByDescending(item => item.OccurredAt)
                : source.OrderBy(item => item.OccurredAt),
            "UserName" => SortDescending
                ? source.OrderByDescending(item => item.UserName, textComparer)
                : source.OrderBy(item => item.UserName, textComparer),
            "OriginalMessage" => SortDescending
                ? source.OrderByDescending(item => item.OriginalMessage, textComparer)
                : source.OrderBy(item => item.OriginalMessage, textComparer),
            "DecisionDetail" => SortDescending
                ? source.OrderByDescending(item => item.DecisionDetail, textComparer)
                : source.OrderBy(item => item.DecisionDetail, textComparer),
            "Reason" => SortDescending
                ? source.OrderByDescending(item => item.Reason, textComparer)
                : source.OrderBy(item => item.Reason, textComparer),
            "TwitchResult" => SortDescending
                ? source.OrderByDescending(item => item.TwitchResult, textComparer)
                : source.OrderBy(item => item.TwitchResult, textComparer),
            "CategoryName" => SortDescending
                ? source.OrderByDescending(item => item.CategoryName, textComparer)
                : source.OrderBy(item => item.CategoryName, textComparer),
            "PolicySet" => SortDescending
                ? source.OrderByDescending(item => item.PolicySet, textComparer)
                : source.OrderBy(item => item.PolicySet, textComparer),
            _ => source.OrderByDescending(item => item.OccurredAt),
        };
    }

    private bool MatchesText(AuditItem item)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return item.UserName.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.OriginalMessage.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.Decision.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.TwitchResult.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || item.Reason.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesResult(AuditItem item)
    {
        return ResultFilter switch
        {
            "Alle" => true,
            "Ausgeführt" => item.TwitchResult.StartsWith(
                "Twitch ausgeführt",
                StringComparison.OrdinalIgnoreCase),
            "Verworfen" => item.Decision.Equals(
                "Verworfen",
                StringComparison.OrdinalIgnoreCase),
            "Extern erledigt" => item.Decision.StartsWith(
                "Extern",
                StringComparison.OrdinalIgnoreCase),
            "Unbekannt" =>
                item.Decision.Contains("unbekannt", StringComparison.OrdinalIgnoreCase) ||
                item.TwitchResult.Contains("unbekannt", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}

public enum TwitchSessionRestoreOutcome
{
    Connected,
    NoStoredAuthorization,
    ReauthorizationRequired,
    TemporarilyUnavailable,
    ConfigurationRequired,
    NotConfigured,
}

public sealed record TwitchSessionRestoreResult(
    TwitchSessionRestoreOutcome Outcome,
    string Message)
{
    public bool IsConnected => Outcome == TwitchSessionRestoreOutcome.Connected;

    public bool MayStartDeviceAuthorization =>
        Outcome is TwitchSessionRestoreOutcome.NoStoredAuthorization
            or TwitchSessionRestoreOutcome.ReauthorizationRequired;
}

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppState _state;
    private readonly InfrastructureOptions _options;
    private readonly ITwitchOAuthClient _oauthClient;
    private readonly ITwitchAccessTokenProvider _tokenProvider;
    private readonly ITwitchHelixClient _helixClient;
    private readonly ITwitchConnectionReadiness _twitchConnectionReadiness;
    private readonly ILmStudioClient _lmStudioClient;
    private readonly ILmStudioCredentialVault _lmStudioCredentialVault;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly IPolicyStore _policyStore;
    private readonly ILocalAppStorage _localStorage;
    private readonly ModerationQueueRuntimeSettings _queueRuntimeSettings;
    private readonly TwitchRuntimeModeSettings _twitchRuntimeModeSettings;
    private readonly IModerationCoordinator _coordinator;
    private readonly SemaphoreSlim _twitchSessionGate = new(1, 1);

    public SettingsViewModel(
        AppState state,
        InfrastructureOptions options,
        ITwitchOAuthClient oauthClient,
        ITwitchAccessTokenProvider tokenProvider,
        ITwitchHelixClient helixClient,
        ITwitchConnectionReadiness twitchConnectionReadiness,
        ILmStudioClient lmStudioClient,
        ILmStudioCredentialVault lmStudioCredentialVault,
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator writeCoordinator,
        IPolicyStore policyStore,
        ILocalAppStorage localStorage,
        ModerationQueueRuntimeSettings queueRuntimeSettings,
        TwitchRuntimeModeSettings twitchRuntimeModeSettings,
        IModerationCoordinator coordinator)
    {
        _state = state;
        _options = options;
        _oauthClient = oauthClient;
        _tokenProvider = tokenProvider;
        _helixClient = helixClient;
        _twitchConnectionReadiness = twitchConnectionReadiness;
        _lmStudioClient = lmStudioClient;
        _lmStudioCredentialVault = lmStudioCredentialVault;
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator;
        _policyStore = policyStore;
        _localStorage = localStorage;
        _queueRuntimeSettings = queueRuntimeSettings;
        _twitchRuntimeModeSettings = twitchRuntimeModeSettings;
        _coordinator = coordinator;
        TwitchClientId = _localStorage.GetString("TwitchClientId")
            ?? options.TwitchOAuth.ClientId;
        ChannelLogin = _localStorage.GetString("ChannelLogin") ?? string.Empty;
        LmStudioBaseUrl = _localStorage.GetString("LmStudioBaseUrl")
            ?? options.LmStudio.BaseAddress.ToString().TrimEnd('/');
        LmStudioModel = _localStorage.GetString("LmStudioModel")
            ?? options.LmStudio.Model;
        SelectedTheme = _localStorage.GetString("Theme") ?? "System";
        _removeIdenticalAiQueueMessages = _localStorage.GetBooleanOrDefault(
            LocalAppSettingKeys.RemoveIdenticalAiQueueMessages,
            defaultValue: true);
        _queueRuntimeSettings.RemoveIdenticalMessages =
            _removeIdenticalAiQueueMessages;
        _readOnlyMode = _twitchRuntimeModeSettings.IsReadOnly;
        _state.IsReadOnlyMode = _readOnlyMode;
    }

    [ObservableProperty]
    public partial string TwitchClientId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TwitchClientIdPersistenceStatus { get; set; } =
        "Die öffentliche Client-ID wird lokal gespeichert.";

    [ObservableProperty]
    public partial string ChannelLogin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LmStudioBaseUrl { get; set; } = "http://127.0.0.1:1234";

    [ObservableProperty]
    public partial string LmStudioModel { get; set; } = "openai/gpt-oss-20b";

    [ObservableProperty]
    public partial string SelectedTheme { get; set; } = "System";

    private bool _removeIdenticalAiQueueMessages = true;

    public bool RemoveIdenticalAiQueueMessages
    {
        get => _removeIdenticalAiQueueMessages;
        set
        {
            if (!SetProperty(ref _removeIdenticalAiQueueMessages, value))
            {
                return;
            }

            _queueRuntimeSettings.RemoveIdenticalMessages = value;
            try
            {
                _localStorage.SetString(
                    LocalAppSettingKeys.RemoveIdenticalAiQueueMessages,
                    value.ToString());
                SaveStatus =
                    "AI-Warteschlangenfilter automatisch gespeichert";
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                SaveStatus =
                    "AI-Warteschlangenfilter ist sofort aktiv, konnte aber "
                    + $"nicht gespeichert werden: {exception.Message}";
            }
        }
    }

    private bool _readOnlyMode;

    public bool ReadOnlyMode
    {
        get => _readOnlyMode;
        set
        {
            if (!SetProperty(ref _readOnlyMode, value))
            {
                return;
            }

            _twitchRuntimeModeSettings.IsReadOnly = value;
            _state.IsReadOnlyMode = value;
            try
            {
                _localStorage.SetString(
                    LocalAppSettingKeys.ReadOnlyMode,
                    value.ToString());
                SaveStatus = value
                    ? "Read-Only-Modus automatisch gespeichert"
                    : "Twitch-Ausführung automatisch gespeichert";
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                SaveStatus =
                    "Der Read-Only-Modus ist sofort aktiv, konnte aber nicht "
                    + $"gespeichert werden: {exception.Message}";
            }
        }
    }

    [ObservableProperty]
    public partial string SaveStatus { get; set; } = "Noch nicht gespeichert";

    [ObservableProperty]
    public partial string TwitchConnectionStatus { get; set; } = "Nicht verbunden";

    [ObservableProperty]
    public partial string LmStudioConnectionStatus { get; set; } = "Nicht geprüft";

    [ObservableProperty]
    public partial string LmStudioCredentialStatus { get; set; } =
        "Tokenstatus wird geprüft";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public void Save()
    {
        if (!Uri.TryCreate(LmStudioBaseUrl, UriKind.Absolute, out var baseAddress)
            || !baseAddress.IsLoopback)
        {
            throw new InvalidOperationException(
                "LM Studio muss über eine lokale Loopback-Adresse erreichbar sein.");
        }

        if (string.IsNullOrWhiteSpace(LmStudioModel))
        {
            throw new InvalidOperationException("Ein LM-Studio-Modell ist erforderlich.");
        }

        PersistTwitchClientId();
        _options.LmStudio.BaseAddress = EnsureTrailingSlash(baseAddress);
        _options.LmStudio.Model = LmStudioModel.Trim();

        _localStorage.SetString("ChannelLogin", ChannelLogin.Trim());
        _localStorage.SetString(
            "LmStudioBaseUrl",
            _options.LmStudio.BaseAddress.ToString());
        _localStorage.SetString("LmStudioModel", _options.LmStudio.Model);
        _localStorage.SetString("Theme", SelectedTheme);
        _localStorage.SetString(
            LocalAppSettingKeys.RemoveIdenticalAiQueueMessages,
            RemoveIdenticalAiQueueMessages.ToString());
        _localStorage.SetString(
            LocalAppSettingKeys.ReadOnlyMode,
            ReadOnlyMode.ToString());
        SaveStatus = "Lokal gespeichert · Verbindungsänderungen gelten nach App-Neustart";
    }

    public async Task ApplyReadOnlyModeChangeAsync(
        bool isReadOnly,
        CancellationToken cancellationToken = default)
    {
        ReadOnlyMode = isReadOnly;
        var restartAnalysis = _coordinator.IsRunning;
        if (!restartAnalysis && !_state.IsTwitchConnectionReady)
        {
            return;
        }

        if (restartAnalysis)
        {
            await _coordinator.StopAsync(cancellationToken);
            _state.IsAnalysisRunning = false;
        }

        try
        {
            await CheckTwitchAsync(cancellationToken);
            if (restartAnalysis)
            {
                await _coordinator.StartAsync(cancellationToken);
                _state.IsAnalysisRunning = true;
            }

            SaveStatus = isReadOnly
                ? "Read-Only aktiv · Analyse verwendet nur lesende Twitch-Ereignisse"
                : "Twitch-Ausführung aktiv · Moderationsrechte wurden neu geprüft";
        }
        catch
        {
            _state.IsAnalysisRunning = false;
            throw;
        }
    }

    public void SaveTwitchClientId()
    {
        PersistTwitchClientId();
    }

    public async Task<TwitchDeviceAuthorization> StartDeviceAuthorizationAsync(
        CancellationToken cancellationToken = default)
    {
        ApplyTwitchClientId(TwitchClientId);
        var authorization = await _oauthClient
            .StartDeviceAuthorizationAsync(cancellationToken);
        PersistTwitchClientId();
        return authorization;
    }

    public async Task CompleteDeviceAuthorizationAsync(
        TwitchDeviceAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        SetTwitchSessionPending("Twitch-Freigabe wird geprüft.");
        TwitchConnectionStatus = "Warte auf Twitch-Freigabe";
        try
        {
            var token = await _oauthClient
                .CompleteDeviceAuthorizationAsync(authorization, cancellationToken);
            var validation = await _oauthClient
                .ValidateAsync(token.AccessToken, cancellationToken);
            EnsureAndPersistValidatedTwitchClientId(validation);
            await _tokenProvider.StoreNewAuthorizationAsync(token, cancellationToken);
            await ConfigureTwitchIdentityAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task CheckTwitchAsync(CancellationToken cancellationToken = default)
    {
        await _twitchSessionGate.WaitAsync(cancellationToken);
        try
        {
            IsBusy = true;
            SetTwitchSessionPending("Gespeicherte Twitch-Verbindung wird geprüft.");
            ApplyTwitchClientId(TwitchClientId);
            var validation = await _tokenProvider
                .ValidateStoredAuthorizationAsync(cancellationToken);
            EnsureAndPersistValidatedTwitchClientId(validation);
            await ConfigureTwitchIdentityAsync(cancellationToken);
        }
        finally
        {
            IsBusy = false;
            _twitchSessionGate.Release();
        }
    }

    public async Task<TwitchSessionRestoreResult> RestoreTwitchSessionAsync(
        CancellationToken cancellationToken = default)
    {
        await _twitchSessionGate.WaitAsync(cancellationToken);
        try
        {
            ApplyTwitchClientId(TwitchClientId);

            IsBusy = true;
            SetTwitchSessionPending("Gespeicherte Twitch-Verbindung wird geprüft.");
            TwitchConnectionStatus = "Gespeicherte Twitch-Verbindung wird geprüft";

            bool hasStoredAuthorization;
            try
            {
                hasStoredAuthorization = await _tokenProvider
                    .HasStoredAuthorizationAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException)
            {
                return SetTwitchRestoreResult(
                    TwitchSessionRestoreOutcome.ReauthorizationRequired,
                    "Die gespeicherte Twitch-Anmeldung ist beschädigt und muss erneuert werden.");
            }
            catch (Exception exception) when (IsTransientConnectionFailure(exception))
            {
                return SetTwitchRestoreResult(
                    TwitchSessionRestoreOutcome.TemporarilyUnavailable,
                    "Die gespeicherte Twitch-Anmeldung ist vorhanden, konnte aber vorübergehend nicht gelesen werden.");
            }

            if (!hasStoredAuthorization)
            {
                var isClientIdMissing =
                    string.IsNullOrWhiteSpace(_options.TwitchOAuth.ClientId);
                return SetTwitchRestoreResult(
                    isClientIdMissing
                        ? TwitchSessionRestoreOutcome.NotConfigured
                        : TwitchSessionRestoreOutcome.NoStoredAuthorization,
                    isClientIdMissing
                        ? "Keine Twitch Client-ID gespeichert."
                        : "Keine gespeicherte Twitch-Anmeldung gefunden.");
            }

            try
            {
                var validation = await _tokenProvider
                    .ValidateStoredAuthorizationAsync(cancellationToken);
                EnsureAndPersistValidatedTwitchClientId(validation);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TwitchAuthorizationRequiredException)
            {
                return SetTwitchRestoreResult(
                    TwitchSessionRestoreOutcome.NoStoredAuthorization,
                    "Keine gespeicherte Twitch-Anmeldung gefunden.");
            }
            catch (InvalidDataException)
            {
                return SetTwitchRestoreResult(
                    TwitchSessionRestoreOutcome.ReauthorizationRequired,
                    "Die gespeicherte Twitch-Anmeldung ist ungültig und muss erneuert werden.");
            }
            catch (HttpRequestException exception)
                when (IsAuthorizationFailure(exception))
            {
                return SetTwitchRestoreResult(
                    TwitchSessionRestoreOutcome.ReauthorizationRequired,
                    "Die Twitch-Anmeldung wurde widerrufen oder ist abgelaufen. Eine neue Freigabe ist erforderlich.");
            }
            catch (InvalidOperationException exception)
            {
                if (string.IsNullOrWhiteSpace(_options.TwitchOAuth.ClientId))
                {
                    return SetTwitchRestoreResult(
                        TwitchSessionRestoreOutcome.NotConfigured,
                        "Keine Twitch Client-ID gespeichert. Die vorhandene Anmeldung kann ohne Client-ID nicht erneuert werden.");
                }

                return SetTwitchRestoreResult(
                    TwitchSessionRestoreOutcome.ReauthorizationRequired,
                    exception.Message);
            }
            catch (Exception exception) when (IsTransientConnectionFailure(exception))
            {
                return SetTwitchRestoreResult(
                    TwitchSessionRestoreOutcome.TemporarilyUnavailable,
                    "Gespeicherte Anmeldung vorhanden · Twitch ist vorübergehend nicht erreichbar.");
            }

            try
            {
                await ConfigureTwitchIdentityAsync(cancellationToken);
                return new TwitchSessionRestoreResult(
                    TwitchSessionRestoreOutcome.Connected,
                    TwitchConnectionStatus);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsTransientConnectionFailure(exception))
            {
                return SetTwitchRestoreResult(
                    TwitchSessionRestoreOutcome.TemporarilyUnavailable,
                    "Twitch-Anmeldung ist gespeichert · Kanalinformationen konnten vorübergehend nicht aktualisiert werden.");
            }
            catch (Exception exception)
            {
                return SetTwitchRestoreResult(
                    TwitchSessionRestoreOutcome.ConfigurationRequired,
                    $"Twitch-Anmeldung ist gültig · Kanalkonfiguration prüfen: {exception.Message}");
            }
        }
        finally
        {
            IsBusy = false;
            _twitchSessionGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> CheckLmStudioAsync(
        CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        LmStudioConnectionStatus = "Prüfe lokale Modelle";
        try
        {
            var models = await _lmStudioClient.GetLoadedModelsAsync(cancellationToken);
            var isConfiguredModelLoaded = models.Contains(
                _options.LmStudio.Model,
                StringComparer.Ordinal);
            LmStudioConnectionStatus = isConfiguredModelLoaded
                ? "Modell geladen"
                : "Verbunden · Modell nicht geladen";
            _state.LmStudioStatus = LmStudioConnectionStatus;
            return models;
        }
        catch
        {
            LmStudioConnectionStatus = "LM Studio nicht erreichbar";
            _state.LmStudioStatus = LmStudioConnectionStatus;
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RefreshLmStudioCredentialStatusAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            var token = await _lmStudioCredentialVault
                .LoadTokenAsync(cancellationToken);
            LmStudioCredentialStatus = string.IsNullOrWhiteSpace(token)
                ? "Kein optionaler API-Token gespeichert"
                : "API-Token sicher im Credential Locker gespeichert";
        }
        catch
        {
            LmStudioCredentialStatus = "Tokenstatus konnte nicht gelesen werden";
            throw;
        }
    }

    public async Task SaveLmStudioTokenAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                "Gib einen API-Token ein oder verwende „Token entfernen“.");
        }

        await _lmStudioCredentialVault
            .SaveTokenAsync(token.Trim(), cancellationToken);
        LmStudioCredentialStatus =
            "API-Token sicher im Credential Locker gespeichert";
    }

    public async Task DeleteLmStudioTokenAsync(
        CancellationToken cancellationToken = default)
    {
        await _lmStudioCredentialVault.DeleteTokenAsync(cancellationToken);
        LmStudioCredentialStatus = "Kein optionaler API-Token gespeichert";
    }

    private async Task ConfigureTwitchIdentityAsync(CancellationToken cancellationToken)
    {
        var currentUser = await _helixClient.GetCurrentUserAsync(cancellationToken);
        var normalizedLogin = ChannelLogin.Trim().TrimStart('#');
        TwitchModeratedChannel target;
        if (normalizedLogin.Length == 0
            || string.Equals(
                normalizedLogin,
                currentUser.Login,
                StringComparison.OrdinalIgnoreCase))
        {
            target = new(
                currentUser.Id,
                currentUser.Login,
                currentUser.DisplayName);
        }
        else if (_twitchRuntimeModeSettings.IsReadOnly)
        {
            var channel = await _helixClient
                .GetUserByLoginAsync(normalizedLogin, cancellationToken);
            target = channel is null
                ? throw new InvalidOperationException(
                    $"Der Twitch-Kanal '{normalizedLogin}' wurde nicht gefunden.")
                : new TwitchModeratedChannel(
                    channel.Id,
                    channel.Login,
                    channel.DisplayName);
        }
        else
        {
            var moderated = await _helixClient
                .GetModeratedChannelsAsync(currentUser.Id, cancellationToken);
            target = moderated.FirstOrDefault(channel =>
                    string.Equals(
                        channel.BroadcasterLogin,
                        normalizedLogin,
                        StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Der Kanal '{normalizedLogin}' ist nicht in der Moderatorliste dieses Kontos.");
        }

        _options.TwitchApi.ReaderUserId = currentUser.Id;
        _options.TwitchApi.ModeratorUserId = currentUser.Id;
        _options.TwitchApi.BroadcasterId = target.BroadcasterId;
        ChannelLogin = target.BroadcasterLogin;

        var channelInfo = await _helixClient
            .GetChannelAsync(target.BroadcasterId, cancellationToken);
        var justChatting = await _helixClient
            .GetGameByNameAsync("Just Chatting", cancellationToken);
        var irl = await _helixClient
            .GetGameByNameAsync("IRL", cancellationToken);
        if (justChatting is null
            || !string.Equals(
                justChatting.Id,
                EffectivePolicyResolver.BootstrapJustChattingCategoryId,
                StringComparison.Ordinal)
            || !string.Equals(
                justChatting.Name,
                "Just Chatting",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Die Twitch-Kategorie-ID für Just Chatting konnte nicht verifiziert werden.");
        }

        if (irl is null
            || !string.Equals(
                irl.Id,
                EffectivePolicyResolver.BootstrapIrlCategoryId,
                StringComparison.Ordinal)
            || !string.Equals(irl.Name, "IRL", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Die Twitch-Kategorie-ID für IRL konnte nicht verifiziert werden.");
        }

        await PersistVerifiedCategoryAsync(
            justChatting.Id,
            justChatting.Name,
            cancellationToken);
        await PersistVerifiedCategoryAsync(
            irl.Id,
            irl.Name,
            cancellationToken);

        _localStorage.SetString(
            "BroadcasterId",
            _options.TwitchApi.BroadcasterId);
        _localStorage.SetString("ReaderUserId", _options.TwitchApi.ReaderUserId);
        _localStorage.SetString(
            "ModeratorUserId",
            _options.TwitchApi.ModeratorUserId);
        _localStorage.SetString("ChannelLogin", ChannelLogin);
        _localStorage.SetString("JustChattingCategoryId", justChatting.Id);
        _localStorage.SetString("IrlCategoryId", irl.Id);

        _state.ChannelDisplayName = target.BroadcasterName;
        _state.CategoryName = channelInfo?.CategoryName ?? "Kategorie unbekannt";
        var effectivePolicy = await _policyStore.GetEffectivePolicyAsync(
            target.BroadcasterId,
            channelInfo?.CategoryId,
            cancellationToken);
        _state.ActivePolicies = effectivePolicy.CategoryRevision > 0
            ? "General + Backseatgaming"
            : "Nur General";
        _state.TwitchStatus = "Twitch verbunden";
        _state.IsTwitchConnectionReady = true;
        _twitchConnectionReadiness.SetSessionReady(
            isReady: true,
            _twitchRuntimeModeSettings.IsReadOnly
                ? "Twitch-Anmeldung und Read-Only-Kanal sind verbunden."
                : "Twitch-Anmeldung und Moderationskanal sind verbunden.");
        TwitchConnectionStatus = $"Verbunden als {currentUser.DisplayName}";
        SaveStatus = "Twitch-Zuordnung sicher gespeichert";
    }

    private async Task PersistVerifiedCategoryAsync(
        string categoryId,
        string displayName,
        CancellationToken cancellationToken)
    {
        await using var writeLease = await _writeCoordinator
            .AcquireAsync(cancellationToken);
        await using var connection = await _connectionFactory
            .OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO twitch_categories(
                category_id, display_name, last_seen_utc, last_verified_utc)
            VALUES(
                @category_id, @display_name, @verified_utc, @verified_utc)
            ON CONFLICT(category_id) DO UPDATE SET
                display_name = excluded.display_name,
                last_verified_utc = excluded.last_verified_utc;
            """;
        command.Parameters.AddWithValue("@category_id", categoryId);
        command.Parameters.AddWithValue("@display_name", displayName);
        command.Parameters.AddWithValue(
            "@verified_utc",
            DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Uri EnsureTrailingSlash(Uri value)
    {
        var text = value.ToString();
        return text.EndsWith('/')
            ? value
            : new Uri($"{text}/", UriKind.Absolute);
    }

    private string ApplyTwitchClientId(string? clientId)
    {
        var normalized = clientId?.Trim() ?? string.Empty;
        TwitchClientId = normalized;
        _options.TwitchOAuth.ClientId = normalized;
        return normalized;
    }

    private void PersistTwitchClientId()
    {
        var normalized = ApplyTwitchClientId(TwitchClientId);
        _localStorage.SetString("TwitchClientId", normalized);
        TwitchClientIdPersistenceStatus = normalized.Length == 0
            ? "Keine Client-ID gespeichert."
            : "Client-ID lokal gespeichert.";
    }

    private void EnsureAndPersistValidatedTwitchClientId(
        TwitchTokenValidation validation)
    {
        if (string.IsNullOrWhiteSpace(_options.TwitchOAuth.ClientId))
        {
            ApplyTwitchClientId(validation.ClientId);
        }

        EnsureRequiredScopes(validation);
        PersistTwitchClientId();
    }

    private TwitchSessionRestoreResult SetTwitchRestoreResult(
        TwitchSessionRestoreOutcome outcome,
        string message)
    {
        _state.IsTwitchConnectionReady = false;
        _twitchConnectionReadiness.SetSessionReady(
            isReady: false,
            message);
        TwitchConnectionStatus = message;
        _state.TwitchStatus = outcome switch
        {
            TwitchSessionRestoreOutcome.ConfigurationRequired =>
                "Twitch autorisiert · Kanal prüfen",
            TwitchSessionRestoreOutcome.TemporarilyUnavailable =>
                "Twitch vorübergehend nicht erreichbar",
            _ => "Twitch offline",
        };
        return new TwitchSessionRestoreResult(outcome, message);
    }

    private void SetTwitchSessionPending(string status)
    {
        _state.IsTwitchConnectionReady = false;
        _twitchConnectionReadiness.SetSessionReady(
            isReady: false,
            status);
    }

    private static bool IsAuthorizationFailure(HttpRequestException exception)
    {
        return exception.StatusCode is HttpStatusCode.BadRequest
            or HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden;
    }

    private static bool IsTransientConnectionFailure(Exception exception)
    {
        return exception switch
        {
            HttpRequestException requestException =>
                !IsAuthorizationFailure(requestException),
            TimeoutException => true,
            TaskCanceledException => true,
            IOException => true,
            _ => false,
        };
    }

    public void Dispose()
    {
        _twitchSessionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void EnsureRequiredScopes(TwitchTokenValidation validation)
    {
        if (!string.Equals(
                validation.ClientId,
                _options.TwitchOAuth.ClientId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Der Twitch-Token gehört nicht zur konfigurierten Client-ID.");
        }

        var granted = validation.Scopes.ToHashSet(StringComparer.Ordinal);
        var missing = _options.TwitchOAuth.Scopes
            .Where(scope => !granted.Contains(scope))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Dem Twitch-Token fehlen erforderliche Scopes: {string.Join(", ", missing)}");
        }
    }
}
