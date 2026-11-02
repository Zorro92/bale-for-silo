using System.Numerics;
using Bale.Xbox.Services;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Shapes;

namespace Bale.Xbox.Controls;

/// <summary>Shared anchored-menu chrome from TvSkylinePanelChrome.kt, with unclipped Composition shadows.</summary>
public sealed partial class SkylinePanel : ContentControl
{
    private Rectangle? _shadowMask;

    public SkylinePanel()
    {
        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => UpdateShadowRadius());
    }

    protected override void OnApplyTemplate()
    {
        try
        {
            base.OnApplyTemplate();
            _shadowMask = GetTemplateChild("ShadowMask") as Rectangle;
            if (GetTemplateChild("ShadowHost") is not FrameworkElement host || _shadowMask is not { } mask) return;
            UpdateShadowRadius();
            if (ElementCompositionPreview.GetElementChildVisual(host) is not null) return;

            // TvSkylinePanelChrome.kt: 4dp / 20dp elevation, black at 35% / 55%, outside the rounded fill.
            var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
            var container = compositor.CreateContainerVisual();
            container.RelativeSizeAdjustment = Vector2.One;
            container.Children.InsertAtTop(CreateShadow(compositor, mask, 0.35f, 8, 2));
            container.Children.InsertAtTop(CreateShadow(compositor, mask, 0.55f, 32, 8));
            ElementCompositionPreview.SetElementChildVisual(host, container);
        }
        catch (Exception ex)
        {
            Log.Error("Panel shadow", ex);
        }
    }

    private void UpdateShadowRadius()
    {
        try
        {
            if (_shadowMask is null) return;
            _shadowMask.RadiusX = _shadowMask.RadiusY = CornerRadius.TopLeft;
        }
        catch (Exception ex)
        {
            Log.Error("Panel shadow radius", ex);
        }
    }

    private static SpriteVisual CreateShadow(Compositor compositor, Shape mask, float opacity, float blurRadius, float offsetY)
    {
        var shadow = compositor.CreateDropShadow();
        shadow.Color = Colors.Black;
        shadow.Opacity = opacity;
        shadow.BlurRadius = blurRadius;
        shadow.Offset = new Vector3(0, offsetY, 0);
        shadow.Mask = mask.GetAlphaMask();
        var visual = compositor.CreateSpriteVisual();
        visual.RelativeSizeAdjustment = Vector2.One;
        visual.Shadow = shadow;
        return visual;
    }
}
