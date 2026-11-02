using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;

namespace Bale.Xbox.Services;

/// <summary>TvAnchoredSelectorMenu.kt's alpha-only list fade; never paint over the Skyline panel gradient.</summary>
internal sealed class SelectorEdgeFade : IDisposable
{
    private readonly FrameworkElement _host;
    private readonly CompositionVisualSurface _sourceSurface;
    private readonly CompositionSurfaceBrush _sourceBrush;
    private readonly CompositionLinearGradientBrush _gradient;
    private readonly SpriteVisual _maskVisual;
    private readonly CompositionVisualSurface _maskSurface;
    private readonly CompositionSurfaceBrush _maskBrush;
    private readonly CompositionMaskBrush _brush;
    private readonly SpriteVisual _visual;

    public SelectorEdgeFade(FrameworkElement source, FrameworkElement host)
    {
        _host = host;
        var sourceVisual = ElementCompositionPreview.GetElementVisual(source);
        var compositor = sourceVisual.Compositor;
        _sourceSurface = compositor.CreateVisualSurface();
        _sourceSurface.SourceVisual = sourceVisual;
        _sourceBrush = compositor.CreateSurfaceBrush(_sourceSurface);
        _gradient = compositor.CreateLinearGradientBrush();
        _gradient.StartPoint = Vector2.Zero;
        _gradient.EndPoint = Vector2.UnitY;
        foreach (var offset in new[] { 0f, 0f, 1f, 1f })
        {
            _gradient.ColorStops.Add(compositor.CreateColorGradientStop(offset, Colors.Black));
        }

        // Mask accepts a surface brush, not a gradient brush. Rasterize both edges together into
        // one surface: CompositionMaskBrush cannot nest another mask (Native AOT/UWP pitfall).
        _maskVisual = compositor.CreateSpriteVisual();
        _maskVisual.Brush = _gradient;
        _maskSurface = compositor.CreateVisualSurface();
        _maskSurface.SourceVisual = _maskVisual;
        _maskBrush = compositor.CreateSurfaceBrush(_maskSurface);
        _brush = compositor.CreateMaskBrush();
        _brush.Source = _sourceBrush;
        _brush.Mask = _maskBrush;
        _visual = compositor.CreateSpriteVisual();
        _visual.Brush = _brush;
        ElementCompositionPreview.SetElementChildVisual(host, _visual);
    }

    public void Update(double width, double height, double fadeHeight, bool fadeTop, bool fadeBottom)
    {
        if (width <= 0 || height <= 0) return;
        var size = new Vector2((float)width, (float)height);
        _sourceSurface.SourceSize = size;
        _maskSurface.SourceSize = size;
        _maskVisual.Size = size;
        _visual.Size = size;
        var fraction = (float)Math.Clamp(fadeHeight / height, 0, 0.5);
        _gradient.ColorStops[0].Color = fadeTop ? Colors.Transparent : Colors.Black;
        _gradient.ColorStops[1].Offset = fraction;
        _gradient.ColorStops[2].Offset = 1 - fraction;
        _gradient.ColorStops[3].Color = fadeBottom ? Colors.Transparent : Colors.Black;
    }

    public void Dispose()
    {
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _visual.Dispose();
        _brush.Dispose();
        _maskBrush.Dispose();
        _maskSurface.Dispose();
        _maskVisual.Dispose();
        foreach (var stop in _gradient.ColorStops) stop.Dispose();
        _gradient.Dispose();
        _sourceBrush.Dispose();
        _sourceSurface.Dispose();
    }
}
