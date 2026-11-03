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
    private readonly IReviewReasonService _reviewReasonService;
    private readonly HashSet<ModerationCaseItem> _observedCases = [];
    private readonly Dictionary<long, CancellationTokenSource> _reasonRuns = [];

    public ModerationQueueViewModel(
        AppState state,
        ITwitchModerationExecutor executor,
        IModerationReviewFeedbackService feedbackService,
        IModerationCoordinator coordinator,
        IConfigurationDataService configuration,
        IPolicyStore policyStore,
        ITwitchChatReplyExecutor chatReplyExecutor,
        IReviewReasonService reviewReasonService)
    {
        _state = state;
        _executor = executor;
        _feedbackService = feedbackService;
        _coordinator = coordinator;
        _configuration = configuration;
        _policyStore = policyStore;
        _chatReplyExecutor = chatReplyExecutor;
        _reviewReasonService = reviewReasonService;
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

        if (_reasonRuns.Remove(item.CaseId, out var cancellation))
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }
    }

    public async Task SelectViolationRuleAsync(
        ModerationCaseItem item,
        string selectedRuleCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var previousRuleCode = item.RuleCode;
        if (!item.TrySelectViolationRule(selectedRuleCode)
            || string.Equals(
                previousRuleCode,
                item.RuleCode,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (_reasonRuns.Remove(item.CaseId, out var previousRun))
        {
            previousRun.Cancel();
            previousRun.Dispose();
        }

        item.Reason = string.Empty;
        item.IsReasonGenerating = false;
        item.ReasonGenerationStatus = string.Empty;
        if (item.Action is not ("timeout" or "ban"))
        {
            return;
        }

        var selectedCode = item.RuleCode!;
        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        _reasonRuns[item.CaseId] = linkedCancellation;
        item.IsReasonGenerating = true;
        item.ReasonGenerationStatus = "AI erstellt eine kontextbezogene Reason …";
        try
        {
            var action = item.Action == "ban"
                ? ModerationActionKind.Ban
                : ModerationActionKind.Timeout;
            var generated = await _reviewReasonService.GenerateAsync(
                new ReviewReasonRequest(
                    item.OriginalMessage,
                    item.ChannelId,
                    item.CategoryId,
                    selectedCode,
                    action,
                    action == ModerationActionKind.Timeout
                        ? checked((int)item.TimeoutSeconds)
                        : null,
                    item.TriggerPhrases
                        .Where(phrase => phrase.IsSelected)
                        .Select(phrase => phrase.Phrase)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                    $"review-reason:{item.CaseId}:{Guid.NewGuid():N}"),
                linkedCancellation.Token);
            if (!linkedCancellation.IsCancellationRequested
                && Cases.Contains(item)
                && _reasonRuns.TryGetValue(item.CaseId, out var current)
                && ReferenceEquals(current, linkedCancellation)
                && string.Equals(
                    item.RuleCode,
                    generated.RuleCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                item.Reason = generated.Reason;
                item.ReasonGenerationStatus = "AI-Reason wurde aktualisiert.";
            }
        }
        catch (OperationCanceledException)
            when (linkedCancellation.IsCancellationRequested)
        {
            // A newer category selection owns the visible state.
        }
        catch (Exception exception)
        {
            if (string.Equals(
                    item.RuleCode,
                    selectedCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                item.ReasonGenerationStatus =
                    $"AI-Reason fehlgeschlagen: {exception.Message} Manuelle Eingabe ist möglich.";
            }
        }
        finally
        {
            if (_reasonRuns.TryGetValue(item.CaseId, out var current)
                && ReferenceEquals(current, linkedCancellation))
            {
                _reasonRuns.Remove(item.CaseId);
                item.IsReasonGenerating = false;
                linkedCancellation.Dispose();
            }
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
            ? string.Join(
                ' ',
                item.Reason.Split(
                    [' ', '\t', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries))
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
                .Where(definition => activeCodes.Contains(definition.RuleCode)
                    && ModerationRuleCodes.IsSemanticAiRule(definition.RuleCode))
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
                    : ModerationRuleCodes.PersonalAttack;
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

    public Task<ModerationExecutionResult> AllowAsync(
        ModerationCaseItem item,
        CancellationToken cancellationToken = default) =>
        CompleteReviewAsync(
            item,
            "Als erlaubt markiert",
            learnAsAllowed: true,
            cancellationToken);

    public Task<ModerationExecutionResult> DiscardAsync(
        ModerationCaseItem item,
        CancellationToken cancellationToken = default) =>
        CompleteReviewAsync(
            item,
            "Vorschlag verworfen",
            learnAsAllowed: false,
            cancellationToken);

    private async Task<ModerationExecutionResult> CompleteReviewAsync(
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
        item.Status = learnAsAllowed
            ? "Erlaubtes Learning wird gespeichert"
            : "Vorschlag wird verworfen";
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

    private static void ApplyExecutionResult(
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

    public async Task SetEnabledAsync(
        IEnumerable<PolicyPhraseItem> items,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var pendingItems = items
            .Where(item => item is not null && item.PersistedIsEnabled != isEnabled)
            .Distinct()
            .ToArray();
        if (pendingItems.Length == 0)
        {
            return;
        }

        if (IsBusy)
        {
            foreach (var item in pendingItems)
            {
                item.RestoreEnabled();
            }
            return;
        }

        IsBusy = true;
        try
        {
            var entries = pendingItems
                .Select(item => (item.PhraseId, item.RowVersion))
                .ToArray();
            var rowVersions = await _configuration.SetPolicyPhrasesEnabledAsync(
                entries,
                isEnabled,
                cancellationToken);
            for (var i = 0; i < pendingItems.Length; i++)
            {
                pendingItems[i].AcceptEnabled(isEnabled, rowVersions[i]);
            }
            OnPropertyChanged(nameof(SortedPhrases));
            StatusMessage = isEnabled
                ? $"{pendingItems.Length:N0} Blacklist-Zeile(n) sind aktiv."
                : $"{pendingItems.Length:N0} Blacklist-Zeile(n) sind deaktiviert.";
        }
        catch (DatabaseConcurrencyException)
        {
            foreach (var item in pendingItems)
            {
                item.RestoreEnabled();
            }
            await ReloadCoreAsync(cancellationToken);
            StatusMessage =
                "Eine Zeile wurde zwischenzeitlich geändert. Die Auswahl wurde nicht gespeichert. Die SQLite-Daten wurden neu geladen.";
        }
        catch (Exception exception)
        {
            foreach (var item in pendingItems)
            {
                item.RestoreEnabled();
            }
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
                         StringComparison.Ordinal)
                         && !GlobalPolicyStorage.IsRetiredRuleCode(
                             row.RuleCode)))
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
        await SetEnabledAsync([item], isEnabled, cancellationToken);
    }

    public async Task SetEnabledAsync(
        IEnumerable<ChatReplyTemplateItem> items,
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var pendingItems = items
            .Where(item => item is not null && item.PersistedIsEnabled != isEnabled)
            .Distinct()
            .ToArray();
        if (pendingItems.Length == 0)
        {
            return;
        }

        if (IsBusy)
        {
            foreach (var item in pendingItems)
            {
                item.RestoreEnabled();
            }
            return;
        }

        IsBusy = true;
        try
        {
            var entries = pendingItems
                .Select(item => (item.TemplateId, item.RowVersion))
                .ToArray();
            var rowVersions = await _chatReplyStore.SetTemplatesEnabledAsync(
                entries,
                isEnabled,
                cancellationToken);
            for (var i = 0; i < pendingItems.Length; i++)
            {
                pendingItems[i].AcceptEnabled(isEnabled, rowVersions[i]);
            }
            OnPropertyChanged(nameof(SortedTemplates));
            StatusMessage = isEnabled
                ? $"{pendingItems.Length:N0} Chat-Vorlage(n) sind aktiv."
                : $"{pendingItems.Length:N0} Chat-Vorlage(n) sind deaktiviert.";
        }
        catch (Exception exception)
        {
            foreach (var item in pendingItems)
            {
                item.RestoreEnabled();
            }
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
    private static readonly object TwitchDiagnosticGate = new();
    private readonly AppState _state;
    private readonly InfrastructureOptions _options;
    private readonly ITwitchOAuthClient _oauthClient;
    private readonly ITwitchAccessTokenProvider _tokenProvider;
    private readonly ITwitchHelixClient _helixClient;
    private readonly ITwitchConnectionReadiness _twitchConnectionReadiness;
    private readonly ILocalAiClient _localAiClient;
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly IPolicyStore _policyStore;
    private readonly ILocalAppStorage _localStorage;
    private readonly ModerationQueueRuntimeSettings _queueRuntimeSettings;
    private readonly ModerationPolicyRuntimeSettings _policyRuntimeSettings;
    private readonly TwitchRuntimeModeSettings _twitchRuntimeModeSettings;
    private readonly IModerationCoordinator _coordinator;
    private readonly IDatabaseTransferService _databaseTransferService;
    private readonly IChannelStateRepository? _channelStateRepository;
    private readonly SemaphoreSlim _twitchSessionGate = new(1, 1);
    private bool _isInitializing = true;

    public SettingsViewModel(
        AppState state,
        InfrastructureOptions options,
        ITwitchOAuthClient oauthClient,
        ITwitchAccessTokenProvider tokenProvider,
        ITwitchHelixClient helixClient,
        ITwitchConnectionReadiness twitchConnectionReadiness,
        ILocalAiClient localAiClient,
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator writeCoordinator,
        IPolicyStore policyStore,
        ILocalAppStorage localStorage,
        ModerationQueueRuntimeSettings queueRuntimeSettings,
        TwitchRuntimeModeSettings twitchRuntimeModeSettings,
        IModerationCoordinator coordinator,
        IDatabaseTransferService databaseTransferService,
        ModerationPolicyRuntimeSettings? policyRuntimeSettings = null,
        IChannelStateRepository? channelStateRepository = null)
    {
        _state = state;
        _options = options;
        _oauthClient = oauthClient;
        _tokenProvider = tokenProvider;
        _helixClient = helixClient;
        _twitchConnectionReadiness = twitchConnectionReadiness;
        _localAiClient = localAiClient;
        _connectionFactory = connectionFactory;
        _writeCoordinator = writeCoordinator;
        _policyStore = policyStore;
        _localStorage = localStorage;
        _queueRuntimeSettings = queueRuntimeSettings;
        _policyRuntimeSettings = policyRuntimeSettings
            ?? new ModerationPolicyRuntimeSettings(
                _localStorage.GetBooleanOrDefault(
                    LocalAppSettingKeys.BackseatgamingEnabled,
                    defaultValue: true));
        _twitchRuntimeModeSettings = twitchRuntimeModeSettings;
        _coordinator = coordinator;
        _databaseTransferService = databaseTransferService;
        _channelStateRepository = channelStateRepository;
        TwitchClientId = _localStorage.GetString("TwitchClientId")
            ?? options.TwitchOAuth.ClientId;
        ChannelLogin = TwitchChannelSelection.FormatLogins(
            TwitchChannelSelection.ParseLogins(
                _localStorage.GetString("ChannelLogin")));
        LocalAiBaseUrl = options.LocalAi.BaseAddress.ToString().TrimEnd('/');
        ReasoningEffort = options.LocalAi.ReasoningEffort;
        LocalAiModel = _localStorage.GetString("LocalAiModel")
            ?? options.LocalAi.Model;
        if (!string.IsNullOrWhiteSpace(LocalAiModel))
        {
            AvailableLocalAiModels.Add(LocalAiModel);
        }
        SelectedTheme = _localStorage.GetString("Theme") ?? "System";
        _removeIdenticalAiQueueMessages = _localStorage.GetBooleanOrDefault(
            LocalAppSettingKeys.RemoveIdenticalAiQueueMessages,
            defaultValue: true);
        _queueRuntimeSettings.RemoveIdenticalMessages =
            _removeIdenticalAiQueueMessages;
        _backseatgamingEnabled = _policyRuntimeSettings.BackseatgamingEnabled;
        _readOnlyMode = _twitchRuntimeModeSettings.IsReadOnly;
        _state.IsReadOnlyMode = _readOnlyMode;
        _isInitializing = false;
    }

    [ObservableProperty]
    public partial string TwitchClientId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TwitchClientIdPersistenceStatus { get; set; } =
        "Die öffentliche Client-ID wird lokal gespeichert.";

    [ObservableProperty]
    public partial string ChannelLogin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LocalAiBaseUrl { get; set; } = "http://127.0.0.1:8180";

    [ObservableProperty]
    public partial string LocalAiModel { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReasoningEffort { get; set; } = "auto";

    public ObservableCollection<string> ReasoningLevels { get; } = ["auto"];

    private void RefreshReasoningLevels()
    {
        var levels = _localAiClient?.GetReasoningLevels(LocalAiModel).ToArray()
            ?? ["auto"];
        var selected = levels.Contains(ReasoningEffort, StringComparer.Ordinal)
            ? ReasoningEffort
            : "auto";
        if (!ReasoningLevels.SequenceEqual(levels, StringComparer.Ordinal))
        {
            ReasoningLevels.Clear();
            foreach (var level in levels) ReasoningLevels.Add(level);
        }
        ReasoningEffort = selected;
        OnPropertyChanged(nameof(ReasoningEffort));
    }

    partial void OnReasoningEffortChanged(string value)
    {
        if (!_isInitializing)
        {
            SaveStatus = "Reasoning geändert · Speichern übernimmt die Auswahl";
        }
    }

    public ObservableCollection<string> AvailableLocalAiModels { get; } = [];

    partial void OnLocalAiModelChanged(string value)
    {
        if (_isInitializing || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        RefreshReasoningLevels();
        SaveStatus = "Lokale-AI-Modell geändert · Speichern übernimmt die Auswahl";
    }

    [ObservableProperty]
    public partial string SelectedTheme { get; set; } = "System";

    private bool _removeIdenticalAiQueueMessages = true;

    public bool RemoveIdenticalAiQueueMessages
    {
        get => _removeIdenticalAiQueueMessages;
        set
        {
            if (SetProperty(ref _removeIdenticalAiQueueMessages, value))
            {
                SaveStatus =
                    "AI-Warteschlangenfilter geändert · Speichern übernimmt die Auswahl";
            }
        }
    }

    private bool _backseatgamingEnabled = true;

    public bool BackseatgamingEnabled
    {
        get => _backseatgamingEnabled;
        set
        {
            if (SetProperty(ref _backseatgamingEnabled, value))
            {
                SaveStatus = "Backseatgaming geändert · Speichern übernimmt die Auswahl";
            }
        }
    }

    private bool _readOnlyMode;

    public bool ReadOnlyMode
    {
        get => _readOnlyMode;
        set
        {
            if (SetProperty(ref _readOnlyMode, value))
            {
                SaveStatus = "Read-Only-Modus geändert · Speichern übernimmt die Auswahl";
            }
        }
    }

    [ObservableProperty]
    public partial string SaveStatus { get; set; } = "Noch nicht gespeichert";

    [ObservableProperty]
    public partial string TwitchConnectionStatus { get; set; } = "Nicht verbunden";

    [ObservableProperty]
    public partial string LocalAiConnectionStatus { get; set; } = "Nicht geprüft";


    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(LocalAiBaseUrl, UriKind.Absolute, out var baseAddress)
            || !IsAllowedLocalAiAddress(baseAddress))
        {
            throw new InvalidOperationException(
                "Lokale AI muss über localhost oder eine private lokale Netzwerkadresse erreichbar sein.");
        }

        if (string.IsNullOrWhiteSpace(LocalAiModel))
        {
            throw new InvalidOperationException("Ein Lokale-AI-Modell ist erforderlich.");
        }

        await _twitchSessionGate.WaitAsync(cancellationToken);
        var analysisStopped = false;
        var settingsApplied = false;
        var analysisGateEntered = false;
        try
        {
            _state.IsApplyingSettings = true;
            await _state.AnalysisLifecycleGate.WaitAsync(cancellationToken);
            analysisGateEntered = true;
            // A running batch keeps its old model, reasoning and policy until
            // all old workers have stopped. Apply one captured draft together.
            var draft = new SettingsDraft(
                TwitchClientId.Trim(),
                TwitchChannelSelection.FormatLogins(
                    TwitchChannelSelection.ParseLogins(ChannelLogin)),
                LocalAiModel.Trim(),
                ReasoningEffort,
                SelectedTheme,
                RemoveIdenticalAiQueueMessages,
                BackseatgamingEnabled,
                ReadOnlyMode);
            if (draft.Model.Length == 0)
            {
                throw new InvalidOperationException("Ein Lokale-AI-Modell ist erforderlich.");
            }
            IsBusy = true;
            SaveStatus = "Einstellungen werden geprüft und angewandt …";
            var restartAnalysis = _coordinator?.IsRunning == true;
            var wasConnected = restartAnalysis || _state.IsTwitchConnectionReady;
            PreparedTwitchIdentity? prepared = null;
            if (_tokenProvider is not null)
            {
                try
                {
                    if (wasConnected || await _tokenProvider.HasStoredAuthorizationAsync(cancellationToken))
                    {
                        var validation = await _tokenProvider.ValidateStoredAuthorizationAsync(cancellationToken);
                        var clientId = draft.TwitchClientId.Length == 0
                            ? validation.ClientId
                            : draft.TwitchClientId;
                        EnsureRequiredScopes(validation, clientId);
                        draft = draft with { TwitchClientId = clientId };
                        var previousClientId = _options.TwitchOAuth.ClientId;
                        if (restartAnalysis && !string.Equals(clientId, previousClientId, StringComparison.Ordinal))
                        {
                            await _coordinator!.StopAsync(cancellationToken);
                            analysisStopped = true;
                            _state.IsAnalysisRunning = false;
                        }
                        _options.TwitchOAuth.ClientId = clientId;
                        try
                        {
                            prepared = await PrepareTwitchIdentityAsync(
                                draft.ReadOnlyMode,
                                cancellationToken,
                                draft.BackseatgamingEnabled,
                                draft.ChannelLogin);
                        }
                        finally
                        {
                            _options.TwitchOAuth.ClientId = previousClientId;
                        }
                    }
                }
                catch (Exception exception) when (!wasConnected
                    && !cancellationToken.IsCancellationRequested
                    && (exception is TwitchAuthorizationRequiredException or InvalidDataException
                        || exception is HttpRequestException
                        || IsTransientConnectionFailure(exception)))
                {
                    // Offline settings still apply now. A missing/expired login
                    // requires reconnecting, never an application restart.
                    SetTwitchRestoreResult(
                        TwitchSessionRestoreOutcome.TemporarilyUnavailable,
                        "Twitch nicht verbunden: " + MeaningfulExceptionMessage(exception));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (restartAnalysis && !analysisStopped)
            {
                SaveStatus = "Laufende Analyse wird neu initialisiert …";
                await _coordinator!.StopAsync(cancellationToken);
                analysisStopped = true;
                _state.IsAnalysisRunning = false;
            }

            ApplySavedSettings(draft);
            settingsApplied = true;
            if (prepared is not null)
            {
                await ApplyPreparedTwitchIdentityAsync(prepared, cancellationToken);
            }
            if (restartAnalysis)
            {
                await _coordinator!.StartAsync(cancellationToken);
                _state.IsAnalysisRunning = true;
            }

            SaveStatus = prepared is null
                ? "Einstellungen gespeichert und angewandt · Twitch nicht verbunden"
                : "Einstellungen gespeichert und angewandt · TwitchAI neu initialisiert";
        }
        catch (Exception exception)
        {
            if (analysisStopped && !settingsApplied && !cancellationToken.IsCancellationRequested)
            {
                await _coordinator!.StartAsync(CancellationToken.None);
                _state.IsAnalysisRunning = true;
            }
            SaveStatus = settingsApplied
                ? "Einstellungen gespeichert · Neuinitialisierung fehlgeschlagen: " + MeaningfulExceptionMessage(exception)
                : "Einstellungen nicht übernommen: " + MeaningfulExceptionMessage(exception);
            WriteTwitchConnectionDiagnostic("Einstellungen konnten nicht vollständig angewendet werden.", exception);
            throw;
        }
        finally
        {
            IsBusy = false;
            if (analysisGateEntered) _state.AnalysisLifecycleGate.Release();
            _state.IsApplyingSettings = false;
            _twitchSessionGate.Release();
        }
    }

    private void ApplySavedSettings(SettingsDraft draft)
    {
        _localStorage.SetStrings(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TwitchClientId"] = draft.TwitchClientId,
            ["ChannelLogin"] = draft.ChannelLogin,
            ["LocalAiBaseUrl"] = "http://127.0.0.1:8180/",
            ["LocalAiModel"] = draft.Model,
            ["ReasoningEffort"] = draft.ReasoningEffort,
            ["Theme"] = draft.Theme,
            [LocalAppSettingKeys.RemoveIdenticalAiQueueMessages] = draft.RemoveIdenticalMessages.ToString(),
            [LocalAppSettingKeys.BackseatgamingEnabled] = draft.BackseatgamingEnabled.ToString(),
            [LocalAppSettingKeys.ReadOnlyMode] = draft.ReadOnlyMode.ToString(),
        });
        ApplyTwitchClientId(draft.TwitchClientId);
        ChannelLogin = draft.ChannelLogin;
        _options.LocalAi.BaseAddress = new Uri("http://127.0.0.1:8180/");
        _options.LocalAi.Model = draft.Model;
        _options.LocalAi.ReasoningEffort = draft.ReasoningEffort;
        _policyRuntimeSettings.BackseatgamingEnabled = draft.BackseatgamingEnabled;
        _queueRuntimeSettings.RemoveIdenticalMessages = draft.RemoveIdenticalMessages;
        ApplyReadOnlyModeInMemory(draft.ReadOnlyMode);
        TwitchClientIdPersistenceStatus = draft.TwitchClientId.Length == 0
            ? "Keine Client-ID gespeichert."
            : "Client-ID lokal gespeichert.";
    }

    private sealed record SettingsDraft(
        string TwitchClientId,
        string ChannelLogin,
        string Model,
        string ReasoningEffort,
        string Theme,
        bool RemoveIdenticalMessages,
        bool BackseatgamingEnabled,
        bool ReadOnlyMode);

    private static bool IsAllowedLocalAiAddress(Uri address)
    {
        if (address.IsLoopback)
        {
            return true;
        }

        if (address.Scheme is not ("http" or "https")
            || !IPAddress.TryParse(address.Host, out var ip)
            || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    public async Task ExportDatabaseAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        await _databaseTransferService.ExportAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        SaveStatus = "Datenbank exportiert · alle Daten sind enthalten";
    }

    public async Task ImportDatabaseAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        await _coordinator.StopAsync(cancellationToken).ConfigureAwait(false);
        _state.IsAnalysisRunning = false;
        await _databaseTransferService.ImportAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        _state.IsTwitchConnectionReady = false;
        _twitchConnectionReadiness.SetSessionReady(false, "Datenbank importiert · Verbindung bitte erneut prüfen");
        SaveStatus = "Datenbank importiert · TwitchAI bitte neu starten";
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
        if (_coordinator?.IsRunning != true
            && !_state.IsTwitchConnectionReady)
        {
            SetTwitchSessionPending("Twitch-Freigabe wird geprüft.");
        }

        TwitchConnectionStatus = "Warte auf Twitch-Freigabe";
        try
        {
            var token = await _oauthClient
                .CompleteDeviceAuthorizationAsync(authorization, cancellationToken);
            var validation = await _oauthClient
                .ValidateAsync(token.AccessToken, cancellationToken);
            EnsureValidatedTwitchClientId(validation);
            await _tokenProvider.StoreNewAuthorizationAsync(token, cancellationToken);
            await ConfigureTwitchIdentityAndRestartAnalysisAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            WriteTwitchConnectionDiagnostic(
                "Twitch-Geräteautorisierung konnte nicht abgeschlossen werden.",
                exception);
            if (!string.IsNullOrWhiteSpace(exception.Message))
            {
                throw;
            }

            throw new InvalidOperationException(
                MeaningfulExceptionMessage(exception),
                exception);
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
            if (_coordinator?.IsRunning != true
                && !_state.IsTwitchConnectionReady)
            {
                SetTwitchSessionPending("Gespeicherte Twitch-Verbindung wird geprüft.");
            }

            ApplyTwitchClientId(TwitchClientId);
            var validation = await _tokenProvider
                .ValidateStoredAuthorizationAsync(cancellationToken);
            EnsureValidatedTwitchClientId(validation);
            await ConfigureTwitchIdentityAndRestartAnalysisAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            WriteTwitchConnectionDiagnostic(
                "Manuelle Twitch-Prüfung fehlgeschlagen.",
                exception);
            if (!string.IsNullOrWhiteSpace(exception.Message))
            {
                throw;
            }

            throw new InvalidOperationException(
                MeaningfulExceptionMessage(exception),
                exception);
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
            if (_coordinator?.IsRunning != true
                && !_state.IsTwitchConnectionReady)
            {
                SetTwitchSessionPending("Gespeicherte Twitch-Verbindung wird geprüft.");
            }

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
                WriteTwitchConnectionDiagnostic(
                    "Twitch-Kanäle konnten vorübergehend nicht vorbereitet werden.",
                    exception);
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
                EnsureValidatedTwitchClientId(validation);
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
                await ConfigureTwitchIdentityAndRestartAnalysisAsync(cancellationToken);
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
                WriteTwitchConnectionDiagnostic(
                    "Gespeicherte Twitch-Kanalkonfiguration konnte nicht wiederhergestellt werden.",
                    exception);
                return SetTwitchRestoreResult(
                    TwitchSessionRestoreOutcome.ConfigurationRequired,
                    "Twitch-Anmeldung ist gültig · Kanalkonfiguration prüfen: "
                    + MeaningfulExceptionMessage(exception));
            }
        }
        finally
        {
            IsBusy = false;
            _twitchSessionGate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> CheckLocalAiAsync(
        CancellationToken cancellationToken = default)
    {
        await _twitchSessionGate.WaitAsync(cancellationToken);
        IsBusy = true;
        LocalAiConnectionStatus = "Prüfe verfügbare Modelle";
        try
        {
            var models = await _localAiClient.GetLoadedModelsAsync(cancellationToken);
            var availableModels = models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var selectedModel = availableModels.Contains(LocalAiModel, StringComparer.Ordinal)
                ? LocalAiModel
                : availableModels.FirstOrDefault() ?? string.Empty;
            for (var index = AvailableLocalAiModels.Count - 1; index >= 0; index--)
            {
                var existingModel = AvailableLocalAiModels[index];
                if (!availableModels.Contains(existingModel, StringComparer.Ordinal))
                {
                    AvailableLocalAiModels.RemoveAt(index);
                }
            }

            foreach (var availableModel in availableModels)
            {
                if (!AvailableLocalAiModels.Contains(
                        availableModel,
                        StringComparer.Ordinal))
                {
                    AvailableLocalAiModels.Add(availableModel);
                }
            }
            LocalAiModel = selectedModel;
            RefreshReasoningLevels();
            var isConfiguredModelAvailable = availableModels.Contains(
                _options.LocalAi.Model,
                StringComparer.Ordinal);
            LocalAiConnectionStatus = isConfiguredModelAvailable
                ? "Modell verfügbar"
                : "Verbunden · kein vollständiges GGUF-Modell im Hugging-Face-Cache gefunden";
            _state.LocalAiStatus = LocalAiConnectionStatus;
            return availableModels;
        }
        catch (Exception exception)
        {
            LocalAiConnectionStatus = $"Lokale AI nicht erreichbar: {exception.Message}";
            _state.LocalAiStatus = LocalAiConnectionStatus;
            throw;
        }
        finally
        {
            IsBusy = false;
            _twitchSessionGate.Release();
        }
    }

    private async Task ConfigureTwitchIdentityAndRestartAnalysisAsync(
        CancellationToken cancellationToken)
    {
        var prepared = await PrepareTwitchIdentityAsync(
                _twitchRuntimeModeSettings.IsReadOnly,
                cancellationToken);
        var restartAnalysis = _coordinator.IsRunning;
        await ApplyPreparedTwitchIdentityAndRestartAnalysisAsync(
                prepared,
                restartAnalysis,
                cancellationToken);
    }

    private async Task<PreparedTwitchIdentity> PrepareTwitchIdentityAsync(
        bool isReadOnly,
        CancellationToken cancellationToken,
        bool? backseatgamingEnabled = null,
        string? channelLogin = null)
    {
        var currentUser = await _helixClient
            .GetCurrentUserAsync(cancellationToken);
        var targets = await ResolveTargetChannelsAsync(
                currentUser,
                isReadOnly,
                cancellationToken,
                channelLogin);

        var channelInfos = new List<TwitchChannelInfo>(targets.Count);
        foreach (var target in targets)
        {
            TwitchChannelInfo? channelInfo;
            try
            {
                channelInfo = await _helixClient
                    .GetChannelAsync(target.BroadcasterId, cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Kanalinformationen für '{target.BroadcasterLogin}' konnten nicht geladen werden: "
                    + MeaningfulExceptionMessage(exception),
                    exception);
            }
            if (channelInfo is null
                || !string.Equals(
                    channelInfo.BroadcasterId,
                    target.BroadcasterId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Die Kanalinformationen für '{target.BroadcasterLogin}' konnten nicht verifiziert werden.");
            }

            channelInfos.Add(channelInfo);
        }

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

        var policies = new List<
            TwitchAI.Infrastructure.Persistence.EffectivePolicySnapshot>(
            targets.Count);
        for (var index = 0; index < targets.Count; index++)
        {
            try
            {
                var info = channelInfos[index];
                if (!string.IsNullOrWhiteSpace(info.CategoryId))
                {
                    // The policy store only includes category rules for known
                    // categories. Register the freshly verified game first.
                    await PersistVerifiedCategoryAsync(
                        info.CategoryId,
                        info.CategoryName ?? info.CategoryId,
                        cancellationToken);
                }
                policies.Add(
                    await _policyStore.GetEffectivePolicyAsync(
                        targets[index].BroadcasterId,
                        channelInfos[index].CategoryId,
                        cancellationToken));
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Regelkontext für '{targets[index].BroadcasterLogin}' konnte nicht geladen werden: "
                    + MeaningfulExceptionMessage(exception),
                    exception);
            }
        }

        var normalizedChannelLogins = TwitchChannelSelection.FormatLogins(
            targets.Select(target => target.BroadcasterLogin));
        var categoryNames = channelInfos
            .Select(info => info.CategoryName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var categoryName = categoryNames.Length switch
        {
            0 => "Kategorie unbekannt",
            1 => categoryNames[0]!,
            _ => "Mehrere Kategorien",
        };

        var resolver = new EffectivePolicyResolver(
            justChatting.Id,
            irl.Id,
            new ModerationPolicyRuntimeSettings(
                backseatgamingEnabled ?? _policyRuntimeSettings.BackseatgamingEnabled));
        var hasBackseatgaming = policies.Select((policy, index) => resolver.Resolve(
                new ChannelContextSnapshot(
                    targets[index].BroadcasterId,
                    channelInfos[index].CategoryId,
                    channelInfos[index].CategoryName ?? string.Empty,
                    DateTimeOffset.UtcNow,
                    1,
                    IsVerified: policy.CategoryRevision > 0
                        && string.Equals(policy.CategoryId, channelInfos[index].CategoryId, StringComparison.Ordinal)),
                Math.Max(1, policy.GeneralRevision),
                Math.Max(1, policy.CategoryRevision)))
            .Any(policy => policy.Contains(PolicySetCode.Backseatgaming));

        return new PreparedTwitchIdentity(
            currentUser,
            targets,
            channelInfos,
            policies,
            justChatting,
            irl,
            normalizedChannelLogins,
            string.Join(", ", targets.Select(target => target.BroadcasterName)),
            categoryName,
            hasBackseatgaming
                ? "General + Backseatgaming"
                : "Nur General",
            isReadOnly);
    }

    private async Task ApplyPreparedTwitchIdentityAndRestartAnalysisAsync(
        PreparedTwitchIdentity prepared,
        bool restartAnalysis,
        CancellationToken cancellationToken)
    {
        if (restartAnalysis)
        {
            await _coordinator.StopAsync(cancellationToken);
            _state.IsAnalysisRunning = false;
        }

        await ApplyPreparedTwitchIdentityAsync(prepared, cancellationToken);
        if (!restartAnalysis)
        {
            return;
        }

        await _coordinator.StartAsync(cancellationToken);
        _state.IsAnalysisRunning = true;
    }

    private async Task ApplyPreparedTwitchIdentityAsync(
        PreparedTwitchIdentity prepared,
        CancellationToken cancellationToken)
    {
        await PersistVerifiedCategoryAsync(
                prepared.JustChatting.Id,
                prepared.JustChatting.Name,
                cancellationToken);

        if (_channelStateRepository is not null)
        {
            foreach (var info in prepared.ChannelInfos)
            {
                await _channelStateRepository.UpsertAsync(new ChannelStateRecord(
                    info.BroadcasterId,
                    info.BroadcasterLogin,
                    info.BroadcasterName,
                    prepared.CurrentUser.Id,
                    info.CategoryId,
                    info.CategoryName,
                    DateTimeOffset.UtcNow,
                    IsVerified: true), cancellationToken);
            }
        }
        await PersistVerifiedCategoryAsync(
                prepared.Irl.Id,
                prepared.Irl.Name,
                cancellationToken);

        var broadcasterIds = prepared.Targets
            .Select(target => target.BroadcasterId)
            .ToArray();
        _localStorage.SetStrings(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["BroadcasterId"] = broadcasterIds[0],
                ["BroadcasterIds"] = string.Join(",", broadcasterIds),
                ["ReaderUserId"] = prepared.CurrentUser.Id,
                ["ModeratorUserId"] = prepared.CurrentUser.Id,
                ["ChannelLogin"] = prepared.NormalizedChannelLogins,
                ["JustChattingCategoryId"] = prepared.JustChatting.Id,
                ["IrlCategoryId"] = prepared.Irl.Id,
                ["TwitchClientId"] = _options.TwitchOAuth.ClientId,
                [LocalAppSettingKeys.ReadOnlyMode] = prepared.IsReadOnly.ToString(),
            });

        _options.TwitchApi.ReaderUserId = prepared.CurrentUser.Id;
        _options.TwitchApi.ModeratorUserId = prepared.CurrentUser.Id;
        _options.TwitchApi.SetBroadcasterIds(broadcasterIds);
        ApplyReadOnlyModeInMemory(prepared.IsReadOnly);
        ChannelLogin = prepared.NormalizedChannelLogins;
        TwitchClientIdPersistenceStatus =
            _options.TwitchOAuth.ClientId.Length == 0
                ? "Keine Client-ID gespeichert."
                : "Client-ID lokal gespeichert.";

        _state.ChannelDisplayName = prepared.ChannelDisplayName;
        _state.ReplaceChannelNames(
            prepared.Targets.Select(target => (
                target.BroadcasterId,
                target.BroadcasterName)));
        _state.CategoryName = prepared.CategoryName;
        _state.ActivePolicies = prepared.ActivePolicies;
        _state.TwitchStatus = prepared.Targets.Count == 1
            ? "Twitch verbunden"
            : $"Twitch verbunden · {prepared.Targets.Count} Kanäle";
        _state.IsTwitchConnectionReady = true;
        _twitchConnectionReadiness.SetSessionReady(
            isReady: true,
            prepared.IsReadOnly
                ? "Twitch-Anmeldung und Read-Only-Kanäle sind verbunden."
                : "Twitch-Anmeldung und Moderationskanäle sind verbunden.");
        TwitchConnectionStatus = prepared.Targets.Count == 1
            ? $"Verbunden als {prepared.CurrentUser.DisplayName}"
            : $"Verbunden als {prepared.CurrentUser.DisplayName} · {prepared.Targets.Count} Kanäle";
        SaveStatus = "Twitch-Zuordnung sicher gespeichert";
    }

    private async Task<IReadOnlyList<TwitchModeratedChannel>> ResolveTargetChannelsAsync(
        TwitchUserInfo currentUser,
        bool isReadOnly,
        CancellationToken cancellationToken,
        string? channelLogin = null)
    {
        var requestedLogins = TwitchChannelSelection.ParseLogins(channelLogin ?? ChannelLogin);
        if (requestedLogins.Count == 0)
        {
            requestedLogins = [currentUser.Login];
        }


        var maximumChannels = TwitchChannelSelection.GetMaximumChannelCount(isReadOnly);
        if (requestedLogins.Count > maximumChannels)
        {
            throw new InvalidOperationException(
                $"Es können in diesem Modus höchstens {maximumChannels} "
                + "Twitch-Kanäle gleichzeitig verbunden werden.");
        }

        var ownChannel = new TwitchModeratedChannel(
            currentUser.Id,
            currentUser.Login,
            currentUser.DisplayName);
        if (isReadOnly)
        {
            var readOnlyTargets = new List<TwitchModeratedChannel>(
                requestedLogins.Count);
            foreach (var login in requestedLogins)
            {
                if (string.Equals(
                        login,
                        currentUser.Login,
                        StringComparison.OrdinalIgnoreCase))
                {
                    readOnlyTargets.Add(ownChannel);
                    continue;
                }

                var user = await _helixClient
                    .GetUserByLoginAsync(login, cancellationToken);
                if (user is null)
                {
                    throw new InvalidOperationException(
                        $"Der Twitch-Kanal '{login}' wurde nicht gefunden.");
                }

                readOnlyTargets.Add(
                    new TwitchModeratedChannel(
                        user.Id,
                        user.Login,
                        user.DisplayName));
            }

            return readOnlyTargets
                .DistinctBy(target => target.BroadcasterId, StringComparer.Ordinal)
                .ToArray();
        }

        var moderated = await _helixClient
            .GetModeratedChannelsAsync(currentUser.Id, cancellationToken);
        var available = moderated
            .Append(ownChannel)
            .DistinctBy(target => target.BroadcasterId, StringComparer.Ordinal)
            .ToDictionary(
                target => target.BroadcasterLogin,
                StringComparer.OrdinalIgnoreCase);
        var targets = new List<TwitchModeratedChannel>(requestedLogins.Count);
        foreach (var login in requestedLogins)
        {
            if (!available.TryGetValue(login, out var target))
            {
                throw new InvalidOperationException(
                    $"Der Kanal '{login}' ist nicht in der Moderatorliste dieses Kontos.");
            }

            targets.Add(target);
        }

        return targets
            .DistinctBy(target => target.BroadcasterId, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record PreparedTwitchIdentity(
        TwitchUserInfo CurrentUser,
        IReadOnlyList<TwitchModeratedChannel> Targets,
        IReadOnlyList<TwitchChannelInfo> ChannelInfos,
        IReadOnlyList<TwitchAI.Infrastructure.Persistence.EffectivePolicySnapshot> Policies,
        TwitchGameInfo JustChatting,
        TwitchGameInfo Irl,
        string NormalizedChannelLogins,
        string ChannelDisplayName,
        string CategoryName,
        string ActivePolicies,
        bool IsReadOnly);

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

    private bool ApplyReadOnlyModeInMemory(bool value)
    {
        SetProperty(ref _readOnlyMode, value, nameof(ReadOnlyMode));
        _twitchRuntimeModeSettings.IsReadOnly = value;
        _state.IsReadOnlyMode = value;
        return true;
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

    private void EnsureValidatedTwitchClientId(
        TwitchTokenValidation validation)
    {
        if (string.IsNullOrWhiteSpace(_options.TwitchOAuth.ClientId))
        {
            ApplyTwitchClientId(validation.ClientId);
        }

        EnsureRequiredScopes(validation);
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
                "Twitch autorisiert · Kanäle prüfen",
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
            _ when exception.InnerException is not null =>
                IsTransientConnectionFailure(exception.InnerException),
            _ => false,
        };
    }

    private static string MeaningfulExceptionMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (Exception? current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                return current.Message.Trim();
            }
        }

        return $"{exception.GetType().Name} (0x{exception.HResult:X8})";
    }

    private void WriteTwitchConnectionDiagnostic(
        string operation,
        Exception exception)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_localStorage.LocalFolderPath))
            {
                return;
            }

            lock (TwitchDiagnosticGate)
            {
                var path = Path.Combine(
                    _localStorage.LocalFolderPath,
                    "twitch-connection-error.log");
                File.AppendAllText(
                    path,
                    $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}"
                    + operation
                    + Environment.NewLine
                    + exception
                    + Environment.NewLine
                    + Environment.NewLine);
            }
        }
        catch
        {
            // A diagnostic write must never replace the original Twitch error.
        }
    }

    public void Dispose()
    {
        _twitchSessionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void EnsureRequiredScopes(TwitchTokenValidation validation, string? clientId = null)
    {
        if (!string.Equals(
                validation.ClientId,
                clientId ?? _options.TwitchOAuth.ClientId,
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
