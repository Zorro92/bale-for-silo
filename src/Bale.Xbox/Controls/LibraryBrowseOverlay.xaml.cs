using System.Collections.ObjectModel;
using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Controls;

/// <summary>TvLibraryBrowseControls.kt and TvAlphabetRail.kt: modal facets and persistent letter targets.</summary>
public sealed partial class LibraryBrowseOverlay : UserControl
{
    private readonly ObservableCollection<LibraryPanelRow> _rows = [];
    private readonly List<LibraryAlphabetEntry> _letters = new[] { "All", "#" }.Concat(Enumerable.Range('A', 26).Select(c => ((char)c).ToString()))
        .Select((letter, index) => new LibraryAlphabetEntry(letter, index)).ToList();
    private readonly Dictionary<string, Action<LibraryPanelRow>> _updates = [];
    private LibraryCatalogSource? _source;
    private CatalogFilters? _options;
    private LibraryFacetSelection _draft = new();
    private LibraryFacet? _facet;
    private bool _filterPanel;
    private bool _expanded;
    private bool _preserve;
    private int _panelIndex;
    private int _letterIndex;

    public LibraryBrowseOverlay()
    {
        InitializeComponent();
        PanelRows.ItemsSource = _rows;
        Alphabet.ItemsSource = _letters;
    }

    public event Action<LibraryBrowseFilter>? FilterChanged;
    public event Action<bool>? PreserveChanged;
    public event Action<bool>? PanelClosed;
    public event Action<bool>? OpenChanged;
    public event Action? GridRequested;
    public event Action<bool>? AlphabetExpanded;
    public bool IsOpen => Modal.Visibility == Visibility.Visible;
    public bool AlphabetHasFocus => _expanded;

    public void Configure(LibraryCatalogSource source, bool preserve)
    {
        _source = source;
        _preserve = preserve;
        Update();
    }

    public void Update(bool available = true)
    {
        AlphabetScroller.Visibility = available && _source?.Query.Filter.ShowsAlphabet == true ? Visibility.Visible : Visibility.Collapsed;
        foreach (var letter in _letters) letter.Update(_expanded, _source?.Query.Filter.NamePrefix);
    }

    public bool FocusAlphabet()
    {
        if (AlphabetScroller.Visibility != Visibility.Visible || IsOpen) return false;
        _letterIndex = 0;
        ExpandAlphabet(true);
        AlphabetScroller.ChangeView(null, 0, null, true);
        FocusRow(Alphabet, _letters[0]);
        return true;
    }

    private void ExpandAlphabet(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        AlphabetScroller.Width = expanded ? 96 : 18;
        AlphabetScroller.Margin = expanded ? new Thickness(0, 110, 16, 80) : new Thickness(0, 72, 16, 48);
        Update();
        AlphabetExpanded?.Invoke(expanded);
    }

    private void OnAlphabetFocus(object sender, RoutedEventArgs e)
    {
        ExpandAlphabet(true);
        if (RowData<LibraryAlphabetEntry>(e.OriginalSource as DependencyObject) is { } letter) _letterIndex = _letters.IndexOf(letter);
    }

    private void OnAlphabetBlur(object sender, RoutedEventArgs e)
    {
        if (!Within(FocusManager.GetFocusedElement() as DependencyObject, Alphabet)) ExpandAlphabet(false);
    }

    private void OnLetterClick(object sender, RoutedEventArgs e)
    {
        if (_source is null || sender is not FrameworkElement { DataContext: LibraryAlphabetEntry letter }) return;
        // Selecting a prefix reloads the grid but never removes/replaces the focused letter node.
        FilterChanged?.Invoke(_source.Query.Filter.SelectPrefix(letter.Letter));
        Update();
    }

