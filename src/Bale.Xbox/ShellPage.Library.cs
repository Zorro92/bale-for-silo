using Bale.Core.Api.Models;
using Bale.Core.Browse;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

/// <summary>TvCascadeSelector.kt: single-library sections or libraries → anchored section flyout.</summary>
public sealed partial class ShellPage
{
    private readonly DispatcherTimer _cascadeFollow = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private UserLibrary? _cascadeLibrary;
    private Control? _cascadeAnchor;
    private Control? _cascadeDismissTarget;
    private List<LibraryChoiceView> _cascadeChoices = [];

    private void InitializeLibraryCascade()
    {
        // TvLibraryPill.set: Recommended is always first and the default; Collections is a later wave.
        var sections = new[] { new LibrarySectionChoiceView("Recommended", false), new LibrarySectionChoiceView("Browse", true) };
        SingleLibrarySections.ItemsSource = sections;
        LibrarySectionRows.ItemsSource = sections;
        LibraryPanel.PreviewKeyDown += OnLibraryCascadeKeyDown;
        LibrarySectionPanel.PreviewKeyDown += OnLibraryCascadeKeyDown;
        _cascadeFollow.Tick += (_, _) => { _cascadeFollow.Stop(); if (LibrarySectionPanel.Visibility == Visibility.Visible) OpenLibraryFlyout(); };
    }

    private static string? LibraryMediaScope(LibraryTab tab) => tab.Type switch
    {
        LibraryTabType.Movies => "movie",
        LibraryTabType.Series => "series",
        LibraryTabType.Audiobooks => "audiobook",
        _ => null,
    };

    private void OpenLibraryCascade(Control tabButton)
    {
        if (tabButton.DataContext is not TabView { LibraryTab: { } tab }) return;
        CloseLibraryCascade();
        if (_panelTab is not null) _panelTab.XYFocusDown = null;
        _panelTab = tabButton;
        var current = ScopeFor(tab);
        _cascadeLibrary = current;
        var single = tab.Libraries.Count == 1;
        _cascadeChoices = tab.Libraries.Select(library => new LibraryChoiceView(library.Id!, library.Name ?? string.Empty, library.Id == current.Id)).ToList();
        LibraryRows.ItemsSource = _cascadeChoices;
        LibraryListScroller.Visibility = single ? Visibility.Collapsed : Visibility.Visible;
        SingleLibrarySections.Visibility = single ? Visibility.Visible : Visibility.Collapsed;
        LibraryPanelHeader.Text = single ? (current.Name ?? tab.Title).ToUpperInvariant() : tab.Type switch
        {
            LibraryTabType.Movies => "MOVIE LIBRARIES",
            LibraryTabType.Series => "SERIES LIBRARIES",
            LibraryTabType.Audiobooks => "AUDIOBOOK LIBRARIES",
            _ => "MUSIC LIBRARIES",
        };
        LibraryFooterHint.Text = single ? "Press opens the section · Menu closes" : "Press opens the library · → jumps to a section · Menu closes";
        var tabLeft = tabButton.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        var left = Math.Clamp(tabLeft + tabButton.ActualWidth / 2 - 115, 44, Math.Max(44, Root.ActualWidth - 44 - 389));
        LibraryPanel.Margin = new Thickness(left, 66, 0, 0);
        LibraryPanel.Visibility = PanelScrim.Visibility = Visibility.Visible;
        LibraryPanel.UpdateLayout();
        var target = single ? CascadeButtons(SingleLibrarySections).FirstOrDefault()
            : CascadeButtons(LibraryRows).FirstOrDefault(button => button.DataContext is LibraryChoiceView { IsCurrent: true });
        tabButton.XYFocusDown = target;
    }

    private void FocusLibraryCascadeEntry()
    {
        var list = SingleLibrarySections.Visibility == Visibility.Visible ? SingleLibrarySections : LibraryRows;
        FocusRetry.Until(list, () => LibraryPanel.Visibility != Visibility.Visible ||
            (SingleLibrarySections.Visibility == Visibility.Visible ? CascadeButtons(list).FirstOrDefault()
                : CascadeButtons(list).FirstOrDefault(button => button.DataContext is LibraryChoiceView { IsCurrent: true }))?.Focus(FocusState.Programmatic) == true);
    }

