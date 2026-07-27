using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

    public int PendingCount => _state.ModerationCases.Count(item => item.Status == "Pending");

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
        ToggleAnalysisCommand.NotifyCanExecuteChanged();
    }

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
