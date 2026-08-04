using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using TwitchAI.Core.Models;
using TwitchAI.Core.Services;

namespace TwitchAI.App.ViewModels;

public sealed partial class AppState : ObservableObject
{
    private readonly Dictionary<string, string> _channelNames =
        new(StringComparer.Ordinal);

    public ObservableCollection<ModerationCaseItem> ModerationCases { get; } = [];

    public LocalAiRunItem CurrentLocalAiRun { get; } = new();

    public ObservableCollection<LocalAiRunItem> ActiveLocalAiRuns { get; } = [];

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
    public partial bool IsReadOnlyMode { get; set; }

    [ObservableProperty]
    public partial int SkippedOverloadCount { get; set; }

    [ObservableProperty]
    public partial int SkippedWhitelistCount { get; set; }

    [ObservableProperty]
    public partial int ActiveLocalAiRunCount { get; set; }

    [ObservableProperty]
    public partial int ActiveLocalAiMessageCount { get; set; }

    [ObservableProperty]
    public partial int QueuedLocalAiRunCount { get; set; }

    [ObservableProperty]
    public partial int QueuedLocalAiMessageCount { get; set; }

    public void ReplaceChannelNames(
        IEnumerable<(string ChannelId, string DisplayName)> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _channelNames.Clear();
        foreach (var channel in channels)
        {
            if (!string.IsNullOrWhiteSpace(channel.ChannelId)
                && !string.IsNullOrWhiteSpace(channel.DisplayName))
            {
                _channelNames[channel.ChannelId] = channel.DisplayName.Trim();
            }
        }
    }

    public string ResolveChannelName(string channelId) =>
        _channelNames.TryGetValue(channelId, out var displayName)
            ? displayName
            : string.Empty;
}

public sealed partial class ModerationCaseItem : ObservableObject
{
    private bool _suppressViolationRuleDefaults;
    private bool _hasViolationRuleSnapshot;
    private string? _ruleCode;
    private IReadOnlyList<ViolationCategoryOption> _violationRuleOptions = [];

    public required long CaseId { get; init; }

    public bool IsChatReply { get; init; }

    public string ReplyMessage { get; init; } = string.Empty;

    public string ReviewTitle =>
        IsChatReply
            ? "Vorschlag prüfen · Chat-Antwort"
            : "Vorschlag prüfen";

    public Visibility ModerationEditorVisibility =>
        IsChatReply ? Visibility.Collapsed : Visibility.Visible;

    public Visibility TriggerEvidenceVisibility =>
        IsChatReply || TriggerPhrases.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility ChatReplyEditorVisibility =>
        IsChatReply ? Visibility.Visible : Visibility.Collapsed;

    public required string ChannelId { get; init; }

    public string ChannelName { get; init; } = string.Empty;

    public string ChannelDisplayLabel =>
        string.IsNullOrWhiteSpace(ChannelName)
            ? $"Channel: {ChannelId}"
            : $"Channel: {ChannelName} ({ChannelId})";

    public required string UserName { get; init; }

    public required string OriginalMessage { get; init; }

    public bool IsAutoModHeld { get; init; }

    public Visibility DeleteActionVisibility =>
        IsAutoModHeld ? Visibility.Collapsed : Visibility.Visible;

    public Visibility AllowActionVisibility =>
        IsChatReply ? Visibility.Collapsed : Visibility.Visible;

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

    public Visibility ReasonEditorVisibility =>
        Action is "timeout" or "ban"
            ? Visibility.Visible
            : Visibility.Collapsed;

    [ObservableProperty]
    public partial double TimeoutSeconds { get; set; } =
        ModerationActionRules.DefaultTimeoutSeconds;

