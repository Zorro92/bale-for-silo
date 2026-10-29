using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>
/// Skyline's root shell: the persistent top bar over whichever root screen is selected.
/// Tabs are Home plus one per library type (guide §3.1); search and profile sit on the right.
/// </summary>
public sealed partial class ShellPage : Page, IBackHandler
{
    /// <summary>Focus must rest on a library tab this long before its panel opens (guide §5.3).</summary>
    private static readonly TimeSpan PanelDwell = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _dwell = new() { Interval = PanelDwell };
    private readonly Dictionary<Button, LibraryTab?> _tabs = new();
    private Button? _homeTab;
    private Button? _selectedTab;
    private Button? _dwellTab;
    private Button? _panelTab;

    public ShellPage()
    {
        InitializeComponent();
        _dwell.Tick += (_, _) =>
        {
            _dwell.Stop();
            if (_dwellTab is not null && _dwellTab.FocusState != FocusState.Unfocused)
            {
                OpenLibraryPanel(_dwellTab);
            }
        };
        Root.GotFocus += OnAnyFocus;
    }

    /// <summary>Content pages ask the shell to switch tabs or reach the root frame.</summary>
    internal static ShellPage? Current { get; private set; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Current = this;
        var name = Session.ProfileName ?? Session.Client?.Account?.Username ?? "?";
        AvatarInitial.Text = PanelInitial.Text = name.Substring(0, 1).ToUpperInvariant();
        PanelProfileName.Text = name;
        PanelServer.Text = Session.Client?.Server.Host ?? string.Empty;

        _homeTab = AddTab("Home", null);
        try
        {
            foreach (var tab in await Session.Client!.Browse.LoadLibraryTabsAsync())
            {
                AddTab(tab.Title, tab);
            }
        }
        catch (Exception ex)
        {
            // Home still works without the library tabs.
            Log.Error("Library tabs", ex);
        }

        Select(_homeTab);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        if (Current == this)
        {
            Current = null;
        }
    }

    /// <summary>Back chain (guide §7): panel → close; content → top bar; another tab → Home; Home → leave the app.</summary>
    public bool OnBack()
    {
        if (LibraryPanel.Visibility == Visibility.Visible || ProfilePanel.Visibility == Visibility.Visible)
        {
            var anchor = ProfilePanel.Visibility == Visibility.Visible ? AvatarButton : _panelTab;
            ClosePanels();
            anchor?.Focus(FocusState.Programmatic);
            return true;
        }

        if (!IsInTopBar(FocusManager.GetFocusedElement() as DependencyObject))
        {
            _selectedTab?.Focus(FocusState.Programmatic);
            return true;
        }

        if (_selectedTab != _homeTab && _homeTab is not null)
        {
            Select(_homeTab);
            _homeTab.Focus(FocusState.Programmatic);
            return true;
        }

        return false;
    }

    private Button AddTab(string title, LibraryTab? tab)
    {
        var button = new Button { Content = title, Style = (Style)Application.Current.Resources["SkylineCapsuleStyle"] };
        button.Click += (_, _) => Select(button);
        button.GotFocus += (_, _) =>
        {
            ClosePanels();
            _dwell.Stop();
            if (_tabs[button] is { Libraries.Count: > 1 })
            {
                _dwellTab = button;
                _dwell.Start();
            }
        };
        _tabs[button] = tab;
        Tabs.Children.Add(button);
        return button;
    }

    /// <summary>Pressing a tab commits it (guide §7): its page replaces the content in place.</summary>
    private void Select(Button tab)
    {
        ClosePanels();
        SetSelected(tab);
        if (_tabs[tab] is { } libraryTab)
        {
            var scope = ScopeFor(libraryTab);
            ShowContent(typeof(BrowsePage), BrowsePage.LibraryParameter(scope.Id!, scope.Name ?? libraryTab.Title));
        }
        else
        {
            ShowContent(typeof(BrowsePage), BrowsePage.HomeParameter);
        }
    }

    private void SetSelected(Button? tab)
    {
        var resting = (Style)Application.Current.Resources["SkylineCapsuleStyle"];
        var selected = (Style)Application.Current.Resources["SkylineCapsuleSelectedStyle"];
        foreach (var button in _tabs.Keys)
        {
            button.Style = button == tab ? selected : resting;
        }

        SearchButton.Style = tab is null ? selected : resting;
        _selectedTab = tab;
    }

