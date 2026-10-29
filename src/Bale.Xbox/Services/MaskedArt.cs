using System.Numerics;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Services;

/// <summary>
/// The marquee art box, drawn with Composition so the image itself fades out (alpha masks), as
/// TvRootHeroBackdrop masks it: transparent at the left edge to fully visible at 68% across, and
/// fully visible down to 58% then transparent at the bottom. Overlays can't do this over a tinted
/// diagonal wash without leaving a visible seam. Regenerate the mask with:
///   magick -size 614x378 xc:white -alpha set -channel A -fx 'min(1, (i/w)/0.68) * (j/h &lt; 0.58 ? 1 : 1 - (j/h - 0.58)/0.42)' +channel PNG32:ArtMask.png
/// </summary>
internal sealed class MaskedArt
{
    private readonly Compositor _compositor;
    private readonly SpriteVisual _visual;
    private readonly CompositionSurfaceBrush _image;
    private LoadedImageSurface? _surface;
    private Uri? _requested;

    public MaskedArt(FrameworkElement host, string maskUri = "ms-appx:///Assets/ArtMask.png")
    {
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _image = _compositor.CreateSurfaceBrush();
        _image.Stretch = CompositionStretch.UniformToFill;
        _image.HorizontalAlignmentRatio = 0.5f;
        _image.VerticalAlignmentRatio = 0.25f;

        // One mask image holding both fades (Assets/ArtMask.png): mask brushes can't be nested.
        var mask = _compositor.CreateSurfaceBrush(LoadedImageSurface.StartLoadFromUri(new Uri(maskUri)));
        mask.Stretch = CompositionStretch.Fill;
        var both = _compositor.CreateMaskBrush();
        both.Source = _image;
        both.Mask = mask;

        _visual = _compositor.CreateSpriteVisual();
        _visual.Brush = both;
        _visual.Opacity = 0;
        _visual.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(host, _visual);
    }

    /// <summary>Marquee crossfade (TvMarqueeCrossfadeMs).</summary>
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(240);

    public void Show(Uri image)
    {
        _requested = image;
        ShowAsync(image);
    }

    private async void ShowAsync(Uri image)
    {
        // Composition loads from the artwork cache's file, which the tint sampler shares.
        Uri source;
        try
        {
            source = await Artwork.LocalUriAsync(image);
        }
        catch (Exception ex)
        {
            Log.Error("Art source", ex);
            source = image;
        }

        if (!ReferenceEquals(image, _requested))
        {
            return;
        }

        var surface = LoadedImageSurface.StartLoadFromUri(source);
        surface.LoadCompleted += (sender, e) =>
        {
            if (e.Status != LoadedImageSourceLoadStatus.Success)
            {
                Log.Info($"Art failed to load ({e.Status}): {source.AbsolutePath}");
            }
            else if (ReferenceEquals(sender, _surface))
            {
                _image.Surface = sender;
                FadeIn();
            }
        };
        _surface?.Dispose();
        _surface = surface;
    }

    public void Hide()
    {
        _requested = null;
        _visual.Opacity = 0;
    }

    private void FadeIn()
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 0);
        animation.InsertKeyFrame(1, 1);
        animation.Duration = Fade;
        _visual.StartAnimation(nameof(Visual.Opacity), animation);
    }
}
