using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using TwitchAI.Infrastructure.AI;
using TwitchAI.Infrastructure.Pipeline;

namespace TwitchAI.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly IModerationCoordinator _coordinator;
    private readonly HashSet<ModerationCaseItem> _observedCases = [];

    public ShellViewModel(
        AppState state,
        IModerationCoordinator coordinator)
    {
        _state = state;
        _coordinator = coordinator;
        _state.PropertyChanged += (_, _) => RefreshFromState();
        _state.CurrentLocalAiRun.PropertyChanged +=
            OnCurrentLocalAiRunPropertyChanged;
        _state.ModerationCases.CollectionChanged += OnCasesCollectionChanged;
        foreach (var item in _state.ModerationCases)
        {
            ObserveCase(item);
        }
    }

    public string ChannelDisplayName => _state.ChannelDisplayName;

    public string CategorySummary => $"{_state.CategoryName} · {_state.ActivePolicies}";

    public string TwitchStatus => _state.TwitchStatus;

    public string EventSubStatus => _state.EventSubStatus;

    public string LmStudioStatus => _state.LmStudioStatus;

    public string DatabaseStatus => _state.DatabaseStatus;

    public bool IsAnalysisRunning => _state.IsAnalysisRunning;

    public string AnalysisLabel => IsAnalysisRunning ? "Analyse pausieren" : "Analyse starten";

    public bool CanToggleAnalysis =>
        _coordinator.IsRunning
        || IsAnalysisRunning
        || _state.IsTwitchConnectionReady;

    public string AnalysisToolTip => IsAnalysisRunning
        ? "Analyse pausieren"
        : _state.IsTwitchConnectionReady
            ? "Analyse starten; EventSub wird dabei verbindlich geprüft"
            : "Zuerst Twitch in den Einstellungen erfolgreich verbinden";

    public Visibility ReadOnlyModeVisibility =>
        _state.IsReadOnlyMode ? Visibility.Visible : Visibility.Collapsed;

    public int PendingCount => _state.ModerationCases.Count(item => item.Status == "Pending");

    public ObservableCollection<LocalAiRunItem> QueuedLocalAiRuns =>
        _state.QueuedLocalAiRuns;

    public string LocalAiRunStateLabel =>
        _state.CurrentLocalAiRun.IsPresent
            ? _state.CurrentLocalAiRun.State == LocalAiRunState.Failed
                && _state.IsAnalysisRunning
                    ? "Fehler · Analyse läuft weiter"
                    : _state.CurrentLocalAiRun.StateLabel
            : "Bereit";

    public string LocalAiRunBatchLabel =>
        _state.CurrentLocalAiRun.IsPresent
            ? $"{_state.CurrentLocalAiRun.RunLabel} · "
                + _state.CurrentLocalAiRun.MessageCountLabel
            : "Noch kein lokaler AI-Lauf";

    public string LocalAiRunTokenLabel =>
        _state.CurrentLocalAiRun.IsPresent
            ? _state.CurrentLocalAiRun.TokenLabel
            : "LM Studio lokal";

    public string LocalAiRunMetaLabel
    {
        get
        {
            var run = _state.CurrentLocalAiRun;
            if (!run.IsPresent)
            {
                return "Bereit für den nächsten Batch";
            }

            var model = string.IsNullOrWhiteSpace(run.ModelId)
                ? "Lokales Modell"
                : run.ModelId;
            return $"{run.DurationLabel} · {run.UpdatedAtLabel} · {model}";
        }
    }

    public bool IsLocalAiRunRunning =>
        _state.CurrentLocalAiRun.IsPresent
        && _state.CurrentLocalAiRun.State == LocalAiRunState.Running;

    public Visibility LocalAiRunRunningVisibility =>
        VisibilityForCurrentState(LocalAiRunState.Running);

    public Visibility LocalAiRunQueuedVisibility =>
        VisibilityForCurrentState(LocalAiRunState.Queued);

    public Visibility LocalAiRunSucceededVisibility =>
        VisibilityForCurrentState(LocalAiRunState.Succeeded);

    public Visibility LocalAiRunFailedVisibility =>
        VisibilityForCurrentState(LocalAiRunState.Failed);

    public Visibility LocalAiRunIdleVisibility =>
        _state.CurrentLocalAiRun.IsPresent
            ? Visibility.Collapsed
            : Visibility.Visible;

    public string LocalAiQueueLabel
    {
        get
        {
            var batchCount = _state.QueuedLocalAiRunCount;
            var messageCount = _state.QueuedLocalAiMessageCount;
            var batches = batchCount == 1
                ? "1 Batch"
                : $"{batchCount:N0} Batches";
            var messages = messageCount == 1
                ? "1 Nachricht"
                : $"{messageCount:N0} Nachrichten";
            return $"{batches} · {messages} warten";
        }
    }

    public Visibility LocalAiQueueVisibility =>
        _state.QueuedLocalAiRunCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string LocalAiQueueCompactLabel =>
        $"{_state.QueuedLocalAiRunCount:N0} wartet";

    [ObservableProperty]
    public partial string ActivePageTitle { get; set; } = "Moderationswarteschlange";

    [RelayCommand(CanExecute = nameof(CanToggleAnalysis))]
    private async Task ToggleAnalysisAsync()
    {
        try
        {
            if (_coordinator.IsRunning)
            {
                await _coordinator.StopAsync();
            }
            else
            {
                if (!_state.IsTwitchConnectionReady)
                {
                    _state.EventSubStatus =
                        "Analyse nicht gestartet · Bitte zuerst Twitch "
                        + "in den Einstellungen verbinden.";
                    return;
                }

                await _coordinator.StartAsync();
            }

            _state.IsAnalysisRunning = _coordinator.IsRunning;
        }
        catch (Exception exception)
        {
            _state.IsAnalysisRunning = false;
            _state.EventSubStatus =
                $"Analyse nicht gestartet · {exception.Message}";
        }

        OnPropertyChanged(nameof(IsAnalysisRunning));
        OnPropertyChanged(nameof(AnalysisLabel));
        OnPropertyChanged(nameof(CanToggleAnalysis));
        OnPropertyChanged(nameof(AnalysisToolTip));
        OnPropertyChanged(nameof(ReadOnlyModeVisibility));
        RefreshAiFooter();
        ToggleAnalysisCommand.NotifyCanExecuteChanged();
    }

    private void OnCurrentLocalAiRunPropertyChanged(
        object? sender,
        PropertyChangedEventArgs args) =>
        RefreshAiFooter();

    private void RefreshAiFooter()
    {
        OnPropertyChanged(nameof(LocalAiRunStateLabel));
        OnPropertyChanged(nameof(LocalAiRunBatchLabel));
        OnPropertyChanged(nameof(LocalAiRunTokenLabel));
        OnPropertyChanged(nameof(LocalAiRunMetaLabel));
        OnPropertyChanged(nameof(IsLocalAiRunRunning));
        OnPropertyChanged(nameof(LocalAiRunRunningVisibility));
        OnPropertyChanged(nameof(LocalAiRunQueuedVisibility));
        OnPropertyChanged(nameof(LocalAiRunSucceededVisibility));
        OnPropertyChanged(nameof(LocalAiRunFailedVisibility));
        OnPropertyChanged(nameof(LocalAiRunIdleVisibility));
        OnPropertyChanged(nameof(LocalAiQueueLabel));
        OnPropertyChanged(nameof(LocalAiQueueCompactLabel));
        OnPropertyChanged(nameof(LocalAiQueueVisibility));
    }

    private Visibility VisibilityForCurrentState(LocalAiRunState state) =>
        _state.CurrentLocalAiRun.IsPresent
        && _state.CurrentLocalAiRun.State == state
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void RefreshFromState()
    {
        OnPropertyChanged(nameof(ChannelDisplayName));
        OnPropertyChanged(nameof(CategorySummary));
        OnPropertyChanged(nameof(TwitchStatus));
        OnPropertyChanged(nameof(EventSubStatus));
        OnPropertyChanged(nameof(LmStudioStatus));
        OnPropertyChanged(nameof(DatabaseStatus));
        OnPropertyChanged(nameof(IsAnalysisRunning));
        OnPropertyChanged(nameof(AnalysisLabel));
        OnPropertyChanged(nameof(CanToggleAnalysis));
        OnPropertyChanged(nameof(AnalysisToolTip));
        OnPropertyChanged(nameof(ReadOnlyModeVisibility));
        RefreshAiFooter();
        ToggleAnalysisCommand.NotifyCanExecuteChanged();
    }

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

            foreach (var item in _state.ModerationCases)
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
}