    private void ShowContent(Type page, string parameter)
    {
        ContentFrame.Navigate(page, parameter);
        ContentFrame.BackStack.Clear();
    }

    /// <summary>The library a tab shows: the persisted choice, else the first by server sort order (guide §3.1).</summary>
    private static UserLibrary ScopeFor(LibraryTab tab)
    {
        var saved = AppSettings.GetLibraryScope(Session.Client?.ProfileId, tab.Type.ToString());
        return tab.Libraries.FirstOrDefault(library => library.Id == saved) ?? tab.Libraries[0];
    }

    private void OpenLibraryPanel(Button tabButton)
    {
        if (_tabs[tabButton] is not { } tab)
        {
            return;
        }

        var current = ScopeFor(tab);
        LibraryPanelHeader.Text = $"{tab.Title.ToUpperInvariant()} LIBRARIES";
        LibraryRows.Children.Clear();
        Button? currentRow = null;
        foreach (var library in tab.Libraries)
        {
            var row = new Button { Style = (Style)Application.Current.Resources["SkylinePanelRowStyle"], Content = PanelRowContent(library.Name ?? "", library.Id == current.Id) };
            row.Click += (_, _) =>
            {
                AppSettings.SetLibraryScope(Session.Client?.ProfileId, tab.Type.ToString(), library.Id!);
                Select(tabButton);
            };
            LibraryRows.Children.Add(row);
            if (library.Id == current.Id)
            {
                currentRow = row;
            }
        }

        // Centre the panel under its tab.
        var tabLeft = tabButton.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        var left = Math.Max(44, tabLeft + tabButton.ActualWidth / 2 - LibraryPanel.Width / 2);
        LibraryPanel.Margin = new Thickness(left, 66, 0, 0);
        LibraryPanel.Visibility = Visibility.Visible;
        PanelScrim.Visibility = Visibility.Visible;
        _panelTab = tabButton;

        // Down from the tab enters the panel on the current scope row; focus stays on the tab until then.
        tabButton.XYFocusDown = currentRow;
    }

    private static Grid PanelRowContent(string name, bool current)
    {
        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis });
        var glyph = new FontIcon { Glyph = current ? "" : "", FontSize = 10 };
        Grid.SetColumn(glyph, 1);
        grid.Children.Add(glyph);
        return grid;
    }

    private void ClosePanels()
    {
        if (_panelTab is not null)
        {
            _panelTab.XYFocusDown = null;
            _panelTab = null;
        }

        AvatarButton.XYFocusDown = null;
        LibraryPanel.Visibility = Visibility.Collapsed;
        ProfilePanel.Visibility = Visibility.Collapsed;
        PanelScrim.Visibility = Visibility.Collapsed;
    }

    /// <summary>Panels close when focus leaves them; the bar dims to 70% while focus is in content (guide §5.1).</summary>
    private void OnAnyFocus(object sender, RoutedEventArgs e)
    {
        var focused = e.OriginalSource as DependencyObject;
        var inBar = IsInTopBar(focused);
        TopBar.Opacity = inBar ? 1 : 0.7;
        if (!inBar)
        {
            _dwell.Stop();
            ClosePanels();
        }
    }

    private bool IsInTopBar(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == TopBar || node == LibraryPanel || node == ProfilePanel)
            {
                return true;
            }
        }

        return false;
    }

    private void OnSearchClick(object sender, RoutedEventArgs e)
    {
        ClosePanels();
        SetSelected(null);
        ShowContent(typeof(SearchPage), string.Empty);
    }

    private void OnAvatarClick(object sender, RoutedEventArgs e)
    {
        ClosePanels();
        ProfilePanel.Visibility = Visibility.Visible;
        PanelScrim.Visibility = Visibility.Visible;
        AvatarButton.XYFocusDown = SwitchProfileRow;
        SwitchProfileRow.Focus(FocusState.Programmatic);
    }

    private void OnSwitchProfileClick(object sender, RoutedEventArgs e)
    {
        ClosePanels();
        Frame.Navigate(typeof(ProfilesPage), ProfilesPage.FromHome);
    }

    private async void OnSignOutClick(object sender, RoutedEventArgs e)
    {
        ClosePanels();
        await SignOutAsync();
    }

    /// <summary>Signs out and returns to the sign-in screen; content pages call this on a dead session.</summary>
    internal async Task SignOutAsync()
    {
        await Session.SignOutAsync();
        Frame.Navigate(typeof(MainPage));
        Frame.BackStack.Clear();
    }
}
