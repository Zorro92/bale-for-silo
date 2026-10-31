using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

/// <summary>TvCascadeSelector.kt's For You selector, sharing the library cascade's dwell and Back path.</summary>
public sealed partial class ShellPage
{
    private void InitializeForYouCascade()
    {
        ForYouPanel.PreviewKeyDown += OnForYouCascadeKeyDown;
        ForYouPanel.Selected += kind =>
        {
            if (_panelTab?.DataContext is not TabView { Page: "foryou" } tab) return;
            if (kind == "recommendations")
            {
                Select(tab);
                return;
            }

            _selected = tab;
            _searchSelected = false;
            UpdateChecked();
            ClosePanels();
            OpenPersonalList(kind);
        };
    }

    private void OpenForYouCascade(Control tabButton)
    {
        ClosePanels();
        _panelTab = tabButton;
        var tabLeft = tabButton.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, 0)).X;
        // TvMainShell.kt gives For You only the 230dp column, not the library flyout's 389dp cap.
        var left = Math.Clamp(tabLeft + tabButton.ActualWidth / 2 - 115, 44, Math.Max(44, Root.ActualWidth - 44 - 230));
        ForYouPanel.Margin = new Thickness(left, 66, 0, 0);
        ForYouPanel.Visibility = PanelScrim.Visibility = Visibility.Visible;
        tabButton.XYFocusDown = ForYouPanel.EntryTarget;
    }

    private void FocusForYouCascadeEntry() => FocusRetry.Until(ForYouPanel,
        () => ForYouPanel.Visibility != Visibility.Visible || ForYouPanel.FocusEntry());

    private void OnForYouCascadeKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.GamepadMenu or VirtualKey.Application)
        {
            e.Handled = true;
            var anchor = _panelTab;
            ClosePanels();
            FocusAfterCascadeDismiss(anchor);
            return;
        }

        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.GamepadDPadLeft or VirtualKey.GamepadDPadRight
            or VirtualKey.GamepadLeftThumbstickLeft or VirtualKey.GamepadLeftThumbstickRight)
        {
            e.Handled = true;
            return;
        }

        var step = e.Key switch
        {
            VirtualKey.Up or VirtualKey.GamepadDPadUp or VirtualKey.GamepadLeftThumbstickUp => -1,
            VirtualKey.Down or VirtualKey.GamepadDPadDown or VirtualKey.GamepadLeftThumbstickDown => 1,
            _ => 0,
        };
        if (step == 0) return;
        e.Handled = true;
        var buttons = ForYouPanel.Rows.ToList();
        var index = buttons.FindIndex(button => ReferenceEquals(button, FocusManager.GetFocusedElement()));
        if (index < 0) return;
        var target = index + step;
        if (target < 0) _panelTab?.Focus(FocusState.Programmatic);
        else if (target < buttons.Count) buttons[target].Focus(FocusState.Programmatic);
    }

    // TEMP: replaced at merge by ShellPage.Personal.cs
    private static void OpenPersonalList(string kind) { }
}
