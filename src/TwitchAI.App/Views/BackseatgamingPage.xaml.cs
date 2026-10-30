using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.Infrastructure.Persistence;
using WinUI.TableView;

namespace TwitchAI.App.Views;

public sealed partial class BackseatgamingPage : Page
{
    private readonly IUniversalDatabaseEditor _databaseEditor;
    private readonly TableColumnWidthPersistenceController
        _columnWidthPersistence;

    public BackseatgamingPage()
    {
        ViewModel = App.Current.GetService<PoliciesViewModel>();
        _databaseEditor = App.Current.GetService<IUniversalDatabaseEditor>();
        InitializeComponent();
        _columnWidthPersistence =
            new TableColumnWidthPersistenceController(
                PhraseTable,
                static () => "policies.backseatgaming",
                App.Current.GetService<ITableColumnLayoutStore>());
        _columnWidthPersistence.ApplyStoredWidths();
        UpdateSortIndicators();
    }

    public PoliciesViewModel ViewModel { get; }

    private async void OnViewSelectorSelectionChanged(
        SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        var showLearning = false;
        PageInfoBar.Title = showLearning
            ? "Bestätigtes Backseatgaming-Learning"
            : "Kategorieabhängiges Regelset";
        PageInfoBar.Message = showLearning
            ? "Diese bestätigten Beispiele werden nur bei aktivem Backseatgaming als AI-Kontext verwendet."
            : "Wenn Backseatgaming in den Einstellungen aktiviert ist, gilt das Regelset für bekannte Twitch-Kategorien außer Just Chatting und IRL.";
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectPolicySet("Backseatgaming");
        UpdateSortIndicators();
        await ViewModel.LoadAsync();
        UpdateSortIndicators();
    }

    private void OnPhraseTableSorting(
        object sender,
        TableViewSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column.Tag is not string columnName)
        {
            return;
        }

        ViewModel.SortByColumn(columnName);
        UpdateSortIndicators();
    }

    private void UpdateSortIndicators()
    {
        foreach (var column in PhraseTable.Columns)
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

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout(e.NewSize.Width);
    }

    private void UpdateResponsiveLayout(double availableWidth)
    {
        var useCompactLayout = availableWidth < 760;
        PageContent.Margin = useCompactLayout
            ? new Thickness(72, 20, 16, 40)
            : new Thickness(28, 24, 28, 40);

        PhraseFormPrimaryColumn.Width = new GridLength(1, GridUnitType.Star);
        PhraseFormActionColumn.Width = useCompactLayout
            ? new GridLength(0)
            : GridLength.Auto;

        Grid.SetColumn(AddPhraseButton, useCompactLayout ? 0 : 1);
        Grid.SetRow(AddPhraseButton, useCompactLayout ? 1 : 0);
        AddPhraseButton.HorizontalAlignment = useCompactLayout
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;

        TableCommandHint.Visibility = useCompactLayout
            ? Visibility.Collapsed
            : Visibility.Visible;
        Grid.SetColumn(DeleteRowButton, useCompactLayout ? 0 : 1);
        Grid.SetColumnSpan(DeleteRowButton, useCompactLayout ? 2 : 1);
        DeleteRowButton.HorizontalAlignment = useCompactLayout
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
    }

    private async void OnAddPhraseClicked(object sender, RoutedEventArgs e)
    {
        var candidate = await ViewModel.AddPhraseAsync();
        if (candidate is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Archivierte Backseatgaming-Zeile wiederherstellen?",
            Content =
                $"„{candidate.NormalizedPhrase}“ existiert bereits archiviert mit der "
                + $"festen ID {candidate.Id}. Die bisherigen Lernzähler "
                + $"({candidate.PositiveConfirmations} positiv / "
                + $"{candidate.NegativeConfirmations} negativ) bleiben erhalten. "
                + "Die Wiederherstellung gilt global für alle Twitch-Kanäle; "
                + "Just Chatting und IRL bleiben weiterhin ausgenommen.",
            PrimaryButtonText = "Wiederherstellen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (!await ViewModel.RestorePhraseAsync(candidate))
        {
            await ShowDeleteErrorAsync(ViewModel.StatusMessage);
        }
    }

    private async void OnPhraseCellEditEnded(
        object sender,
        TableViewCellEditEndedEventArgs e)
    {
        if (e.EditAction != TableViewEditAction.Commit
            || e.DataItem is not PolicyPhraseItem item
            || e.Column.Tag is not string columnName
            || columnName is not ("Phrase" or "Override"))
        {
            return;
        }

        await ViewModel.UpdateEditableValuesAsync(item);
    }

    private async void PhraseEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch
            {
                Tag: PolicyPhraseItem item,
                IsOn: var isEnabled,
            })
        {
            return;
        }

        await ViewModel.SetEnabledAsync(item, isEnabled);
    }

    private async void OnDeleteSelectedClicked(
        object sender,
        RoutedEventArgs e)
    {
        var items = PhraseTable.SelectedItems.OfType<PolicyPhraseItem>().ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{items.Length:N0} Backseatgaming-Zeile(n) löschen?",
            Content = "Die ausgewählten Zeilen und ihre abhängigen Daten werden sofort gelöscht.",
            PrimaryButtonText = "Zeile löschen",
            CloseButtonText = "Abbrechen",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await _databaseEditor.DeleteRowsAsync(
                "phrase_rules",
                "phrase_id",
                items.Select(item => (object)item.PhraseId).ToArray());
            await ViewModel.LoadAsync();
        }
        catch (Exception exception)
        {
            await ShowDeleteErrorAsync(exception.Message);
        }
    }

    private void OnPhraseSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        ViewModel.SelectedPhrase =
            PhraseTable.SelectedItems.OfType<PolicyPhraseItem>().LastOrDefault();
    }

    private void OnTableSearchTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        PhraseTable.SelectedItems.Clear();
        ViewModel.SelectedPhrase = null;
    }

    private async Task ShowDeleteErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Zeile konnte nicht gelöscht werden",
            Content = message,
            CloseButtonText = "Schließen",
        };
        await dialog.ShowAsync();
    }
}
