using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.Infrastructure.Persistence;
using WinUI.TableView;

namespace TwitchAI.App.Views;

public sealed partial class PoliciesPage : Page
{
    private readonly IUniversalDatabaseEditor _databaseEditor;
    private readonly TableColumnWidthPersistenceController
        _columnWidthPersistence;

    public PoliciesPage()
    {
        ViewModel = App.Current.GetService<PoliciesViewModel>();
        _databaseEditor = App.Current.GetService<IUniversalDatabaseEditor>();
        InitializeComponent();
        _columnWidthPersistence =
            new TableColumnWidthPersistenceController(
                PhraseTable,
                static () => "policies.general",
                App.Current.GetService<ITableColumnLayoutStore>());
        _columnWidthPersistence.ApplyStoredWidths();
        UpdateSortIndicators();
    }

    public PoliciesViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectPolicySet("General");
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
        Grid.SetRow(AddPhraseButton, useCompactLayout ? 2 : 1);
        AddPhraseButton.HorizontalAlignment = useCompactLayout
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;

        TableCommandHint.Visibility = useCompactLayout
            ? Visibility.Collapsed
            : Visibility.Visible;
        CategoryFilterBox.MinWidth = useCompactLayout ? 0 : 250;
        CategoryFilterBox.HorizontalAlignment = useCompactLayout
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
        Grid.SetRow(CategoryFilterBox, 0);
        Grid.SetColumn(CategoryFilterBox, useCompactLayout ? 0 : 1);
        Grid.SetColumnSpan(CategoryFilterBox, useCompactLayout ? 3 : 1);
        Grid.SetRow(DeleteRowButton, useCompactLayout ? 1 : 0);
        Grid.SetColumn(DeleteRowButton, useCompactLayout ? 0 : 2);
        Grid.SetColumnSpan(DeleteRowButton, useCompactLayout ? 3 : 1);
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
            Title = "Archivierte Blacklist-Zeile wiederherstellen?",
            Content =
                $"„{candidate.NormalizedPhrase}“ existiert bereits archiviert als "
                + $"{candidate.RuleCode ?? "General-Regel"} mit der festen ID {candidate.Id}. "
                + $"Die bisherigen Lernzähler ({candidate.PositiveConfirmations} positiv / "
                + $"{candidate.NegativeConfirmations} negativ) bleiben erhalten. "
                + "Die Wiederherstellung gilt global für alle Twitch-Kanäle.",
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
            || e.Column.Tag is not string columnName)
        {
            return;
        }

        if (columnName == "Code")
        {
            if (item.SelectedRuleOption is not { } selectedRule
                || item.RuleId == selectedRule.RuleId)
            {
                return;
            }

            try
            {
                await _databaseEditor.SaveRowAsync(
                    "phrase_rules",
                    "phrase_id",
                    item.PhraseId,
                    new Dictionary<string, object?>
                    {
                        ["rule_id"] = selectedRule.RuleId,
                    });
                await ViewModel.LoadAsync();
            }
            catch (Exception exception)
            {
                await ViewModel.LoadAsync();
                await ShowDeleteErrorAsync(exception.Message);
            }

            return;
        }

        if (columnName is not ("Phrase" or "Override"))
        {
            return;
        }

        await ViewModel.UpdateEditableValuesAsync(item);
    }

    private void OnPhraseSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        ViewModel.SelectedPhrase =
            PhraseTable.SelectedItems.OfType<PolicyPhraseItem>().LastOrDefault();
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
            Title = $"{items.Length:N0} Blacklist-Zeile(n) löschen?",
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
