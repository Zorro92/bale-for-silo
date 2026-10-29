using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>Search, reached from the top bar's search icon (guide §6.7). Results update as you type.</summary>
public sealed partial class SearchPage : Page
{
    /// <summary>Wait for typing to pause before asking the server.</summary>
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(350);

    /// <summary>Standard library grid poster: 208x312 mockup px.</summary>
    private const double PosterWidth = 104;
    private const double PosterHeight = 156;

    private readonly DispatcherTimer _typing = new() { Interval = TypingPause };
    private CancellationTokenSource? _search;

    public SearchPage()
    {
        InitializeComponent();
        _typing.Tick += async (_, _) =>
        {
            _typing.Stop();
            await SearchAsync(QueryBox.Text.Trim());
        };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        QueryBox.Focus(FocusState.Programmatic);
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
            return;
        }

        var search = _search = new CancellationTokenSource();
        Status.Text = "Searching…";
        try
        {
            var cards = await client.Browse.SearchAsync(query, ArtworkWidths.Tv1080, cancellationToken: search.Token);
            if (search.IsCancellationRequested)
            {
                return;
            }

            Results.ItemsSource = cards.Select(card => new CardView(card, "Search", PosterWidth, PosterHeight, landscape: false)).ToList();
            Status.Text = cards.Count == 0 ? $"Nothing matches “{query}”." : string.Empty;
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

    private async void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CardView { Card: var card })
        {
            await CardInfo.ShowAsync(card);
        }
    }
}
