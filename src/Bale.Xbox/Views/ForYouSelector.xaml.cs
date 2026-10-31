using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Bale.Xbox.Views;

public sealed partial class ForYouSelector : UserControl
{
    public ForYouSelector() => InitializeComponent();

    internal event Action<string>? Selected;

    internal Control EntryTarget => RecommendationsRow;

    internal IReadOnlyList<Control> Rows => [RecommendationsRow, FavoritesRow, WatchlistRow];

    internal bool FocusEntry() => RecommendationsRow.Focus(FocusState.Programmatic);

    private void OnSelected(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string kind }) Selected?.Invoke(kind);
    }
}
