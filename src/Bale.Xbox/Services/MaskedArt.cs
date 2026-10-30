using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Services;

/// <summary>
/// The marquee art box, drawn with Composition so the image itself fades out (alpha masks), as
/// TvRootHeroBackdrop.kt masks it: transparent at the left edge to fully visible at 68% across, and
/// fully visible down to 58% then transparent at the bottom. Overlays can't do this over a tinted
/// diagonal wash without leaving a visible seam. Regenerate the mask with:
///   magick -size 614x378 xc:white -alpha set -channel A -fx 'min(1, (i/w)/0.68) * (j/h &lt; 0.58 ? 1 : 1 - (j/h - 0.58)/0.42)' +channel PNG32:ArtMask.png
/// </summary>
internal sealed class MaskedArt : IDisposable
{
    // TvRootHeroBackdrop.kt / TvFocusMarqueeModel.kt: first art snaps in, later swaps crossfade
    // for 500 ms with the same easing as the tint. Never hide the outgoing image before decoding.
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(500);

    private readonly FrameworkElement _host;
    private readonly Compositor _compositor;
    private readonly ContainerVisual _root;
    private readonly LoadedImageSurface _maskSurface;
    private readonly CompositionSurfaceBrush _mask;
    private readonly CubicBezierEasingFunction _easing;
    private readonly List<ArtLayer> _retiring = new();
    private ArtLayer? _current;
    private LoadedImageSurface? _pendingSurface;
    private InMemoryRandomAccessStream? _pendingStream;
    private Uri? _requested;
    private int _request;
    private bool _hasDisplayedArtwork;
    private bool _disposed;

    public MaskedArt(FrameworkElement host, string maskUri = "ms-appx:///Assets/ArtMask.png")
    {
        _host = host;
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));

        // One mask image holding both fades (Assets/ArtMask.png): mask brushes can't be nested.
        _maskSurface = LoadedImageSurface.StartLoadFromUri(new Uri(maskUri));
        _mask = _compositor.CreateSurfaceBrush(_maskSurface);
        _mask.Stretch = CompositionStretch.Fill;
        _root = _compositor.CreateContainerVisual();
        _root.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(host, _root);
    }

    public void Show(Uri image)
    {
        if (_disposed || Equals(image, _requested))
        {
            return;
        }

        _requested = image;
        var request = ++_request;
        ClearPending();
        _ = ShowAsync(image, request);
    }

    private async Task ShowAsync(Uri image, int request)
    {
        InMemoryRandomAccessStream? stream = null;
        try
        {
            // The artwork cache shares bytes with the tint sampler. Composition won't load an
            // ms-appdata URI here, but a stream decodes fine. Keep it alive until LoadCompleted.
            var bytes = await Artwork.ReadAsync(image);
            if (_disposed || request != _request)
            {
                return;
            }

            LoadedImageSurface surface;
            if (bytes is null)
            {
                surface = LoadedImageSurface.StartLoadFromUri(image);
            }
            else
            {
                stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                if (_disposed || request != _request)
                {
                    stream.Dispose();
                    return;
                }

                surface = LoadedImageSurface.StartLoadFromStream(stream);
            }

            _pendingStream = stream;
            _pendingSurface = surface;
            surface.LoadCompleted += OnLoadCompleted;
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            Log.Error("Art source", ex);
        }
    }

    private void OnLoadCompleted(LoadedImageSurface sender, LoadedImageSourceLoadCompletedEventArgs e)
    {
        if (!ReferenceEquals(sender, _pendingSurface))
        {
            return;
        }

        sender.LoadCompleted -= OnLoadCompleted;
        _pendingSurface = null;
        _pendingStream?.Dispose();
        _pendingStream = null;
        if (_disposed || e.Status != LoadedImageSourceLoadStatus.Success)
        {
            Log.Info($"Art failed to load ({e.Status}): {_requested?.AbsolutePath}");
            sender.Dispose();
            return;
        }

        var incoming = new ArtLayer(_compositor, sender, _mask);
        _root.Children.InsertAtTop(incoming.Visual);
        var outgoing = _current;
        _current = incoming;
        if (_hasDisplayedArtwork)
        {
            Animate(incoming.Visual, 1);
        }
        else
        {
            incoming.Visual.Opacity = 1;
            _hasDisplayedArtwork = true;
        }

        if (outgoing is not null)
        {
            Retire(outgoing);
        }
    }

    public void Hide()
    {
        _requested = null;
        _request++;
        ClearPending();
        if (_current is { } outgoing)
        {
            _current = null;
            Retire(outgoing);
        }
    }

    private void ClearPending()
    {
        if (_pendingSurface is { } surface)
        {
            surface.LoadCompleted -= OnLoadCompleted;
            surface.Dispose();
            _pendingSurface = null;
        }

        _pendingStream?.Dispose();
        _pendingStream = null;
    }

    private void Animate(Visual visual, float opacity)
    {
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, opacity, _easing);
        animation.Duration = Fade;
        visual.StartAnimation(nameof(Visual.Opacity), animation);
        animation.Dispose();
    }

    private void Retire(ArtLayer layer)
    {
        _retiring.Add(layer);
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) =>
        {
            // A page can unload before this fade finishes; Dispose already clears those layers.
            if (_retiring.Remove(layer))
            {
                _root.Children.Remove(layer.Visual);
                layer.Dispose();
            }

            batch.Dispose();
        };
        Animate(layer.Visual, 0);
        batch.End();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _request++;
        ClearPending();
        ElementCompositionPreview.SetElementChildVisual(_host, null);
        _root.Children.RemoveAll();
        _current?.Dispose();
        _current = null;
        foreach (var layer in _retiring)
        {
            layer.Dispose();
        }

        _retiring.Clear();
        _root.Dispose();
        _mask.Dispose();
        _maskSurface.Dispose();
        _easing.Dispose();
    }

    private sealed class ArtLayer : IDisposable
    {
        private readonly LoadedImageSurface _surface;
        private readonly CompositionSurfaceBrush _image;
        private readonly CompositionMaskBrush _brush;

        public ArtLayer(Compositor compositor, LoadedImageSurface surface, CompositionSurfaceBrush mask)
        {
            _surface = surface;
            _image = compositor.CreateSurfaceBrush(surface);
            _image.Stretch = CompositionStretch.UniformToFill;
            _image.HorizontalAlignmentRatio = 0.5f;
            _image.VerticalAlignmentRatio = 0.25f;
            _brush = compositor.CreateMaskBrush();
            _brush.Source = _image;
            _brush.Mask = mask;
            Visual = compositor.CreateSpriteVisual();
            Visual.Brush = _brush;
            Visual.Opacity = 0;
            Visual.RelativeSizeAdjustment = Vector2.One;
        }

        public SpriteVisual Visual { get; }

        public void Dispose()
        {
            Visual.Dispose();
            _brush.Dispose();
            _image.Dispose();
            _surface.Dispose();
        }
    }
}
