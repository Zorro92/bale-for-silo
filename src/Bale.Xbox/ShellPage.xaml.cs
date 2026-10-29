using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>
/// Skyline's root shell: the persistent top bar over whichever root screen is selected.
/// Tabs are Home plus one per library type (guide §3.1); search and profile sit on the right.
/// All styling lives in XAML: tabs are ToggleButtons whose Checked state is "selected".
/// </summary>
public sealed partial class ShellPage : Page, IBackHandler
{
    /// <summary>Focus must rest on a library tab this long before its panel opens (guide §5.3).</summary>
    private static readonly TimeSpan PanelDwell = TimeSpan.FromMilliseconds(250);

    private readonly DispatcherTimer _dwell = new() { Interval = PanelDwell };
    private readonly List<TabView> _tabs = new();
    private TabView? _selected;
    private bool _searchSelected;
    private Control? _dwellTab;
    private Control? _panelTab;

    public ShellPage()
    {
        InitializeComponent();
        _dwell.Tick += (_, _) =>
        {
            _dwell.Stop();
            if (_dwellTab is { FocusState: not FocusState.Unfocused } tab)
            {
                OpenLibraryPanel(tab);
            }
        };
        Root.GotFocus += OnAnyFocus;
        TopBar.GettingFocus += OnTopBarGettingFocus;
        TopBar.PreviewKeyDown += OnTopBarPreviewKeyDown;
    }

    /// <summary>
    /// Up from content lands on the selected tab (or Search on the search screen), never the control
    /// nearest on screen (TvTopMenuBar's enter target).
    /// </summary>
    private void OnTopBarGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        try
        {
            if (!IsInTopBar(args.OldFocusedElement) && IsInTopBar(args.NewFocusedElement)
                && (_searchSelected ? (Control?)SearchButton : ButtonFor(_selected)) is { } target
                && !ReferenceEquals(target, args.NewFocusedElement))
            {
                args.TrySetNewFocusedElement(target);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Top bar focus", ex);
        }
    }

