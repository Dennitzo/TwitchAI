using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using TwitchAI.Infrastructure.AI;

namespace TwitchAI.App.ViewModels;

public sealed partial class LocalAiRunItem : ObservableObject
{
    public LocalAiRunItem()
    {
    }

    public LocalAiRunItem(LocalAiRunActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Apply(activity);
    }

    [ObservableProperty]
    public partial string CorrelationId { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial DateTimeOffset FirstObservedAt { get; private set; }

    [ObservableProperty]
    public partial bool IsPresent { get; private set; }

    public Visibility ItemVisibility =>
        IsPresent ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial LocalAiRunState State { get; private set; }

    [ObservableProperty]
    public partial LocalAiRunKind RunKind { get; private set; }

    [ObservableProperty]
    public partial int MessageCount { get; private set; }

    [ObservableProperty]
    public partial string ModelId { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int? PromptTokens { get; private set; }

    [ObservableProperty]
    public partial int? CompletionTokens { get; private set; }

    [ObservableProperty]
    public partial int? TotalTokens { get; private set; }

    [ObservableProperty]
    public partial long? ElapsedMilliseconds { get; private set; }

    [ObservableProperty]
    public partial DateTimeOffset UpdatedAt { get; private set; }

    [ObservableProperty]
    public partial string ErrorCode { get; private set; } = string.Empty;

    public string RunLabel
    {
        get
        {
            if (string.IsNullOrEmpty(CorrelationId))
            {
                return string.Empty;
            }

            if (RunKind == LocalAiRunKind.LearningTest)
            {
                return "Lerntest";
            }

            if (RunKind == LocalAiRunKind.ReviewReason)
            {
                return "Review-Begründung";
            }

            var repairSuffix = CorrelationId.EndsWith(
                "-repair",
                StringComparison.Ordinal)
                ? " · Reparatur"
                : string.Empty;
            var separator = CorrelationId.IndexOf('-', StringComparison.Ordinal);
            var shortId = separator > 0
                ? CorrelationId[..separator]
                : CorrelationId[..Math.Min(8, CorrelationId.Length)];
            return $"Batch {shortId}{repairSuffix}";
        }
    }

    public string StateLabel => State switch
    {
        LocalAiRunState.Queued => "Wartet",
        LocalAiRunState.Running => "Arbeitet",
        LocalAiRunState.Succeeded => "Erfolgreich",
        LocalAiRunState.Superseded => "Ersetzt",
        _ => ErrorCode == "cancelled" ? "Abgebrochen" : "Fehler",
    };

    public string MessageCountLabel =>
        MessageCount == 1 ? "1 Nachricht" : $"{MessageCount:N0} Nachrichten";

    public string TokenLabel
    {
        get
        {
            if (TotalTokens is { } total)
            {
                var prompt = PromptTokens?.ToString("N0", CultureInfo.CurrentCulture) ?? "–";
                var completion = CompletionTokens?.ToString(
                    "N0",
                    CultureInfo.CurrentCulture) ?? "–";
                return $"{total:N0} Token · {prompt} Eingabe · {completion} Ausgabe";
            }

            return State switch
            {
                LocalAiRunState.Queued => "Noch kein Tokenverbrauch",
                LocalAiRunState.Running => "Token werden nach Abschluss gemeldet",
                _ => "Keine Tokenwerte verfügbar",
            };
        }
    }

    public string DurationLabel => ElapsedMilliseconds is { } elapsed
        ? elapsed < 1_000
            ? $"{elapsed:N0} ms"
            : $"{elapsed / 1_000d:N1} s"
        : "Laufzeit offen";

    public string UpdatedAtLabel =>
        UpdatedAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    public Visibility QueuedVisibility =>
        State == LocalAiRunState.Queued ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RunningVisibility =>
        State == LocalAiRunState.Running ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SucceededVisibility =>
        State == LocalAiRunState.Succeeded ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FailedVisibility =>
        State == LocalAiRunState.Failed ? Visibility.Visible : Visibility.Collapsed;

    public void Apply(LocalAiRunActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        if (!string.Equals(
            CorrelationId,
            activity.CorrelationId,
            StringComparison.Ordinal))
        {
            CorrelationId = activity.CorrelationId;
            FirstObservedAt = activity.OccurredAtUtc;
        }

        State = activity.State;
        RunKind = activity.RunKind;
        MessageCount = activity.MessageCount;
        ModelId = activity.ModelId ?? string.Empty;
        PromptTokens = activity.PromptTokens;
        CompletionTokens = activity.CompletionTokens;
        TotalTokens = activity.TotalTokens;
        ElapsedMilliseconds = activity.ElapsedMilliseconds;
        UpdatedAt = activity.OccurredAtUtc;
        ErrorCode = activity.ErrorCode ?? string.Empty;
        IsPresent = true;
    }

    public void Clear() => IsPresent = false;

    partial void OnCorrelationIdChanged(string value) =>
        OnPropertyChanged(nameof(RunLabel));

    partial void OnIsPresentChanged(bool value) =>
        OnPropertyChanged(nameof(ItemVisibility));

    partial void OnStateChanged(LocalAiRunState value)
    {
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(TokenLabel));
        OnPropertyChanged(nameof(QueuedVisibility));
        OnPropertyChanged(nameof(RunningVisibility));
        OnPropertyChanged(nameof(SucceededVisibility));
        OnPropertyChanged(nameof(FailedVisibility));
    }

    partial void OnRunKindChanged(LocalAiRunKind value) =>
        OnPropertyChanged(nameof(RunLabel));

    partial void OnMessageCountChanged(int value) =>
        OnPropertyChanged(nameof(MessageCountLabel));

    partial void OnPromptTokensChanged(int? value) =>
        OnPropertyChanged(nameof(TokenLabel));

    partial void OnCompletionTokensChanged(int? value) =>
        OnPropertyChanged(nameof(TokenLabel));

    partial void OnTotalTokensChanged(int? value) =>
        OnPropertyChanged(nameof(TokenLabel));

    partial void OnElapsedMillisecondsChanged(long? value) =>
        OnPropertyChanged(nameof(DurationLabel));

    partial void OnUpdatedAtChanged(DateTimeOffset value) =>
        OnPropertyChanged(nameof(UpdatedAtLabel));

    partial void OnErrorCodeChanged(string value) =>
        OnPropertyChanged(nameof(StateLabel));
}
