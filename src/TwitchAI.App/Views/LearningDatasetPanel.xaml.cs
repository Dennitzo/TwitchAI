using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using WinUI.TableView;

namespace TwitchAI.App.Views;

public sealed partial class LearningDatasetPanel : UserControl
{
    private TableColumnWidthPersistenceController? _columnWidthPersistence;
    private Task? _refreshTask;
    private bool _isConfigured;

    public LearningDatasetPanel()
    {
        ViewModel = App.Current.GetService<LearningDatasetViewModel>();
        InitializeComponent();
    }

    public LearningDatasetViewModel ViewModel { get; }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!_isConfigured)
        {
            return Task.CompletedTask;
        }

        if (_refreshTask is { IsCompleted: false })
        {
            return _refreshTask;
        }

        _refreshTask = RefreshCoreAsync(cancellationToken);
        return _refreshTask;
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        LearningTable.SelectedItems.Clear();
        await ViewModel.LoadAsync(cancellationToken);
        UpdateSortIndicators();
    }

    public void Configure(LearningDatasetViewMode mode)
    {
        if (_isConfigured)
        {
            ViewModel.Configure(mode);
            return;
        }

        ViewModel.Configure(mode);
        _isConfigured = true;

        if (mode == LearningDatasetViewMode.Allowed)
        {
            LearningTable.Columns.Remove(ActionColumn);
            LearningTable.Columns.Remove(TimeoutColumn);
        }

        var widthScope = mode switch
        {
            LearningDatasetViewMode.GeneralModeration => "learning.blacklist",
            LearningDatasetViewMode.BackseatgamingModeration =>
                "learning.backseatgaming",
            LearningDatasetViewMode.Allowed => "learning.whitelist",
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
        };
        _columnWidthPersistence =
            new TableColumnWidthPersistenceController(
                LearningTable,
                () => widthScope,
                App.Current.GetService<ITableColumnLayoutStore>());
        _columnWidthPersistence.ApplyStoredWidths();
        UpdateSortIndicators();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isConfigured || Visibility != Visibility.Visible)
        {
            return;
        }

        await RefreshAsync();
    }

    private async void OnSorting(
        object sender,
        TableViewSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column.Tag is not string columnName)
        {
            return;
        }

        LearningTable.SelectedItems.Clear();
        await ViewModel.SortByColumnAsync(columnName);
        UpdateSortIndicators();
    }

    private void UpdateSortIndicators()
    {
        foreach (var column in LearningTable.Columns)
        {
            column.SortDirection =
                column.Tag is string columnName
                && string.Equals(
                    columnName,
                    ViewModel.SortColumn,
                    StringComparison.Ordinal)
                    ? ViewModel.SortDescending
                        ? SortDirection.Descending
                        : SortDirection.Ascending
                    : null;
        }
    }

    private async void OnCellEditEnded(
        object sender,
        TableViewCellEditEndedEventArgs e)
    {
        if (e.EditAction != TableViewEditAction.Commit
            || e.DataItem is not LearningDatasetItem item
            || e.Column.Tag is not string columnName
            || columnName is not (
                "OriginalText"
                or "RuleCode"
                or "Action"
                or "TimeoutSeconds"
                or "Reason"))
        {
            return;
        }

        await ViewModel.SaveAsync(item);
        LearningTable.SelectedItems.Clear();
    }

    private void OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        ViewModel.SetSelectedCount(
            LearningTable.SelectedItems
                .OfType<LearningDatasetItem>()
                .Count());
    }

    private void OnSearchTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        LearningTable.SelectedItems.Clear();
    }

    private async void OnRemoveSelectedClicked(
        object sender,
        RoutedEventArgs e)
    {
        var items = LearningTable.SelectedItems
            .OfType<LearningDatasetItem>()
            .DistinctBy(item => item.Source)
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = items.Length == 1
                ? "Learning-Beispiel entfernen?"
                : $"{items.Length:N0} Learning-Beispiele entfernen?",
            Content =
                "Die Auswahl wird nicht mehr als Kontext an die lokale AI gesendet. "
                + "Historische Twitch-Aktionen und Ausführungslogs bleiben erhalten.",
            PrimaryButtonText = "Aus Learning entfernen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await ViewModel.RemoveAsync(items);
        LearningTable.SelectedItems.Clear();
    }

    private async void OnPreviousPageClicked(object sender, RoutedEventArgs e)
    {
        LearningTable.SelectedItems.Clear();
        await ViewModel.PreviousPageAsync();
    }

    private async void OnNextPageClicked(object sender, RoutedEventArgs e)
    {
        LearningTable.SelectedItems.Clear();
        await ViewModel.NextPageAsync();
    }
}