    [ObservableProperty]
    public partial string Reason { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsReasonGenerating { get; set; }

    [ObservableProperty]
    public partial string ReasonGenerationStatus { get; set; } = string.Empty;

    public Visibility ReasonGenerationStatusVisibility =>
        string.IsNullOrWhiteSpace(ReasonGenerationStatus)
            ? Visibility.Collapsed
            : Visibility.Visible;

    [ObservableProperty]
    public partial string PolicySet { get; set; } = "General";

    public string? RuleCode
    {
        get => _ruleCode;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value)
                ? null
                : value.Trim();

            // A recycled WinUI ComboBox can briefly report no selection.
            // Once a validated code exists, a visual transition must never
            // erase the canonical domain value.
            if (normalized is null && _ruleCode is not null)
            {
                OnPropertyChanged(nameof(RuleCode));
                return;
            }

            if (!SetProperty(ref _ruleCode, normalized))
            {
                return;
            }

            EnsureProvisionalViolationRule();
            OnPropertyChanged(nameof(ViolationCategoryDisplayName));
            OnPropertyChanged(nameof(CanExecute));
            OnPropertyChanged(nameof(CanAllow));

            if (!_suppressViolationRuleDefaults
                && _hasViolationRuleSnapshot
                && FindViolationRule(normalized) is { } selectedRule)
            {
                ApplyViolationRuleDefaults(selectedRule);
            }
        }
    }

    public IReadOnlyList<ViolationCategoryOption> ViolationRuleOptions
    {
        get => _violationRuleOptions;
        private set
        {
            if (!SetProperty(ref _violationRuleOptions, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ViolationCategoryDisplayName));
            // Resolve the unchanged code against the new immutable snapshot.
            OnPropertyChanged(nameof(RuleCode));
        }
    }

    public bool HasViolationRuleSnapshot => _hasViolationRuleSnapshot;

    public string ViolationCategoryDisplayName =>
        FindViolationRule(RuleCode)?.DisplayName
        ?? ViolationCategoryOption.GetFallbackDisplayName(RuleCode);

    [ObservableProperty]
    public partial double Confidence { get; set; }

    public string ConfidenceLabel => $"Konfidenz {Confidence:P0}";

    public IReadOnlyList<string> LocalMatchCodes { get; init; } = [];

    public string ProposalOriginLabel { get; init; } = string.Empty;

    public Visibility ProposalOriginVisibility =>
        string.IsNullOrWhiteSpace(ProposalOriginLabel)
            ? Visibility.Collapsed
            : Visibility.Visible;

    public long? AiLatencyMilliseconds { get; init; }

    public string AiDurationLabel => AiLatencyMilliseconds is { } duration
        ? $"AI {duration:N0} ms"
        : "AI-Dauer unbekannt";

    partial void OnConfidenceChanged(double value) =>
        OnPropertyChanged(nameof(ConfidenceLabel));

    partial void OnActionChanged(string value)
    {
        OnPropertyChanged(nameof(TimeoutEditorVisibility));
        OnPropertyChanged(nameof(ReasonEditorVisibility));
        OnPropertyChanged(nameof(CanExecute));
    }

    partial void OnReasonChanged(string value) =>
        OnPropertyChanged(nameof(CanExecute));

    partial void OnIsReasonGeneratingChanged(bool value) =>
        OnPropertyChanged(nameof(CanExecute));

    partial void OnReasonGenerationStatusChanged(string value) =>
        OnPropertyChanged(nameof(ReasonGenerationStatusVisibility));

    [ObservableProperty]
    public partial string AvailabilityLabel { get; set; } = "Nachricht verfügbar";

    [ObservableProperty]
    public partial string Status { get; set; } = "Pending";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public bool CanExecute =>
        !IsBusy
        && !IsReasonGenerating
        && (!IsAutoModHeld || Action is "timeout" or "ban")
        && (IsChatReply || HasUsableViolationRule)
        && (IsChatReply
            || Action is not ("timeout" or "ban")
            || Reason.Trim().Length is >= 1 and <= ModerationReasonNormalizer.MaximumLength)
        && Status is "Pending" or "Extern erledigt · Lernentscheidung offen";

    public bool CanAllow =>
        !IsChatReply
        && !IsBusy
        && HasUsableViolationRule
        && Status == "Pending";

    public bool CanDiscard =>
        !IsBusy
        && (Status == "Pending"
            || (IsChatReply && Status == "Fehlgeschlagen"));

    private bool HasUsableViolationRule =>
        !string.IsNullOrWhiteSpace(RuleCode)
        && PolicySet is "General" or "Backseatgaming";

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanAllow));
        OnPropertyChanged(nameof(CanDiscard));
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanAllow));
        OnPropertyChanged(nameof(CanDiscard));
    }

    partial void OnPolicySetChanged(string value)
    {
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanAllow));
    }

    public bool TrySelectViolationRule(string? ruleCode)
    {
        var selected = FindViolationRule(ruleCode);
        if (selected is null)
        {
            return false;
        }

        RuleCode = selected.Code;
        return true;
    }

    public ViolationCategoryOption? FindViolationRule(string? ruleCode) =>
        string.IsNullOrWhiteSpace(ruleCode)
            ? null
            : ViolationRuleOptions.FirstOrDefault(
                option => string.Equals(
                    option.Code,
                    ruleCode,
                    StringComparison.OrdinalIgnoreCase));

    public void ReplaceViolationRuleOptions(
        IReadOnlyList<ViolationCategoryOption> options,
        string? selectedRuleCode,
        bool applyFallbackDefaults = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        var distinctOptions = options
            .Where(option => !string.IsNullOrWhiteSpace(option.Code))
            .GroupBy(option => option.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        var hasPolicySnapshot = distinctOptions.Length > 0;
        var requestedCode = string.IsNullOrWhiteSpace(selectedRuleCode)
            ? RuleCode
            : selectedRuleCode.Trim();
        requestedCode ??= string.Equals(
            PolicySet,
            PolicySetCode.Backseatgaming.ToString(),
            StringComparison.OrdinalIgnoreCase)
                ? ModerationRuleCodes.Backseatgaming
                : ModerationRuleCodes.OtherAbuse;
        var selectedOption = distinctOptions.FirstOrDefault(
            option => !string.IsNullOrWhiteSpace(requestedCode)
                && string.Equals(
                    option.Code,
                    requestedCode,
                    StringComparison.OrdinalIgnoreCase));

        if (selectedOption is null && !string.IsNullOrWhiteSpace(requestedCode))
        {
            selectedOption = ViolationCategoryOption.CreateFallback(
                requestedCode,
                PolicySet,
                Action,
                TimeoutSeconds,
                Reason,
                markUnavailable: distinctOptions.Length > 0);
            distinctOptions = [selectedOption, .. distinctOptions];
        }
        else if (selectedOption is null && distinctOptions.Length > 0)
        {
            selectedOption = distinctOptions[0];
            applyFallbackDefaults = true;
        }

        _suppressViolationRuleDefaults = true;
        try
        {
            ViolationRuleOptions = distinctOptions;
            if (selectedOption is not null)
            {
                RuleCode = selectedOption.Code;
            }

            _hasViolationRuleSnapshot = hasPolicySnapshot;
        }
        finally
        {
            _suppressViolationRuleDefaults = false;
        }

        OnPropertyChanged(nameof(HasViolationRuleSnapshot));
        OnPropertyChanged(nameof(ViolationCategoryDisplayName));
        OnPropertyChanged(nameof(CanExecute));
        OnPropertyChanged(nameof(CanAllow));
        if (applyFallbackDefaults && selectedOption is not null)
        {
            ApplyViolationRuleDefaults(selectedOption);
        }
    }

    private void ApplyViolationRuleDefaults(ViolationCategoryOption rule)
    {
        var sourceAction = rule.DefaultAction?.Trim().ToLowerInvariant() switch
        {
            "delete" => ModerationActionKind.Delete,
            "ban" => ModerationActionKind.Ban,
            _ => ModerationActionKind.Timeout,
        };
        var normalized = ModerationActionRules.NormalizeAiSuggestionForRule(
            IsAutoModHeld && sourceAction == ModerationActionKind.Delete
                ? ModerationActionKind.Timeout
                : sourceAction,
            IsAutoModHeld && sourceAction == ModerationActionKind.Delete
                ? ModerationActionRules.DefaultTimeoutSeconds
                : rule.DefaultTimeoutSeconds,
            rule.Code);
        Action = normalized.Action switch
        {
            ModerationActionKind.Delete => "delete",
            ModerationActionKind.Ban => "ban",
            _ => "timeout",
        };
        TimeoutSeconds = normalized.TimeoutSeconds
            ?? ModerationActionRules.DefaultTimeoutSeconds;
        PolicySet = rule.PolicySet;
        // Keep the AI's message-specific reason when a reviewer changes the
        // category. A rule-level public reason is generic and must never
        // replace the contextual moderation explanation.
    }

    private void EnsureProvisionalViolationRule()
    {
        if (string.IsNullOrWhiteSpace(RuleCode)
            || FindViolationRule(RuleCode) is not null)
        {
            return;
        }

        ViolationRuleOptions =
        [
            ViolationCategoryOption.CreateFallback(
                RuleCode,
                PolicySet,
                Action,
                TimeoutSeconds,
                Reason,
                markUnavailable: false),
            .. ViolationRuleOptions,
        ];
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

/// <summary>
/// Immutable UI projection of a moderation rule. The Twitch/AI rule code is
/// the stable identity; the ComboBox must never rely on object identity.
/// </summary>
public sealed record ViolationCategoryOption(
    string PolicySet,
    string Code,
    string DisplayName,
    string PublicReason,
    string DefaultAction,
    int? DefaultTimeoutSeconds)
{
    public static ViolationCategoryOption FromDefinition(
        ModerationRuleDefinition definition) =>
        new(
            definition.PolicySet.ToString(),
            definition.RuleCode,
            definition.Title,
            definition.PublicReason,
            definition.DefaultAction.ToString().ToLowerInvariant(),
            definition.DefaultTimeoutSeconds);

    public static ViolationCategoryOption CreateFallback(
        string ruleCode,
        string? policySet,
        string? action,
        double timeoutSeconds,
        string? reason,
        bool markUnavailable) =>
        new(
            string.Equals(
                policySet,
                PolicySetCode.Backseatgaming.ToString(),
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    ruleCode,
                    ModerationRuleCodes.Backseatgaming,
                    StringComparison.OrdinalIgnoreCase)
                    ? PolicySetCode.Backseatgaming.ToString()
                    : PolicySetCode.General.ToString(),
            ruleCode,
            markUnavailable
                && !string.Equals(
                    ruleCode,
                    ModerationRuleCodes.AutoModHeld,
                    StringComparison.OrdinalIgnoreCase)
                ? $"{GetFallbackDisplayName(ruleCode)} · nicht mehr aktiv"
                : GetFallbackDisplayName(ruleCode),
            string.IsNullOrWhiteSpace(reason)
                ? "Verstoß gegen die Chatregeln."
                : reason,
            action?.Trim().ToLowerInvariant() switch
            {
                "delete" => "delete",
                "ban" => "ban",
                _ => "timeout",
            },
            double.IsFinite(timeoutSeconds)
                && timeoutSeconds >= 1
                && timeoutSeconds <= int.MaxValue
                    ? checked((int)Math.Truncate(timeoutSeconds))
                    : ModerationActionRules.DefaultTimeoutSeconds);

    public static string GetFallbackDisplayName(string? ruleCode) =>
        ruleCode?.Trim().ToUpperInvariant() switch
        {
            "G_HARM" => "Gewalt, Drohungen & Selbstverletzung",
            "G_HATE" => "Hassrede & Diskriminierung",
            "G_SEXUAL_CONTENT" => "Sexuelle & sexistische Inhalte",
            "G_PERSONAL_ATTACK" => "Beleidigung & Belästigung",
            "LOCAL_BLACKLIST" => "Lokaler Blacklist-Treffer",
            "G_SEXUALIZATION" => "Sexualisierung",
            "G_SEXUAL_EXPLICIT" => "Explizit sexueller Inhalt",
            "G_INSULT" => "Beleidigung",
            "G_HARASSMENT" => "Belästigung / Mobbing",
            "G_RACISM" => "Rassismus",
            "G_SEXISM" => "Sexismus",
            "G_LGBTQ_HATE" => "Queerfeindlichkeit",
            "G_ABLEISM" => "Behindertenfeindlichkeit",
            "G_HATE_OTHER" => "Sonstige Hassrede",
            "G_THREAT" => "Direkte Drohung",
            "G_VIOLENCE" => "Gewaltverherrlichung",
            "G_PERSONAL_DATA" => "Persönliche Daten / Doxxing",
            "G_SELF_HARM_INCITEMENT" =>
                "Aufforderung zur Selbstverletzung",
            "G_SPAM" => "Spam",
            "G_SCAM" => "Betrug / Phishing",
            "G_DISRUPTION" => "Chatstörung",
            "G_ADVERTISING" => "Werbung",
            ModerationRuleCodes.SelfPromotion => "Eigenwerbung",
            ModerationRuleCodes.BotScam => "Bot / Scam",
            ModerationRuleCodes.AutoModHeld => "Twitch AutoMod",
            "G_BEGGING" => "Gebettel",
            "G_OTHER_ABUSE" => "Sonstiger General-Verstoß",
            "BACKSEATGAMING" => "Backseatgaming",
            { Length: > 0 } value => value,
            _ => "Kategorie wird geladen",
        };
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
