using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Services;

/// <summary>
/// The marquee art box, drawn with Composition so the image itself fades out (alpha masks), as
/// TvRootHeroBackdrop masks it: transparent at the left edge to fully visible at 68% across, and
/// fully visible down to 58% then transparent at the bottom. Overlays can't do this over a tinted
/// diagonal wash without leaving a visible seam.
/// </summary>
internal sealed class MaskedArt
{
    private readonly FrameworkElement _host;
    private readonly Compositor _compositor;
    private readonly SpriteVisual _visual;
    private readonly CompositionSurfaceBrush _image;
    private LoadedImageSurface? _surface;

    public MaskedArt(FrameworkElement host)
    {
        _host = host;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _image = _compositor.CreateSurfaceBrush();
        _image.Stretch = CompositionStretch.UniformToFill;
        _image.HorizontalAlignmentRatio = 0.5f;
        _image.VerticalAlignmentRatio = 0.25f;

        var horizontal = _compositor.CreateMaskBrush();
        horizontal.Source = _image;
        horizontal.Mask = Gradient(new Vector2(0, 0.5f), new Vector2(1, 0.5f), (0f, 0f), (0.68f, 1f), (1f, 1f));

        var both = _compositor.CreateMaskBrush();
        both.Source = horizontal;
        both.Mask = Gradient(new Vector2(0.5f, 0), new Vector2(0.5f, 1), (0f, 1f), (0.58f, 1f), (1f, 0f));

        _visual = _compositor.CreateSpriteVisual();
        _visual.Brush = both;
        _visual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(host, _visual);
    }

    /// <summary>Raised when the current image has decoded, so the box can fade in.</summary>
    public event Action? Loaded;

    public void Show(Uri image)
    {
        var surface = LoadedImageSurface.StartLoadFromUri(image);
        surface.LoadCompleted += (sender, e) =>
        {
            if (ReferenceEquals(sender, _surface) && e.Status == LoadedImageSourceLoadStatus.Success)
            {
                _image.Surface = sender;
                Loaded?.Invoke();
            }
        };
        _surface?.Dispose();
        _surface = surface;
    }

    private CompositionLinearGradientBrush Gradient(Vector2 start, Vector2 end, params (float Offset, float Alpha)[] stops)
    {
        var brush = _compositor.CreateLinearGradientBrush();
        brush.MappingMode = CompositionMappingMode.Relative;
        brush.StartPoint = start;
        brush.EndPoint = end;
        foreach (var (offset, alpha) in stops)
        {
            brush.ColorStops.Add(_compositor.CreateColorGradientStop(offset, Color.FromArgb((byte)(alpha * 255), 0, 0, 0)));
        }

        return brush;
    }
}
