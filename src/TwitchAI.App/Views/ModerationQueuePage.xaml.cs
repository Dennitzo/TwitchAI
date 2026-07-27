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
        // ItemsRepeater keeps native collection/item subscriptions while its
        // source is assigned. Attach only while this page is in the visual
        // tree so AI or case updates cannot call back into a page that is
        // being removed by Frame navigation.
        AiRunsRepeater.ItemsSource = ViewModel.AiRuns;
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
        AiRunsRepeater.ItemsSource = null;
        CasesRepeater.ItemsSource = null;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        PageContent.Margin = e.NewSize.Width < 760
            ? new Thickness(72, 20, 16, 40)
            : new Thickness(28, 24, 28, 40);
    }

    private void OnCaseCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid caseCard
            || caseCard.ColumnDefinitions.Count < 2)
        {
            return;
        }

        Border? reviewPanel = null;
        foreach (var child in caseCard.Children)
        {
            if (child is Border border)
            {
                reviewPanel = border;
                break;
            }
        }

        if (reviewPanel is null)
        {
            return;
        }

        var useSideBySideLayout = e.NewSize.Width >= 1050;
        caseCard.ColumnSpacing = useSideBySideLayout ? 16 : 0;
        caseCard.ColumnDefinitions[1].Width = useSideBySideLayout
            ? new GridLength(
                Math.Clamp(e.NewSize.Width * 0.34, 340, 400))
            : new GridLength(0);
        Grid.SetColumn(reviewPanel, useSideBySideLayout ? 1 : 0);
        Grid.SetRow(reviewPanel, useSideBySideLayout ? 0 : 1);
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

    private void OnMetadataGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Grid metadataGrid
            || metadataGrid.ColumnDefinitions.Count < 5)
        {
            return;
        }

        var visibleColumnCount = e.NewSize.Width switch
        {
            >= 780 => 5,
            >= 420 => 2,
            _ => 1,
        };

        for (var columnIndex = 0; columnIndex < metadataGrid.ColumnDefinitions.Count; columnIndex++)
        {
            metadataGrid.ColumnDefinitions[columnIndex].Width =
                columnIndex >= visibleColumnCount
                    ? new GridLength(0)
                    : visibleColumnCount == 5
                        ? GridLength.Auto
                        : new GridLength(1, GridUnitType.Star);
        }

        var itemIndex = 0;
        foreach (var child in metadataGrid.Children)
        {
            if (child is not Border border)
            {
                continue;
            }

            Grid.SetColumn(border, itemIndex % visibleColumnCount);
            Grid.SetRow(border, itemIndex / visibleColumnCount);
            itemIndex++;
        }
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
