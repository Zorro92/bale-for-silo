using Bale.Xbox.Services;
using Windows.UI;
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
            UiShadow.Attach(host, mask,
                new UiShadow.Settings(Colors.Black, 0.35f, 8, 2),
                new UiShadow.Settings(Colors.Black, 0.55f, 32, 8));
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

}