    private void OnLibraryChoiceFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: LibraryChoiceView choice } button || _panelTab?.DataContext is not TabView { LibraryTab: { } tab }) return;
        _cascadeAnchor = button;
        _cascadeLibrary = tab.Libraries.FirstOrDefault(library => library.Id == choice.Id);
        if (LibrarySectionPanel.Visibility == Visibility.Visible) { _cascadeFollow.Stop(); _cascadeFollow.Start(); }
    }

    private void OpenLibraryFlyout()
    {
        if (_cascadeLibrary is null || _cascadeAnchor is null) return;
        LibrarySectionHeader.Text = (_cascadeLibrary.Name ?? string.Empty).ToUpperInvariant();
        var rowTop = _cascadeAnchor.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        LibrarySectionPanel.Margin = new Thickness(LibraryPanel.Margin.Left + 230 + 9, rowTop, 0, 0);
        LibrarySectionPanel.Visibility = Visibility.Visible;
    }

    private void OnLibrarySectionClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LibrarySectionChoiceView section } || _cascadeLibrary is not { } library
            || _panelTab?.DataContext is not TabView { LibraryTab: { } tab } selected || Session.Client is not { } client) return;
        var scope = LibraryMediaScope(tab);
        AppSettings.SetLibraryScope(client.ProfileId, tab.Type.ToString(), library.Id!);
        _selected = selected;
        _searchSelected = false;
        UpdateChecked();
        ClosePanels();
        if (!section.Browse)
        {
            ShowContent(typeof(BrowsePage), new LibraryRecommendedEntry(library.Id!, library.Name ?? tab.Title, scope));
            return;
        }
        if (ContentFrame.Content is CatalogGridPage existing && existing.ShowsLibrary(library.Id!, scope))
        {
            existing.FocusLibraryContent();
            return;
        }
        var query = new LibraryBrowseQuery(library.Id!, scope, new());
        query = query with { Filter = LibraryBrowseSettings.Load(client, query) };
        var source = new LibraryCatalogSource(library.Name ?? tab.Title, query, client.LibraryBrowse);
        ShowContent(typeof(CatalogGridPage), new CatalogGridEntry(source, client));
    }

    private void OnLibraryCascadeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = e.Key;
        if (key is VirtualKey.GamepadMenu or VirtualKey.Application)
        {
            e.Handled = true;
            var anchor = _panelTab;
            ClosePanels();
            FocusAfterCascadeDismiss(anchor);
            return;
        }
        var inFlyout = IsWithin(e.OriginalSource as DependencyObject, LibrarySectionPanel);
        var single = SingleLibrarySections.Visibility == Visibility.Visible;
        if (key is VirtualKey.Right or VirtualKey.GamepadDPadRight or VirtualKey.GamepadLeftThumbstickRight)
        {
            e.Handled = true;
            if (!single && !inFlyout)
            {
                _cascadeFollow.Stop();
                OpenLibraryFlyout();
                FocusRetry.Until(LibrarySectionRows, () => LibrarySectionPanel.Visibility != Visibility.Visible || CascadeButtons(LibrarySectionRows).FirstOrDefault()?.Focus(FocusState.Programmatic) == true);
            }
            return;
        }
        if (key is VirtualKey.Left or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadLeftThumbstickLeft)
        {
            e.Handled = true;
            if (inFlyout) _cascadeAnchor?.Focus(FocusState.Programmatic);
            return;
        }
        var step = key switch
        {
            VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => -1,
            VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => 1,
            _ => 0,
        };
        if (step == 0) return;
        e.Handled = true;
        var list = inFlyout ? LibrarySectionRows : single ? SingleLibrarySections : LibraryRows;
        var buttons = CascadeButtons(list).ToList();
        var focused = FocusManager.GetFocusedElement();
        var index = buttons.FindIndex(button => ReferenceEquals(button, focused));
        var target = index + step;
        if (index < 0 || target >= buttons.Count) return;
        if (target < 0) { if (!inFlyout) _panelTab?.Focus(FocusState.Programmatic); return; }
        buttons[target].Focus(FocusState.Programmatic);
        buttons[target].StartBringIntoView();
    }

    private static IEnumerable<Control> CascadeButtons(ItemsControl list) => list.ItemsPanelRoot?.Children.OfType<FrameworkElement>()
        .Select(child => child is Control control ? control : FindChild<Control>(child)).OfType<Control>() ?? [];

    private void FocusAfterCascadeDismiss(Control? anchor)
    {
        // TvTopMenuBar.kt: the focus used to close a cascade must not immediately dwell-open it again.
        if (anchor is { FocusState: FocusState.Unfocused, DataContext: TabView { LibraryTab: not null } }) _cascadeDismissTarget = anchor;
        anchor?.Focus(FocusState.Programmatic);
    }

    private void CloseLibraryCascade()
    {
        _cascadeFollow.Stop();
        LibrarySectionPanel.Visibility = Visibility.Collapsed;
        _cascadeLibrary = null;
        _cascadeAnchor = null;
    }
}
