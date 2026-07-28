using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TwitchAI.App.Services;
using TwitchAI.App.ViewModels;
using TwitchAI.Infrastructure.Persistence;
using WinUI.TableView;

namespace TwitchAI.App.Views;

public sealed partial class ChatPage : Page
{
    private readonly IUniversalDatabaseEditor _databaseEditor;
    private readonly TableColumnWidthPersistenceController
        _columnWidthPersistence;

    public ChatPage()
    {
        ViewModel = App.Current.GetService<ChatViewModel>();
        _databaseEditor = App.Current.GetService<IUniversalDatabaseEditor>();
        InitializeComponent();
        _columnWidthPersistence =
            new TableColumnWidthPersistenceController(
                TemplateTable,
                static () => "chat",
                App.Current.GetService<ITableColumnLayoutStore>());
        _columnWidthPersistence.ApplyStoredWidths();
        UpdateSortIndicators();
    }

    public ChatViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
        UpdateSortIndicators();
    }

    private void OnTemplateTableSorting(
        object sender,
        TableViewSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column.Tag is not string columnName)
        {
            return;
        }

        ViewModel.SortByColumn(columnName);
        TemplateTable.SelectedItems.Clear();
        ViewModel.SelectedCount = 0;
        UpdateSortIndicators();
    }

    private void UpdateSortIndicators()
    {
        foreach (var column in TemplateTable.Columns)
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
        var compact = e.NewSize.Width < 840;
        PageContent.Margin = compact
            ? new Thickness(72, 20, 16, 40)
            : new Thickness(28, 24, 28, 40);

        ChatMessageFormColumn.Width =
            new GridLength(1, GridUnitType.Star);
        ResponseMessageFormColumn.Width = compact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        TemplateFormActionColumn.Width = compact
            ? new GridLength(0)
            : GridLength.Auto;

        Grid.SetColumn(NewResponseMessageInput, compact ? 0 : 1);
        Grid.SetRow(NewResponseMessageInput, compact ? 1 : 0);
        Grid.SetColumn(AddTemplateButton, compact ? 0 : 2);
        Grid.SetRow(AddTemplateButton, compact ? 2 : 0);
        AddTemplateButton.HorizontalAlignment = compact
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;

        TableCommandHint.Visibility = compact
            ? Visibility.Collapsed
            : Visibility.Visible;
        Grid.SetColumn(DeleteRowButton, compact ? 0 : 1);
        Grid.SetColumnSpan(DeleteRowButton, compact ? 2 : 1);
        DeleteRowButton.HorizontalAlignment = compact
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
    }

    private async void OnAddTemplateClicked(
        object sender,
        RoutedEventArgs e)
    {
        await ViewModel.AddTemplateAsync();
        TemplateTable.SelectedItems.Clear();
        ViewModel.SelectedCount = 0;
    }

    private async void OnTemplateCellEditEnded(
        object sender,
        TableViewCellEditEndedEventArgs e)
    {
        if (e.EditAction != TableViewEditAction.Commit
            || e.DataItem is not ChatReplyTemplateItem item
            || e.Column.Tag is not ("ChatMessage" or "ResponseMessage"))
        {
            return;
        }

        await ViewModel.UpdateTemplateAsync(item);
    }

    private async void TemplateEnabled_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch
            {
                Tag: ChatReplyTemplateItem item,
                IsOn: var isEnabled,
            })
        {
            return;
        }

        await ViewModel.SetEnabledAsync(item, isEnabled);
    }

    private void OnTemplateSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        ViewModel.SelectedCount =
            TemplateTable.SelectedItems.OfType<ChatReplyTemplateItem>().Count();
    }

    private void OnTableSearchTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs e)
    {
        if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        TemplateTable.SelectedItems.Clear();
        ViewModel.SelectedCount = 0;
    }

    private async void OnDeleteSelectedClicked(
        object sender,
        RoutedEventArgs e)
    {
        var items = TemplateTable.SelectedItems
            .OfType<ChatReplyTemplateItem>()
            .ToArray();
        if (items.Length == 0)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{items.Length:N0} Chat-Zeile(n) löschen?",
            Content =
                "Die ausgewählten Chat-Vorlagen und ihre abhängigen Daten werden sofort gelöscht.",
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
                "chat_reply_templates",
                "template_id",
                items.Select(item => (object)item.TemplateId).ToArray());
            TemplateTable.SelectedItems.Clear();
            ViewModel.SelectedCount = 0;
            await ViewModel.LoadAsync();
        }
        catch (Exception exception)
        {
            var errorDialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Zeile konnte nicht gelöscht werden",
                Content = exception.Message,
                CloseButtonText = "Schließen",
            };
            await errorDialog.ShowAsync();
        }
    }
}
