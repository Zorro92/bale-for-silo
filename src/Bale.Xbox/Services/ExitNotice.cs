using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Services;

/// <summary>
/// A notice that outlives the page that raised it: the player leaves at once and the viewer reads why on the page it
/// returns to. TvPlayerScreen.kt shows the server-end notice as a long Toast while it exits, and Roku (6389fcc) draws its
/// notice over every screen the same way. Bale has no system toast, so a window popup carries it, in the official player
/// notice look (TvPlayerNoticeOverlay.kt, as SkylinePlayerNoticeStyle; literals, since code can't read XAML resources
/// under Native AOT), at the bottom centre where Android draws a toast, for Toast.LENGTH_LONG. It never takes focus or
/// input. UI thread.
/// </summary>
internal static class ExitNotice
{
    // android.widget.Toast.LENGTH_LONG.
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(3500);
    private static Popup? _popup;
    private static Grid? _root;
    private static TextBlock? _text;
    private static DispatcherTimer? _timer;

    public static void Show(string message)
    {
        try
        {
            if (_popup is null)
            {
                _text = new TextBlock
                {
                    // SkylinePlayerNoticeTextStyle: 18sp × the TV app's 0.86 scale, white, wrapping.
                    FontFamily = new FontFamily("ms-appx:///Assets/Fonts/InterVariable.ttf#Inter Variable"),
                    FontSize = 15.48,
                    LineHeight = 20.64,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var pill = new Border
                {
                    // SkylinePlayerNoticeStyle: 480 wide at most, 20/14 padding, 16 corners, the info blue at 92%.
                    MaxWidth = 480,
                    Padding = new Thickness(20, 14, 20, 14),
                    CornerRadius = new CornerRadius(16),
                    Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xEB, 0x1E, 0x40, 0xAF)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(32, 0, 32, 48),
                    Child = _text,
                };
                _root = new Grid { IsHitTestVisible = false };
                _root.Children.Add(pill);
                _popup = new Popup { Child = _root, IsHitTestVisible = false };
                _timer = new DispatcherTimer { Interval = Duration };
                _timer.Tick += (_, _) => Hide();
            }
            var bounds = Window.Current.Bounds;
            _root!.Width = bounds.Width;
            _root.Height = bounds.Height;
            _text!.Text = message;
            _popup.IsOpen = true;
            // A second notice restarts the time, as a new toast would.
            _timer!.Stop();
            _timer.Start();
        }
        catch (Exception ex) { Log.Error("exit notice", ex); }
    }

    private static void Hide()
    {
        try
        {
            _timer?.Stop();
            if (_popup is not null) _popup.IsOpen = false;
        }
        catch (Exception ex) { Log.Error("exit notice", ex); }
    }
}