    /// <summary>Left off Search and Right off the avatar stay in the bar instead of dropping into content.</summary>
    private void OnTopBarPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var left = e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.GamepadDPadLeft or Windows.System.VirtualKey.GamepadLeftThumbstickLeft;
        var right = e.Key is Windows.System.VirtualKey.Right or Windows.System.VirtualKey.GamepadDPadRight or Windows.System.VirtualKey.GamepadLeftThumbstickRight;
        if ((left && ReferenceEquals(e.OriginalSource, SearchButton)) || (right && ReferenceEquals(e.OriginalSource, AvatarButton)))
        {
            e.Handled = true;
        }
    }

    /// <summary>Content pages reach the shell through this (sign-out on a dead session).</summary>
    internal static ShellPage? Current { get; private set; }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Log.Info("Shell: navigated to " + (e.Parameter as string ?? ""));
        Current = this;

        // Back from a detail page: the cached shell keeps its tabs and content; just restore focus.
        if (e.NavigationMode == NavigationMode.Back && _tabs.Count > 0)
        {
            // After the cached page is back in the tree and Windows has made its own first-focus pick.
            _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () => (ContentFrame.Content as IRestoresFocus)?.RestoreFocus());
            return;
        }

        var name = Session.ProfileName ?? Session.Client?.Account?.Username ?? "?";
        AvatarInitial.Text = PanelInitial.Text = name.Substring(0, 1).ToUpperInvariant();
        PanelProfileName.Text = name;
        PanelServer.Text = Session.Client?.Server.Host ?? string.Empty;
        if (Session.ProfileAvatar is { } avatar)
        {
            AvatarPicture.Fill = new ImageBrush { ImageSource = ProfileView.AvatarImage(avatar, 30), Stretch = Stretch.UniformToFill };
            PanelPicture.Fill = new ImageBrush { ImageSource = ProfileView.AvatarImage(avatar, 32), Stretch = Stretch.UniformToFill };
            AvatarPicture.Visibility = PanelPicture.Visibility = Visibility.Visible;
        }

        _tabs.Clear();
        _tabs.Add(new TabView("Home", null));
        try
        {
            _tabs.AddRange((await Session.Client!.Browse.LoadLibraryTabsAsync()).Select(tab => new TabView(tab.Title, tab)));
        }
        catch (Exception ex)
        {
            // Home still works without the library tabs.
            Log.Error("Library tabs", ex);
        }

        // Fixed roots after the library tabs, as TvTopMenuBar orders them.
        _tabs.Add(new TabView("For You", null, "foryou"));
        _tabs.Add(new TabView("Calendar", null, "calendar"));
        Tabs.ItemsSource = _tabs.ToList();
        Tabs.UpdateLayout();
        Select(_tabs[0]);
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
            Control? anchor = ProfilePanel.Visibility == Visibility.Visible ? AvatarButton : _panelTab;
            ClosePanels();
            anchor?.Focus(FocusState.Programmatic);
            return true;
        }

        if (!IsInTopBar(FocusManager.GetFocusedElement() as DependencyObject))
        {
            (_searchSelected ? (Control?)SearchButton : ButtonFor(_selected))?.Focus(FocusState.Programmatic);
            return true;
        }

        if (_searchSelected || _selected != _tabs[0])
        {
            Select(_tabs[0]);
            ButtonFor(_tabs[0])?.Focus(FocusState.Programmatic);
            return true;
        }

        return false;
    }

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabView tab })
        {
            Select(tab);
        }
    }

    private void OnTabFocus(object sender, RoutedEventArgs e)
    {
        ClosePanels();
        _dwell.Stop();
        // Match on Control: under Native AOT a sender can arrive typed as its base class.
        if (sender is Control { DataContext: TabView { LibraryTab.Libraries.Count: > 1 } } button)
        {
            _dwellTab = button;
            _dwell.Start();
        }
    }

    /// <summary>Pressing a tab commits it (guide §7): its page replaces the content in place.</summary>
    private void Select(TabView tab)
    {
        ClosePanels();
        _selected = tab;
        _searchSelected = false;
        UpdateChecked();
        if (tab.Page == "foryou")
        {
            ShowContent(typeof(BrowsePage), BrowsePage.ForYouParameter);
        }
        else if (tab.Page == "calendar")
        {
            ShowContent(typeof(CalendarPage), string.Empty);
        }
        else if (tab.LibraryTab is { } libraryTab)
        {
            var scope = ScopeFor(libraryTab);
            ShowContent(typeof(BrowsePage), BrowsePage.LibraryParameter(scope.Id!, scope.Name ?? libraryTab.Title));
        }
        else
        {
            ShowContent(typeof(BrowsePage), BrowsePage.HomeParameter);
        }
    }

    private void UpdateChecked()
    {
        foreach (var tab in _tabs)
        {
            if (ButtonFor(tab) is { } button)
            {
                button.IsChecked = !_searchSelected && tab == _selected;
            }
        }
    }

    private ToggleButton? ButtonFor(TabView? tab) =>
        tab is not null && Tabs.ContainerFromItem(tab) is DependencyObject container ? FindChild<ToggleButton>(container) : null;

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

    private void OpenLibraryPanel(Control tabButton)
    {
        if (tabButton.DataContext is not TabView { LibraryTab: { } tab })
        {
            return;
        }

        var current = ScopeFor(tab);
        var choices = tab.Libraries.Select(library => new LibraryChoiceView(library.Id!, library.Name ?? string.Empty, library.Id == current.Id)).ToList();
        LibraryPanelHeader.Text = $"{tab.Title.ToUpperInvariant()} LIBRARIES";
        LibraryRows.ItemsSource = choices;

        // Centre the panel under its tab.
        var tabLeft = tabButton.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        LibraryPanel.Margin = new Thickness(Math.Max(44, tabLeft + tabButton.ActualWidth / 2 - LibraryPanel.Width / 2), 66, 0, 0);
        LibraryPanel.Visibility = Visibility.Visible;
        PanelScrim.Visibility = Visibility.Visible;
        _panelTab = tabButton;

        // Down from the tab enters the panel on the current scope's row; focus stays on the tab until then.
        LibraryRows.UpdateLayout();
        var currentChoice = choices.First(choice => choice.IsCurrent);
        tabButton.XYFocusDown = LibraryRows.ContainerFromItem(currentChoice) is DependencyObject row ? FindChild<Control>(row) : null;
    }

    private void OnLibraryRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LibraryChoiceView choice }
            && _panelTab?.DataContext is TabView { LibraryTab: { } libraryTab } tab)
        {
            AppSettings.SetLibraryScope(Session.Client?.ProfileId, libraryTab.Type.ToString(), choice.Id);
            Select(tab);
        }
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

    /// <summary>Panels close when focus leaves the bar; the bar dims to 70% while focus is in content (guide §5.1).</summary>
    private void OnAnyFocus(object sender, RoutedEventArgs e)
    {
        var inBar = IsInTopBar(e.OriginalSource as DependencyObject);
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
        _searchSelected = true;
        UpdateChecked();
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

    /// <summary>Pushes a title's detail page full screen over the shell (Skyline: detail has no top bar).</summary>
    internal void OpenDetail(string contentId)
    {
        ClosePanels();
        Frame.Navigate(typeof(DetailPage), contentId);
    }

    /// <summary>Signs out and returns to the sign-in screen; content pages call this on a dead session.</summary>
    internal async Task SignOutAsync()
    {
        await Session.SignOutAsync();
        Frame.Navigate(typeof(MainPage));
        Frame.BackStack.Clear();
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T self)
        {
            return self;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i)) is { } match)
            {
                return match;
            }
        }

        return null;
    }
}
