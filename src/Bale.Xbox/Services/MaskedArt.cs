using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using Windows.Foundation;
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
internal sealed class MaskedArt : IDisposable, IUiResource
{
    // TvRootHeroBackdrop.kt / TvFocusMarqueeModel.kt: first art snaps in, later swaps crossfade
    // for 500 ms with the same easing as the tint. Never hide the outgoing image before decoding.
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(500);

    private readonly FrameworkElement _host;
    private readonly Compositor _compositor;
    private ContainerVisual? _root;
    private readonly Uri _maskUri;
    private LoadedImageSurface? _maskSurface;
    private CompositionSurfaceBrush? _mask;
    private CubicBezierEasingFunction? _easing;
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
        _maskUri = new Uri(maskUri);
        UiResidency.Register(host, this);
        host.SizeChanged += OnSizeChanged;
    }

    // LoadedImageSurface's desiredMaxSize is in DIPs, just like the XAML host. The API applies
    // rasterization scale; don't multiply by two again. Never start a full-source decode at size 0.
    private void OnSizeChanged(object sender, SizeChangedEventArgs args) => Restore();

    private Size DecodeSize => new(Math.Max(1, _host.ActualWidth), Math.Max(1, _host.ActualHeight));

    private void CreateVisuals()
    {
        if (_root is not null) return;
        _easing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));
        _maskSurface = LoadedImageSurface.StartLoadFromUri(_maskUri, DecodeSize);
        _mask = _compositor.CreateSurfaceBrush(_maskSurface);
        _mask.Stretch = CompositionStretch.Fill;
        _root = _compositor.CreateContainerVisual();
        _root.RelativeSizeAdjustment = Vector2.One;
        ElementCompositionPreview.SetElementChildVisual(_host, _root);
    }

    public UiResourceCounts Resident => new(Surfaces:
        (_maskSurface is null ? 0 : 1) + (_pendingSurface is null ? 0 : 1)
        + (_current is null ? 0 : 1) + _retiring.Count);

    public UiResourceCounts Restore()
    {
        if (_disposed || UiResidency.IsSuppressed || !_host.IsLoaded || _root is not null
            || _requested is null || _host.ActualWidth <= 0 || _host.ActualHeight <= 0) return default;
        CreateVisuals();
        _ = ShowAsync(_requested, ++_request);
        return new(Surfaces: 1); // mask created; backdrop decode follows asynchronously
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
        if (UiResidency.IsSuppressed || !_host.IsLoaded || _host.ActualWidth <= 0 || _host.ActualHeight <= 0) return;
        CreateVisuals();
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
            if (_disposed || UiResidency.IsSuppressed || request != _request)
            {
                return;
            }

            LoadedImageSurface surface;
            if (bytes is null)
            {
                surface = LoadedImageSurface.StartLoadFromUri(image, DecodeSize);
            }
            else
            {
                stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(bytes.AsBuffer());
                stream.Seek(0);
                if (_disposed || UiResidency.IsSuppressed || request != _request)
                {
                    stream.Dispose();
                    return;
                }

                surface = LoadedImageSurface.StartLoadFromStream(stream, DecodeSize);
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
        if (_disposed || UiResidency.IsSuppressed || _root is null || _mask is null
            || e.Status != LoadedImageSourceLoadStatus.Success)
        {
            Log.Info($"Art failed to load ({e.Status})");
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
        var root = _root!;
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) =>
        {
            // A page can unload before this fade finishes; Dispose already clears those layers.
            if (_retiring.Remove(layer))
            {
                root.Children.Remove(layer.Visual);
                layer.Dispose();
            }

            batch.Dispose();
        };
        Animate(layer.Visual, 0);
        batch.End();
    }

    public UiResourceCounts Release()
    {
        var released = Resident;
        _request++;
        ClearPending();
        if (_root is not null)
        {
            ElementCompositionPreview.SetElementChildVisual(_host, null);
            _root.Children.RemoveAll();
        }
        _current?.Dispose();
        _current = null;
        foreach (var layer in _retiring) layer.Dispose();
        _retiring.Clear();
        _root?.Dispose();
        _root = null;
        _mask?.Dispose();
        _mask = null;
        _maskSurface?.Dispose();
        _maskSurface = null;
        _easing?.Dispose();
        _easing = null;
        _hasDisplayedArtwork = false; // first resume image snaps; subsequent changes still crossfade
        return released;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.SizeChanged -= OnSizeChanged;
        Release();
        _requested = null;
        UiResidency.Forget(_host, this);
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
