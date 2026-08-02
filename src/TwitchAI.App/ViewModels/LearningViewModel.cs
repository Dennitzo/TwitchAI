using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using TwitchAI.Core.Contracts;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.App.ViewModels;

public sealed partial class LearningViewModel : ObservableObject
{
    private readonly ILearningTestService _testService;
    private readonly ILearningFeedbackService _feedbackService;

    public LearningViewModel(
        ILearningTestService testService,
        ILearningFeedbackService feedbackService)
    {
        _testService = testService;
        _feedbackService = feedbackService;
    }

    [ObservableProperty]
    public partial string TestMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LearningReviewViewModel? Review { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } =
        "Gib eine einzelne Chatnachricht ein, um sie lokal zu prüfen.";

    public bool CanStart =>
        !IsBusy
        && Review is null
        && !string.IsNullOrWhiteSpace(TestMessage)
        && TestMessage.Length <= 500;

    public bool CanReview => !IsBusy && Review is not null;

    public bool CanEditInput => !IsBusy && Review is null;

    public Visibility ReviewVisibility =>
        Review is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility EmptyStateVisibility =>
        Review is null ? Visibility.Visible : Visibility.Collapsed;

    public string CharacterCountLabel => $"{TestMessage.Length} / 500";

    partial void OnTestMessageChanged(string value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CharacterCountLabel));
    }

    partial void OnReviewChanged(LearningReviewViewModel? value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanEditInput));
        OnPropertyChanged(nameof(ReviewVisibility));
        OnPropertyChanged(nameof(EmptyStateVisibility));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanReview));
        OnPropertyChanged(nameof(CanEditInput));
    }

    public async Task AnalyzeAsync(CancellationToken cancellationToken = default)
    {
        if (!CanStart)
        {
            if (Review is not null)
            {
                throw new InvalidOperationException(
                    "Schließe zuerst den offenen Lerntest ab.");
            }

            throw new InvalidOperationException(
                "Bitte gib eine Chatnachricht mit höchstens 500 Zeichen ein.");
        }

        var originalText = TestMessage;
        IsBusy = true;
        StatusMessage = "Die lokale AI prüft die Testnachricht …";
        try
        {
            var result = await _testService
                .AnalyzeAsync(
                    new LearningTestRequest(originalText),
                    cancellationToken);
            Review = new LearningReviewViewModel(result);
            StatusMessage =
                "AI-Auswertung abgeschlossen · Entscheidung lokal prüfen.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ConfirmViolationAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanReview)
        {
            throw new InvalidOperationException(
                "Der Lerntest wird bereits verarbeitet.");
        }

        var review = RequireReview();
        var learningReview = review.CreateReview();
        IsBusy = true;
        StatusMessage = "Bestätigung wird lokal gespeichert …";
        try
        {
            await EnsurePolicyIsCurrentAsync(
                learningReview.TestResult,
                cancellationToken);
            var result = await _feedbackService.SaveAsync(
                learningReview,
                LearningFeedbackDecision.ConfirmedViolation,
                cancellationToken);
            if (!result.Saved)
            {
                throw new InvalidOperationException(result.Message);
            }

            Reset(
                string.IsNullOrWhiteSpace(result.Message)
                    ? "Als Verstoß für das lokale Lernen gespeichert."
                    : result.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task MarkAllowedAsync(
        CancellationToken cancellationToken = default)
    {
        if (!CanReview)
        {
            throw new InvalidOperationException(
                "Der Lerntest wird bereits verarbeitet.");
        }

        var review = RequireReview();
        var learningReview = review.CreateReview();
        IsBusy = true;
        StatusMessage = "Erlaubt-Entscheidung wird lokal gespeichert …";
        try
        {
            await EnsurePolicyIsCurrentAsync(
                learningReview.TestResult,
                cancellationToken);
            var result = await _feedbackService.SaveAsync(
                learningReview,
                LearningFeedbackDecision.MarkedAllowed,
                cancellationToken);
            if (!result.Saved)
            {
                throw new InvalidOperationException(result.Message);
            }

            Reset(
                string.IsNullOrWhiteSpace(result.Message)
                    ? "Als erlaubt für das lokale Lernen gespeichert."
                    : result.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<bool> SelectRuleAsync(
        string? ruleCode,
        CancellationToken cancellationToken = default)
    {
        if (IsBusy || Review is not { } review)
        {
            return false;
        }

        var previousRuleCode = review.SelectedRuleCode;
        if (!review.TrySelectRule(ruleCode))
        {
            return false;
        }

        if (string.Equals(
                previousRuleCode,
                review.SelectedRuleCode,
                StringComparison.Ordinal))
        {
            return true;
        }

        IsBusy = true;
        StatusMessage = "Die lokale AI formuliert eine passende Begründung …";
        try
        {
            var generated = await _testService
                .GenerateReasonAsync(
                    review.CreateReasonRequest(),
                    cancellationToken);
            if (ReferenceEquals(Review, review)
                && string.Equals(
                    review.SelectedRuleCode,
                    generated.RuleCode,
                    StringComparison.Ordinal))
            {
                review.ApplyGeneratedReason(generated);
                StatusMessage =
                    "Begründung für die ausgewählte Verstoßkategorie aktualisiert.";
            }

            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public void Abort()
    {
        if (IsBusy || Review is null)
        {
            return;
        }

        Reset("Testvorschlag ohne Lernsignal abgebrochen.");
    }

    private LearningReviewViewModel RequireReview() =>
        Review
        ?? throw new InvalidOperationException(
            "Es gibt keinen offenen Lerntest.");

    private async Task EnsurePolicyIsCurrentAsync(
        LearningTestResult testResult,
        CancellationToken cancellationToken)
    {
        if (!await _testService
                .IsPolicyCurrentAsync(testResult, cancellationToken))
        {
            throw new InvalidOperationException(
                "Der Regelstand hat sich seit dem AI-Test geändert. "
                + "Bitte starte den Lerntest erneut.");
        }
    }

    private void Reset(string statusMessage)
    {
        Review = null;
        TestMessage = string.Empty;
        StatusMessage = statusMessage;
    }
}

public sealed partial class LearningReviewViewModel : ObservableObject
{
    private bool _suppressRuleDefaults;
    private string _selectedRuleCode = string.Empty;

    public LearningReviewViewModel(LearningTestResult result)
    {
        Result = result;
        RuleOptions = result.ActiveRules
            .Where(rule => ModerationRuleCodes.IsSemanticAiRule(rule.RuleCode))
            .OrderBy(rule => rule.PolicySet)
            .ThenBy(rule => rule.Priority)
            .ThenBy(rule => rule.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(ViolationCategoryOption.FromDefinition)
            .ToArray();
        TriggerPhrases = new ObservableCollection<TriggerPhraseItem>(
            result.ProposedTriggerPhrases
                .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
                .Distinct(StringComparer.Ordinal)
                .Select(phrase => new TriggerPhraseItem(phrase)));

        var initialRule = RuleOptions.FirstOrDefault(
                option => string.Equals(
                    option.Code,
                    result.ProposedRuleCode,
                    StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                "Für den Lerntest ist keine Verstoßkategorie verfügbar.");

        _suppressRuleDefaults = true;
        _selectedRuleCode = initialRule.Code;
        if (result.Verdict == LearningVerdict.Allow
            && initialRule is { } allowedCandidate)
        {
            ApplyRuleDefaults(allowedCandidate);
        }
        else
        {
            var normalized =
                ModerationActionRules.NormalizeAiSuggestionForRule(
                    result.ProposedAction,
                    result.ProposedTimeoutSeconds,
                    initialRule.Code);
            Action = ToActionValue(normalized.Action);
            TimeoutSeconds = normalized.TimeoutSeconds
                ?? initialRule.DefaultTimeoutSeconds
                ?? ModerationActionRules.DefaultTimeoutSeconds;
            Reason = string.IsNullOrWhiteSpace(result.ProposedReason)
                ? initialRule.PublicReason
                : result.ProposedReason;
        }

        _suppressRuleDefaults = false;
    }

    public LearningTestResult Result { get; }

    public string OriginalMessage => Result.OriginalText;

    public string VerdictLabel =>
        Result.Verdict == LearningVerdict.Moderate
            ? "Moderieren"
            : "Durchlassen";

    public string VerdictDescription =>
        Result.Verdict == LearningVerdict.Moderate
            ? "Die AI erkennt einen möglichen Regelverstoß."
            : "Die AI würde diese Nachricht aktuell durchlassen.";

    public string ConfidenceLabel => $"Konfidenz {Result.Confidence:P0}";

    public string LatencyLabel => $"AI {Result.AiLatencyMilliseconds:N0} ms";

    public string LocalMatchesLabel =>
        Result.LocalMatchCodes.Count == 0
            ? "Lokale Treffer: keine"
            : $"Lokale Treffer: {string.Join(", ", Result.LocalMatchCodes)}";

    public string TokenLabel => Result.TotalTokens is { } totalTokens
        ? $"Tokens {totalTokens:N0}"
        : "Tokens unbekannt";

    public string PolicyLabel
    {
        get
        {
            var policySets = Result.ActiveRules
                .Select(rule => rule.PolicySet)
                .Distinct()
                .OrderBy(policySet => policySet)
                .Select(policySet => policySet.ToString());
            return $"Geprüft: {string.Join(" + ", policySets)}";
        }
    }

    public string FallbackLabel => Result.HumanPrioritySource switch
    {
        "exact_allow_learning" => "Exaktes Erlaubt-Learning angewendet",
        "exact_moderate_learning" => "Exaktes Moderations-Learning angewendet",
        "manual_blacklist_phrase" => "Manuelle Blacklist hat Priorität",
        _ when Result.UsedRuleFallback => "Kategorie lokal ergänzt",
        _ => "Kategorie von der AI",
    };

    public IReadOnlyList<ViolationCategoryOption> RuleOptions { get; }

    public ObservableCollection<TriggerPhraseItem> TriggerPhrases { get; }

    public Visibility TriggerEvidenceVisibility =>
        TriggerPhrases.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string SelectedRuleCode => _selectedRuleCode;

    public string SelectedRuleDisplayName =>
        ResolveSelectedRule()?.DisplayName
        ?? ViolationCategoryOption.GetFallbackDisplayName(_selectedRuleCode);

    public bool TrySelectRule(string? ruleCode)
    {
        var selectedRule = FindRule(ruleCode);
        if (selectedRule is null)
        {
            return false;
        }

        if (!SetProperty(
                ref _selectedRuleCode,
                selectedRule.Code,
                nameof(SelectedRuleCode)))
        {
            return true;
        }

        OnPropertyChanged(nameof(SelectedRuleDisplayName));
        OnPropertyChanged(nameof(PolicySetLabel));
        if (!_suppressRuleDefaults)
        {
            ApplyRuleDefaults(selectedRule);
            foreach (var triggerPhrase in TriggerPhrases)
            {
                triggerPhrase.IsSelected = false;
            }
        }

        return true;
    }

    [ObservableProperty]
    public partial string Action { get; set; } = "timeout";

    [ObservableProperty]
    public partial double TimeoutSeconds { get; set; } =
        ModerationActionRules.DefaultTimeoutSeconds;

    [ObservableProperty]
    public partial string Reason { get; set; } = string.Empty;

    public Visibility TimeoutEditorVisibility =>
        Action == "timeout"
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ReasonEditorVisibility =>
        Action is "timeout" or "ban"
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string PolicySetLabel =>
        ResolveSelectedRule()?.PolicySet ?? Result.ProposedPolicySet.ToString();

    private void ApplyRuleDefaults(ViolationCategoryOption selectedRule)
    {
        var normalized =
            ModerationActionRules.NormalizeAiSuggestionForRule(
                ParseActionValue(selectedRule.DefaultAction),
                selectedRule.DefaultTimeoutSeconds,
                selectedRule.Code);
        Action = ToActionValue(normalized.Action);
        TimeoutSeconds = normalized.TimeoutSeconds
            ?? ModerationActionRules.DefaultTimeoutSeconds;
        Reason = selectedRule.PublicReason;
    }

    partial void OnActionChanged(string value)
    {
        OnPropertyChanged(nameof(TimeoutEditorVisibility));
        OnPropertyChanged(nameof(ReasonEditorVisibility));
    }

    public LearningReviewItem CreateReview()
    {
        var selectedRule = ResolveSelectedRule()
            ?? throw new InvalidOperationException(
                "Für den Lerntest ist keine Verstoßkategorie verfügbar.");
        var action = Action switch
        {
            "delete" => ModerationActionKind.Delete,
            "timeout" => ModerationActionKind.Timeout,
            "ban" => ModerationActionKind.Ban,
            _ => throw new InvalidOperationException(
                "Bitte wähle eine gültige Aktion aus."),
        };

        int? timeoutSeconds = null;
        if (action == ModerationActionKind.Timeout)
        {
            if (double.IsNaN(TimeoutSeconds)
                || double.IsInfinity(TimeoutSeconds)
                || TimeoutSeconds != Math.Truncate(TimeoutSeconds)
                || TimeoutSeconds is < 1 or > 1_209_600)
            {
                throw new InvalidOperationException(
                    "Die Timeout-Dauer muss eine ganze Zahl zwischen "
                    + "1 und 1.209.600 Sekunden sein.");
            }

            timeoutSeconds = checked((int)TimeoutSeconds);
        }

        var reason = action == ModerationActionKind.Delete
            ? string.Empty
            : Reason.Trim();
        if (action is ModerationActionKind.Timeout or ModerationActionKind.Ban
            && string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException(
                "Bitte gib eine kurze deutsche Begründung an.");
        }

        if (reason.Length > 120)
        {
            throw new InvalidOperationException(
                "Die Begründung darf höchstens 120 Zeichen lang sein.");
        }

        return new LearningReviewItem(
            Result,
            action,
            timeoutSeconds,
            ParsePolicySet(selectedRule.PolicySet),
            selectedRule.Code,
            reason,
            TriggerPhrases
                .Where(phrase => phrase.IsSelected)
                .Select(phrase => phrase.Phrase)
                .ToArray());
    }

    public LearningReasonRequest CreateReasonRequest()
    {
        var action = ParseActionValue(Action);
        return new LearningReasonRequest(
            Result,
            SelectedRuleCode,
            action,
            action == ModerationActionKind.Timeout
                ? checked((int)Math.Round(TimeoutSeconds))
                : null);
    }

    public void ApplyGeneratedReason(LearningReasonResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(
                result.RuleCode,
                SelectedRuleCode,
                StringComparison.Ordinal))
        {
            return;
        }

        Reason = result.Reason;
    }

    private ViolationCategoryOption? ResolveSelectedRule() =>
        FindRule(_selectedRuleCode)
        ?? FindRule(Result.ProposedRuleCode)
        ?? FindRule(ModerationRuleCodes.OtherAbuse)
        ?? (RuleOptions.Count > 0 ? RuleOptions[0] : null);

    private ViolationCategoryOption? FindRule(string? ruleCode) =>
        string.IsNullOrWhiteSpace(ruleCode)
            ? null
            : RuleOptions.FirstOrDefault(
                option => string.Equals(
                    option.Code,
                    ruleCode,
                    StringComparison.OrdinalIgnoreCase));

    private static string ToActionValue(ModerationActionKind action) =>
        action switch
        {
            ModerationActionKind.Delete => "delete",
            ModerationActionKind.Timeout => "timeout",
            ModerationActionKind.Ban => "ban",
            _ => "timeout",
        };

    private static string NormalizeActionValue(string? action) =>
        action?.Trim().ToLowerInvariant() switch
        {
            "delete" => "delete",
            "ban" => "ban",
            _ => "timeout",
        };

    private static ModerationActionKind ParseActionValue(string? action) =>
        NormalizeActionValue(action) switch
        {
            "delete" => ModerationActionKind.Delete,
            "ban" => ModerationActionKind.Ban,
            _ => ModerationActionKind.Timeout,
        };

    private static PolicySetCode ParsePolicySet(string policySet) =>
        Enum.TryParse<PolicySetCode>(
            policySet,
            ignoreCase: true,
            out var parsed)
            ? parsed
            : PolicySetCode.General;
}
