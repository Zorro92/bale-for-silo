using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

/// <summary>Search, reached from the top bar's search icon (guide §6.7). Results update as you type.</summary>
public sealed partial class SearchPage : Page, IRestoresFocus
{
    /// <summary>Wait for typing to pause before asking the server.</summary>
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(350);

    /// <summary>Android TV's search grid cell (TvArtworkDimens.GridPosterWidth): 138 dp wide, 2:3.</summary>
    private const double PosterWidth = 138;
    private const double PosterHeight = 207;

    private readonly DispatcherTimer _typing = new() { Interval = TypingPause };
    private bool _focusedOnce;
    private bool _resultBringIntoViewHooked;
    private CardView? _lastResult;
    private CancellationTokenSource? _search;

    /// <summary>The filter chip's catalog type: null for All, "movie", "series" (TvSearchMediaType).</summary>
    private string? _type;

    public SearchPage()
    {
        InitializeComponent();

        // Focus the box the first time it exists (OnNavigatedTo is too early). Not on later loads: the
        // page reloads when the user comes back from a detail page, and focus belongs on that result.
        Loaded += (_, _) =>
        {
            if (!_focusedOnce)
            {
                _focusedOnce = true;
                QueryBox.Focus(FocusState.Programmatic);
            }
        };
        _typing.Tick += async (_, _) =>
        {
            _typing.Stop();
            await SearchAsync(QueryBox.Text.Trim());
        };
        Results.PreviewKeyDown += OnResultsPreviewKeyDown;
        Filters.PreviewKeyDown += OnFiltersPreviewKeyDown;
        QueryBox.PreviewKeyDown += OnQueryPreviewKeyDown;
        Results.Loaded += (_, _) => HookResultBringIntoView();
    }

    private void HookResultBringIntoView()
    {
        if (!_resultBringIntoViewHooked && Results.ItemsPanelRoot is { } panel)
        {
            // Handle below the inner ScrollViewer so it sees the adjusted rectangle.
            panel.BringIntoViewRequested += OnResultBringIntoViewRequested;
            _resultBringIntoViewHooked = true;
        }
    }

    private void OnResultBringIntoViewRequested(UIElement sender, BringIntoViewRequestedEventArgs args)
    {
        try
        {
            if (args.TargetElement is ContentControl { Content: CardView })
            {
                // TvSearchScreen-style minimal scrolling, with 44px above and 80px below each result.
                var r = args.TargetRect;
                args.TargetRect = new Rect(r.X, r.Y - 44, r.Width, r.Height + 44 + 80);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Search bring into view", ex);
        }
    }

    private async void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton chip)
        {
            return;
        }

        _type = chip.Tag is string { Length: > 0 } type ? type : null;
        foreach (var other in Filters.Children.OfType<ToggleButton>())
        {
            other.IsChecked = ReferenceEquals(other, chip);
        }

        await SearchAsync(QueryBox.Text.Trim());
    }

    private void OnQueryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown))
        {
            return;
        }

        // TvSearchScreen explicitly hands Down from the field to the selected chip.
        if (Filters.Children.OfType<ToggleButton>().FirstOrDefault(filter => filter.IsChecked == true) is { } chip
            && chip.Focus(FocusState.Programmatic))
        {
            e.Handled = true;
        }
    }

    private void OnFiltersPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown)
            || e.OriginalSource is not ToggleButton
            || Results.Items.Count == 0)
        {
            return;
        }

        // TvSearchScreen explicitly hands Down from the chips to the first card.
        var first = Results.ItemsPanelRoot?.Children.OfType<ContentControl>()
            .FirstOrDefault(item => item.Content is CardView && ReferenceEquals(item.Content, Results.Items[0]));
        if (first?.Focus(FocusState.Programmatic) == true)
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// Left/Right stay on the row, as in the official grid (a LazyVerticalGrid doesn't wrap): the
    /// GridView would otherwise carry Right from a row's last poster to the next row's first.
    /// </summary>
    private void OnResultsPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var step = e.Key switch
        {
            VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft => -1,
            VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight => 1,
            _ => 0,
        };
        var up = e.Key is VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp;
        if ((step == 0 && !up) || e.OriginalSource is not ContentControl { Content: CardView } item
            || Results.ItemsPanelRoot is not { } panel)
        {
            return;
        }

        var index = Results.IndexFromContainer(item);
        var top = panel.Children.OfType<FrameworkElement>().FirstOrDefault()?.ActualOffset.Y;
        var columns = Math.Max(1, panel.Children.OfType<FrameworkElement>().Count(child => child.ActualOffset.Y == top));
        if (up)
        {
            // TvSearchScreen explicitly returns to chips so spatial focus cannot skip to the field.
            if (index >= 0 && index < columns
                && Filters.Children.OfType<ToggleButton>().FirstOrDefault(filter => filter.IsChecked == true) is { } chip
                && chip.Focus(FocusState.Programmatic))
            {
                e.Handled = true;
            }
            return;
        }

        if (index < 0 || (step < 0 && index % columns == 0) || (step > 0 && (index % columns == columns - 1 || index == Results.Items.Count - 1)))
        {
            e.Handled = true;
        }
    }

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        _typing.Stop();
        _typing.Start();
    }

    private async Task SearchAsync(string query)
    {
        _search?.Cancel();
        if (query.Length < 2 || Session.Client is not { } client)
        {
            Results.ItemsSource = null;
            Status.Text = string.Empty;
            ShowEmpty("Search your library", "Find movies and series in one place.");
            return;
        }

        var search = _search = new CancellationTokenSource();
        try
        {
            var cards = await client.Browse.SearchAsync(query, ArtworkWidths.Tv1080, _type, cancellationToken: search.Token);
            if (search.IsCancellationRequested)
            {
                return;
            }

            Results.ItemsSource = cards.Select(card => new CardView(card, "Search", PosterWidth, PosterHeight, landscape: false)).ToList();
            HookResultBringIntoView();
            Status.Text = cards.Count == 0 ? string.Empty : $"{cards.Count} result{(cards.Count == 1 ? string.Empty : "s")}";
            if (cards.Count == 0)
            {
                ShowEmpty($"No matches for “{query}”", "Try a shorter title or a different filter.");
            }
            else
            {
                EmptyState.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Search", ex);
            Status.Text = $"Search failed. {ex.Message}";
        }
    }

    private void ShowEmpty(string title, string body)
    {
        EmptyTitle.Text = title;
        EmptyBody.Text = body;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void OnResultFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView or ProfileView } item)
        {
            HookResultBringIntoView();
            _lastResult = item.Content as CardView;
            CardFocus.Apply(item, focused: true);
        }
    }

    private void OnResultBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView or ProfileView } item)
        {
            CardFocus.Apply(item, focused: false);
        }
    }

    /// <summary>Back from a detail page lands on the result that opened it.</summary>
    public void RestoreFocus()
    {
        if (_lastResult is null)
        {
            return;
        }

        FocusRetry.Until(Results, () =>
            Results.ItemsPanelRoot?.Children.OfType<ContentControl>().FirstOrDefault(c => ReferenceEquals(c.Content, _lastResult)) is { } result
            && result.Focus(FocusState.Programmatic));
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CardView { Card: var card })
        {
            ShellPage.Current?.OpenDetail(card.Type == "episode" && card.SeriesId is { } series ? series : card.ContentId);
        }
    }
}
