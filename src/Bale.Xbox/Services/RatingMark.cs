using Bale.Core.Detail;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Services;

/// <summary>An external rating in a facts line, on the details page and the Home marquee.</summary>
internal static class RatingMark
{
    /// <summary>
    /// A rating as its source's mark and score (RatingEntry): TMDB's green-to-blue wordmark and bar,
    /// IMDb's yellow tag, or the source's plain name.
    /// </summary>
    public static StackPanel Build(DisplayRating rating, double size)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        switch (rating.Source)
        {
            case "tmdb":
                var tmdb = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(1, 0.5) };
                tmdb.GradientStops.Add(new GradientStop { Offset = 0, Color = Color.FromArgb(0xFF, 0x90, 0xCE, 0xA1) });
                tmdb.GradientStops.Add(new GradientStop { Offset = 1, Color = Color.FromArgb(0xFF, 0x01, 0xB4, 0xE4) });
                panel.Children.Add(new TextBlock { Text = "TMDB", FontSize = size * 0.875, FontWeight = Windows.UI.Text.FontWeights.Black, CharacterSpacing = 80, Foreground = tmdb, VerticalAlignment = VerticalAlignment.Center });
                panel.Children.Add(new Border { Width = 22, Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x01, 0xB4, 0xE4)), VerticalAlignment = VerticalAlignment.Center });
                break;
            case "imdb":
                panel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xC5, 0x18)),
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(3, 0, 3, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = "IMDb", FontSize = size * 0.8, FontWeight = Windows.UI.Text.FontWeights.Black, Foreground = new SolidColorBrush(Colors.Black) },
                });
                break;
            default:
                panel.Children.Add(new TextBlock { Text = rating.Name, FontSize = size, FontWeight = Windows.UI.Text.FontWeights.Medium, Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center });
                break;
        }

        panel.Children.Add(new TextBlock { Text = rating.Display, FontSize = size, FontWeight = Windows.UI.Text.FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), VerticalAlignment = VerticalAlignment.Center });
        return panel;
    }
}
