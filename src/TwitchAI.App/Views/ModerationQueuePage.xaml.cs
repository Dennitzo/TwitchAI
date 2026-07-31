using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TwitchAI.App.ViewModels;
using TwitchAI.Core.Models;

namespace TwitchAI.App.Views;

public sealed partial class ModerationQueuePage : Page
{
    public ModerationQueuePage()
    {
        ViewModel = App.Current.GetService<ModerationQueueViewModel>();
        InitializeComponent();
    }

    public ModerationQueueViewModel ViewModel { get; }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        // Attach only while this page is in the visual tree so case updates
        // cannot call back into a page that is being removed by navigation.
        CasesRepeater.ItemsSource = ViewModel.Cases;
        ViewModel.Cases.CollectionChanged -= OnCasesChanged;
        ViewModel.Cases.CollectionChanged += OnCasesChanged;
        UpdateEmptyState();
        await LoadViolationRulesAsync(ViewModel.Cases.ToArray());
    }

    private async void OnCasesChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
        if (e.NewItems is not null)
        {
            await LoadViolationRulesAsync(
                e.NewItems.OfType<ModerationCaseItem>().ToArray());
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Cases.CollectionChanged -= OnCasesChanged;
        CasesRepeater.ItemsSource = null;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PageContent.Margin = e.NewSize.Width < 760
            ? new Thickness(72, 20, 16, 40)
            : new Thickness(28, 24, 28, 40);
    }

    private void OnIdentityHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid identityHeader
            || identityHeader.ColumnDefinitions.Count < 2)
        {
            return;
        }

        Border? status = null;
        foreach (var child in identityHeader.Children)
        {
            if (child is Border border)
            {
                status = border;
                break;
            }
        }

        if (status is null)
        {
            return;
        }

        var useStackedLayout = e.NewSize.Width < 480;
        identityHeader.ColumnDefinitions[1].Width = useStackedLayout
            ? new GridLength(0)
            : GridLength.Auto;
        Grid.SetColumn(status, useStackedLayout ? 0 : 1);
        Grid.SetRow(status, useStackedLayout ? 1 : 0);
        status.HorizontalAlignment = useStackedLayout
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
    }

    private void OnReviewGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid reviewGrid
            || reviewGrid.ColumnDefinitions.Count < 5)
        {
            return;
        }

        var actionEditor = FindNamedChild<ComboBox>(
            reviewGrid,
            "ActionEditor");
        var timeoutEditor = FindNamedChild<NumberBox>(
            reviewGrid,
            "TimeoutEditor");
        var violationEditor = FindNamedChild<ComboBox>(
            reviewGrid,
            "ViolationEditor");
        var reasonEditor = FindNamedChild<TextBox>(
            reviewGrid,
            "ReasonEditor");
        var chatReplyEditor = FindNamedChild<TextBox>(
            reviewGrid,
            "ChatReplyEditor");
        var actionButtons = FindNamedChild<StackPanel>(
            reviewGrid,
            "ActionButtons");
        if (actionEditor is null
            || timeoutEditor is null
            || violationEditor is null
            || reasonEditor is null
            || chatReplyEditor is null
            || actionButtons is null)
        {
            return;
        }

        if (reviewGrid.DataContext is ModerationCaseItem { IsChatReply: true })
        {
            if (e.NewSize.Width >= 700)
            {
                SetReviewColumnWidths(
                    reviewGrid,
                    new GridLength(1, GridUnitType.Star),
                    new GridLength(0),
                    new GridLength(0),
                    new GridLength(0),
                    new GridLength(230));
                PlaceReviewElement(chatReplyEditor, row: 1, column: 0, columnSpan: 4);
                PlaceReviewElement(actionButtons, row: 1, column: 4);
            }
            else
            {
                SetReviewColumnWidths(
                    reviewGrid,
                    new GridLength(1, GridUnitType.Star),
                    new GridLength(0),
                    new GridLength(0),
                    new GridLength(0),
                    new GridLength(0));
                PlaceReviewElement(chatReplyEditor, row: 1, column: 0);
                PlaceReviewElement(actionButtons, row: 2, column: 0);
            }

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
                new GridLength(230));
            PlaceReviewElement(actionEditor, row: 1, column: 0);
            PlaceReviewElement(timeoutEditor, row: 1, column: 1);
            PlaceReviewElement(violationEditor, row: 1, column: 2);
            PlaceReviewElement(reasonEditor, row: 1, column: 3);
            PlaceReviewElement(actionButtons, row: 1, column: 4);
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
            PlaceReviewElement(actionEditor, row: 1, column: 0);
            PlaceReviewElement(timeoutEditor, row: 1, column: 1);
            PlaceReviewElement(violationEditor, row: 1, column: 2);
            PlaceReviewElement(reasonEditor, row: 2, column: 0, columnSpan: 2);
            PlaceReviewElement(actionButtons, row: 2, column: 2);
            return;
        }

        SetReviewColumnWidths(
            reviewGrid,
            new GridLength(1, GridUnitType.Star),
            new GridLength(0),
            new GridLength(0),
            new GridLength(0),
            new GridLength(0));
        PlaceReviewElement(actionEditor, row: 1, column: 0);
        PlaceReviewElement(timeoutEditor, row: 2, column: 0);
        PlaceReviewElement(violationEditor, row: 3, column: 0);
        PlaceReviewElement(reasonEditor, row: 4, column: 0);
        PlaceReviewElement(actionButtons, row: 5, column: 0);
    }

    private void OnViolationCategorySelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox
            {
                Tag: ModerationCaseItem item,
                SelectedValue: string selectedRuleCode,
            })
        {
            // A recycled ComboBox can temporarily clear its selection while
            // WinUI prepares the next card. Selection is display state only;
            // the validated rule code remains authoritative in the item.
            return;
        }

        if (!item.TrySelectViolationRule(selectedRuleCode))
        {
            ((ComboBox)sender).SelectedValue = item.RuleCode;
        }
    }

    private static T? FindNamedChild<T>(Grid root, string name)
        where T : FrameworkElement =>
        root.Children
            .OfType<T>()
            .FirstOrDefault(
                child => string.Equals(
                    child.Name,
                    name,
                    StringComparison.Ordinal));

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

    private void UpdateEmptyState()
    {
        var hasCases = ViewModel.Cases.Count > 0;
        EmptyState.Visibility = hasCases ? Visibility.Collapsed : Visibility.Visible;
        CasesRepeater.Visibility = hasCases ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadViolationRulesAsync(
        ModerationCaseItem[] items)
    {
        if (items.Length == 0)
        {
            return;
        }

        try
        {
            await ViewModel.LoadViolationRulesAsync(items);
        }
        catch (Exception exception)
        {
            foreach (var item in items)
            {
                item.AvailabilityLabel =
                    "Verstoßkategorien konnten nicht geladen werden";
            }

            await ShowErrorAsync(
                exception.Message,
                "Verstoßkategorien nicht verfügbar");
        }
    }

    private async void OnExecuteClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModerationCaseItem item })
        {
            return;
        }

        try
        {
            if (item.IsChatReply)
            {
                var chatResult = await ViewModel.ExecuteChatReplyAsync(item);
                if (chatResult.Outcome !=
                    TwitchAI.Infrastructure.Twitch.ChatReplyExecutionOutcome.Succeeded)
                {
                    await ShowErrorAsync(
                        chatResult.Message,
                        chatResult.Outcome ==
                        TwitchAI.Infrastructure.Twitch.ChatReplyExecutionOutcome.OutcomeUnknown
                            ? "Ergebnis nicht sicher"
                            : "Chat-Antwort nicht gesendet");
                }

                return;
            }

            var request = ModerationQueueViewModel.CreateExecutionRequest(item);
            if (item.Status == "Extern erledigt · Lernentscheidung offen")
            {
                await ShowUnavailableReviewAsync(item, request);
                return;
            }

            var result = await ViewModel.ExecuteAsync(item, request);
            if (result.Outcome == ModerationExecutionOutcome.AlreadyUnavailable)
            {
                await ShowUnavailableReviewAsync(item, request);
            }
            else if (result.Outcome != ModerationExecutionOutcome.Succeeded)
            {
                await ShowResultAsync(result);
            }
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception.Message);
        }
    }

    private async Task ShowUnavailableReviewAsync(
        ModerationCaseItem item,
        ModerationExecutionRequest request)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Nachricht bereits moderiert",
            Content =
                "Die Nachricht wurde bereits moderiert oder ist nicht mehr sicher verfügbar. "
                + "Die Twitch-Aktion wurde nicht ausgeführt. Das Fallbeispiel kann auch "
                + "ohne ausgewählte Triggerphrase bestätigt werden.",
            PrimaryButtonText = "Als bestätigt lernen",
            SecondaryButtonText = "Ohne Lernen verwerfen",
            CloseButtonText = "Zurück",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None)
        {
            return;
        }

        var result = await ViewModel.CompleteUnavailableAsync(
            item,
            request,
            learn: choice == ContentDialogResult.Primary);
        if (result.Outcome != ModerationExecutionOutcome.AlreadyUnavailable)
        {
            await ShowResultAsync(result);
        }
    }

    private async void OnDiscardClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModerationCaseItem item })
        {
            return;
        }

        if (item.IsChatReply)
        {
            try
            {
                var result = await ViewModel.DiscardChatReplyAsync(item);
                if (result.Outcome !=
                    TwitchAI.Infrastructure.Twitch.ChatReplyExecutionOutcome.Succeeded)
                {
                    await ShowErrorAsync(
                        result.Message,
                        "Chat-Antwort konnte nicht verworfen werden");
                }
            }
            catch (Exception exception)
            {
                await ShowErrorAsync(exception.Message);
            }

            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Vorschlag verwerfen",
            Content =
                "„Als erlaubt markieren“ speichert den Vorschlag als Fehlalarm "
                + "für das lokale Lernen.\n\n"
                + "„Nur Vorschlag abbrechen“ entfernt ihn ohne Twitch-Aktion, "
                + "ohne Lernsignal und ohne Whitelist-Eintrag.",
            PrimaryButtonText = "Als erlaubt markieren",
            SecondaryButtonText = "Nur Vorschlag abbrechen",
            CloseButtonText = "Zurück",
            DefaultButton = ContentDialogButton.Close,
        };
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.None)
        {
            return;
        }

        try
        {
            var learnAsAllowed = choice == ContentDialogResult.Primary;
            var result = await ViewModel.DiscardAsync(
                item,
                learnAsAllowed
                    ? "Als erlaubt markiert"
                    : "Testvorschlag abgebrochen",
                learnAsAllowed);
            if (result.Outcome != ModerationExecutionOutcome.Succeeded)
            {
                await ShowResultAsync(result);
            }
        }
        catch (Exception exception)
        {
            await ShowErrorAsync(exception.Message);
        }
    }

    private Task ShowResultAsync(ModerationExecutionResult result)
    {
        var title = result.Outcome switch
        {
            ModerationExecutionOutcome.OutcomeUnknown => "Ergebnis nicht sicher",
            ModerationExecutionOutcome.IntegrityBlocked => "Integritätsprüfung blockiert",
            ModerationExecutionOutcome.StalePolicy => "Regelstand geändert",
            _ => "Aktion nicht ausgeführt",
        };
        return ShowErrorAsync(
            result.Outcome == ModerationExecutionOutcome.OutcomeUnknown
                ? $"{result.Message}\n\nNicht erneut ausführen: Die Anfrage könnte Twitch erreicht haben."
                : result.Message,
            title);
    }

    private async Task ShowErrorAsync(
        string message,
        string title = "Moderation nicht möglich")
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
