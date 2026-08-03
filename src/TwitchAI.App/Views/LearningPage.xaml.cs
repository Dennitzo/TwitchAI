using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TwitchAI.App.ViewModels;

namespace TwitchAI.App.Views;

public sealed partial class LearningPage : Page
{
    public LearningPage()
    {
        ViewModel = App.Current.GetService<LearningViewModel>();
        InitializeComponent();
    }

    public LearningViewModel ViewModel { get; }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PageContent.Margin = e.NewSize.Width < 760
            ? new Thickness(72, 20, 16, 40)
            : new Thickness(28, 24, 28, 40);

        if (e.NewSize.Width >= 680)
        {
            InputGrid.ColumnDefinitions[0].Width =
                new GridLength(1, GridUnitType.Star);
            InputGrid.ColumnDefinitions[1].Width = GridLength.Auto;
            Grid.SetColumn(StartTestButton, 1);
            Grid.SetColumnSpan(StartTestButton, 1);
            Grid.SetRow(StartTestButton, 1);
            return;
        }

        InputGrid.ColumnDefinitions[0].Width =
            new GridLength(1, GridUnitType.Star);
        InputGrid.ColumnDefinitions[1].Width = new GridLength(0);
        Grid.SetColumn(StartTestButton, 0);
        Grid.SetColumnSpan(StartTestButton, 2);
        Grid.SetRow(StartTestButton, 2);
        StartTestButton.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void OnReviewGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid reviewGrid
            || reviewGrid.ColumnDefinitions.Count < 5)
        {
            return;
        }

        if (e.NewSize.Width >= 1120)
        {
            SetReviewColumnWidths(
                reviewGrid,
                new GridLength(160),
                GridLength.Auto,
                new GridLength(230),
                new GridLength(1, GridUnitType.Star),
                new GridLength(280));
            PlaceReviewElement(ActionEditor, row: 1, column: 0);
            PlaceReviewElement(TimeoutEditor, row: 1, column: 1);
            PlaceReviewElement(ViolationEditor, row: 1, column: 2);
            PlaceReviewElement(ReasonEditor, row: 1, column: 3);
            PlaceReviewElement(ActionButtons, row: 1, column: 4);
            return;
        }

        if (e.NewSize.Width >= 700)
        {
            SetReviewColumnWidths(
                reviewGrid,
                new GridLength(1, GridUnitType.Star),
                GridLength.Auto,
                new GridLength(1, GridUnitType.Star),
                new GridLength(0),
                new GridLength(0));
            PlaceReviewElement(ActionEditor, row: 1, column: 0);
            PlaceReviewElement(TimeoutEditor, row: 1, column: 1);
            PlaceReviewElement(ViolationEditor, row: 1, column: 2);
            PlaceReviewElement(ReasonEditor, row: 2, column: 0, columnSpan: 2);
            PlaceReviewElement(ActionButtons, row: 2, column: 2);
            return;
        }

        SetReviewColumnWidths(
            reviewGrid,
            new GridLength(1, GridUnitType.Star),
            new GridLength(0),
            new GridLength(0),
            new GridLength(0),
            new GridLength(0));
        PlaceReviewElement(ActionEditor, row: 1, column: 0);
        PlaceReviewElement(TimeoutEditor, row: 2, column: 0);
        PlaceReviewElement(ViolationEditor, row: 3, column: 0);
        PlaceReviewElement(ReasonEditor, row: 4, column: 0);
        PlaceReviewElement(ActionButtons, row: 5, column: 0);
    }

    private async void OnViolationCategorySelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox
            {
                SelectedValue: string selectedRuleCode,
            }
            || ViewModel.Review is not { } review)
        {
            // Review replacement can briefly clear the native selector. The
            // immutable review model keeps the canonical rule code intact.
            return;
        }

        try
        {
            if (!await ViewModel.SelectRuleAsync(selectedRuleCode))
            {
                ((ComboBox)sender).SelectedValue = review.SelectedRuleCode;
            }
        }
        catch (Exception exception)
        {
            ViewModel.StatusMessage =
                "Die Begründung konnte nicht neu erzeugt werden; bitte erneut versuchen oder konkret eingeben.";
            await ShowErrorAsync(
                exception.Message,
                "Begründung nicht möglich");
        }
    }

    private async void OnStartTestClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.AnalyzeAsync();
        }
        catch (Exception exception)
        {
            ViewModel.StatusMessage = "AI-Test konnte nicht abgeschlossen werden.";
            await ShowErrorAsync(exception.Message, "Lerntest nicht möglich");
        }
    }

    private async void OnAllowClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await ViewModel.MarkAllowedAsync();
        }
        catch (Exception exception)
        {
            ViewModel.StatusMessage = "Learning konnte nicht gespeichert werden.";
            await ShowErrorAsync(exception.Message, "Learning nicht möglich");
        }
    }

    private void OnDiscardClicked(object sender, RoutedEventArgs e) =>
        ViewModel.Abort();

    private static void SetReviewColumnWidths(
        Grid reviewGrid,
        params GridLength[] widths)
    {
        for (var index = 0; index < widths.Length; index++)
        {
            reviewGrid.ColumnDefinitions[index].Width = widths[index];
        }
    }

    private static void PlaceReviewElement(
        FrameworkElement element,
        int row,
        int column,
        int columnSpan = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    private async Task ShowErrorAsync(string message, string title)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "Schließen",
        };
        await dialog.ShowAsync();
    }
}