    private void OnAlphabetKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var direction = Direction(e.Key);
        if (direction is null) return;
        e.Handled = true;
        if (direction == GridDirection.Left)
        {
            GridRequested?.Invoke();
            return;
        }
        var next = direction switch { GridDirection.Up => _letterIndex - 1, GridDirection.Down => _letterIndex + 1, _ => _letterIndex };
        _letterIndex = Math.Clamp(next, 0, _letters.Count - 1);
        FocusRow(Alphabet, _letters[_letterIndex]);
    }

    public void OpenSort()
    {
        if (_source is null) return;
        _filterPanel = false;
        _facet = null;
        Panel.Width = 260;
        Panel.Height = double.NaN;
        Panel.Padding = new Thickness(10);
        PanelEyebrow.Text = "SORT BY";
        PanelTitle.Visibility = PanelDivider.Visibility = Visibility.Collapsed;
        ResetRows();
        foreach (var sort in LibrarySortOption.For(_source.Query.MediaType, _source.Query.CollectionId is not null))
        {
            Add(sort.Field, sort.Label, () =>
            {
                var filter = _source.Query.Filter.SelectSort(sort);
                Close();
                FilterChanged?.Invoke(filter);
            }, row => row.Update(_source.Query.Filter.Sort == sort ? sort.DirectionLabel(_source.Query.Filter.Descending) : "",
                _source.Query.Filter.Sort == sort ? "✓" : ""));
        }
        Show(Math.Max(0, _rows.ToList().FindIndex(row => row.Key == _source.Query.Filter.Sort.Field)));
    }

    public void OpenFilter(CatalogFilters? options)
    {
        if (_source is null) return;
        _options = options;
        _filterPanel = true;
        _draft = _source.Query.Filter.Facets;
        _facet = null;
        Panel.Width = 360;
        Panel.Height = 400;
        Panel.Padding = new Thickness(16);
        BuildFilters();
        Show(0);
    }

    private void BuildFilters()
    {
        if (_source is null) return;
        PanelEyebrow.Text = _facet is null ? "FILTER BY" : "FILTER";
        PanelTitle.Text = _facet?.Title() ?? "Filters";
        PanelTitle.Visibility = PanelDivider.Visibility = Visibility.Visible;
        ResetRows();
        if (_facet is { } facet)
        {
            Add("back", "All filters", () => { _facet = null; BuildFilters(); FocusPanel(0); }, row => row.Update(mark: "‹"));
            Add("clear", $"Clear {facet.Title()}", () => { _draft = _draft.Clear(facet); RefreshRows(); }, row => row.Update(mark: "×", muted: _draft.Selected(facet).Count == 0));
            foreach (var option in facet.Options(_options))
            {
                Add(option.Value, option.Label, () => { _draft = _draft.Toggle(facet, option.Value); RefreshRows(); },
                    row => row.Update(mark: _draft.Selected(facet).Contains(option.Value) ? "●" : "○"));
            }
        }
        else
        {
            foreach (var item in LibraryFacets.For(_source.Query.MediaType).Where(item => item.Options(_options).Count > 0))
            {
                Add(item.ToString(), item.Title(), () => { _facet = item; BuildFilters(); FocusPanel(0); }, row =>
                {
                    var selected = _draft.Selected(item);
                    var summary = selected.Count switch { 0 => "Any", 1 => item.Options(_options).FirstOrDefault(option => option.Value == selected[0])?.Label ?? selected[0], _ => $"{selected.Count} selected" };
                    row.Update(summary + "  ›");
                });
            }
            Add("all", "All selected filters", () => { _draft = _draft with { MatchAll = true }; RefreshRows(); }, row => row.Update(mark: _draft.MatchAll ? "●" : "○"));
            Add("any", "Any selected filter", () => { _draft = _draft with { MatchAll = false }; RefreshRows(); }, row => row.Update(mark: !_draft.MatchAll ? "●" : "○"));
            if (_source.Query.CollectionId is null)
            {
                Add("preserve", "Preserve sort & filters", () => { _preserve = !_preserve; PreserveChanged?.Invoke(_preserve); RefreshRows(); }, row => row.Update(mark: _preserve ? "☑" : "☐"));
            }
            Add("reset", "Reset filters", () => { _draft = new(); RefreshRows(); }, row => row.Update(mark: "↻", muted: !_draft.CanReset));
            Add("done", "Done", CommitAndClose, row => row.Update(mark: "✓"));
        }
    }

    private void Add(string key, string title, Action action, Action<LibraryPanelRow> update)
    {
        var row = new LibraryPanelRow(key, title, action);
        _updates[key] = update;
        update(row);
        _rows.Add(row);
    }

    private void ResetRows()
    {
        _rows.Clear();
        _updates.Clear();
        PanelScroller.ChangeView(null, 0, null, true);
    }

    private void RefreshRows()
    {
        foreach (var row in _rows) _updates[row.Key](row);
    }

    private void Show(int index)
    {
        Modal.Visibility = Visibility.Visible;
        OpenChanged?.Invoke(true);
        FocusPanel(index);
    }

    private void FocusPanel(int index)
    {
        if (_rows.Count == 0) return;
        _panelIndex = Math.Clamp(index, 0, _rows.Count - 1);
        FocusRow(PanelRows, _rows[_panelIndex]);
    }

    private static void FocusRow(ItemsControl list, object item)
    {
        FocusRetry.Until(list, () =>
        {
            if (list.ItemsPanelRoot is not { } panel) return false;
            // ContentPresenter projects only as FrameworkElement under Native AOT.
            var root = panel.Children.OfType<FrameworkElement>().FirstOrDefault(child => ReferenceEquals(child.DataContext, item));
            var button = root is Control control ? control : root is not null ? Rail.FindChild<Control>(root) : null;
            if (button?.Focus(FocusState.Programmatic) != true) return false;
            button.StartBringIntoView();
            return true;
        });
    }

    private void OnPanelRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryPanelRow row }) row.Choose();
    }

    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var direction = Direction(e.Key);
        if (e.Key == VirtualKey.Tab) { e.Handled = true; return; }
        if (direction is null) return;
        e.Handled = true;
        if (RowData<LibraryPanelRow>(e.OriginalSource as DependencyObject) is { } row) _panelIndex = _rows.IndexOf(row);
        if (direction == GridDirection.Left && _filterPanel && _facet is not null)
        {
            _facet = null;
            BuildFilters();
            FocusPanel(0);
        }
        else if (direction is GridDirection.Up or GridDirection.Down)
        {
            FocusPanel(_panelIndex + (direction == GridDirection.Up ? -1 : 1));
        }
    }

    public bool OnBack()
    {
        if (!IsOpen) return false;
        if (_filterPanel && _facet is not null) { _facet = null; BuildFilters(); FocusPanel(0); }
        else if (_filterPanel) CommitAndClose();
        else Close();
        return true;
    }

    private void CommitAndClose()
    {
        if (_source is null) return;
        var filter = _source.Query.Filter.ApplyFacets(_draft);
        Close();
        FilterChanged?.Invoke(filter);
    }

    public void Close(bool restore = true)
    {
        if (!IsOpen) return;
        Modal.Visibility = Visibility.Collapsed;
        OpenChanged?.Invoke(false);
        if (restore) PanelClosed?.Invoke(_filterPanel);
    }

    private static T? RowData<T>(DependencyObject? element) where T : class
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { DataContext: T row }) return row;
        return null;
    }

    private static bool Within(DependencyObject? element, DependencyObject ancestor)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node)) if (node == ancestor) return true;
        return false;
    }

    private static GridDirection? Direction(VirtualKey key) => key switch
    {
        VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft => GridDirection.Left,
        VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight => GridDirection.Right,
        VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => GridDirection.Up,
        VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => GridDirection.Down,
        _ => null,
    };
}
