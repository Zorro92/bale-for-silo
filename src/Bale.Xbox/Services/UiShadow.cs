using System.Numerics;
using System.Runtime.CompilerServices;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Shapes;

namespace Bale.Xbox.Services;

/// <summary>Owns shadows/visuals and borrows XAML's alpha masks; the host owns the small recipe.</summary>
internal static class UiShadow
{
    internal readonly record struct Settings(Color Color, float Opacity, float Blur, float OffsetY);
    private static readonly ConditionalWeakTable<FrameworkElement, ShadowResource> Shadows = new();

    internal static void Attach(FrameworkElement host, FrameworkElement mask, params Settings[] settings)
    {
        if (Shadows.TryGetValue(host, out var old)) old.Dispose();
        Shadows.Remove(host);
        var resource = new ShadowResource(host, mask, settings);
        Shadows.Add(host, resource);
        UiResidency.Register(host, resource);
        resource.Restore();
    }

    private sealed class ShadowResource : IUiResource, IDisposable
    {
        private readonly FrameworkElement _host;
        private readonly FrameworkElement _mask;
        private readonly Settings[] _settings;
        private readonly List<(SpriteVisual Visual, DropShadow Shadow)> _layers = new();
        private ContainerVisual? _root;

        internal ShadowResource(FrameworkElement host, FrameworkElement mask, Settings[] settings)
        {
            _host = host;
            _mask = mask;
            _settings = settings;
            host.Loaded += OnLoaded;
            host.Unloaded += OnUnloaded;
            mask.SizeChanged += OnSizeChanged;
        }

        public UiResourceCounts Resident => new(Shadows: _layers.Count);
        private void OnLoaded(object sender, RoutedEventArgs args) => Restore();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Release();
        private void OnSizeChanged(object sender, SizeChangedEventArgs args) => Resize();

        public UiResourceCounts Restore()
        {
            if (_root is not null || UiResidency.IsSuppressed || !_host.IsLoaded) return default;
            var compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
            _root = compositor.CreateContainerVisual();
            _root.RelativeSizeAdjustment = Vector2.One;
            foreach (var settings in _settings)
            {
                var mask = _mask switch
                {
                    Shape shape => shape.GetAlphaMask(),
                    TextBlock text => text.GetAlphaMask(),
                    _ => throw new InvalidOperationException("Unsupported shadow mask"),
                };
                var shadow = compositor.CreateDropShadow();
                shadow.Color = settings.Color;
                shadow.Opacity = settings.Opacity;
                shadow.BlurRadius = settings.Blur;
                shadow.Offset = new Vector3(0, settings.OffsetY, 0);
                shadow.Mask = mask;
                var visual = compositor.CreateSpriteVisual();
                visual.Shadow = shadow;
                _layers.Add((visual, shadow));
                _root.Children.InsertAtTop(visual);
            }
            Resize();
            ElementCompositionPreview.SetElementChildVisual(_host, _root);
            return Resident;
        }

        private void Resize()
        {
            foreach (var (visual, _) in _layers)
            {
                if (_mask is TextBlock) visual.Size = new((float)_mask.ActualWidth, (float)_mask.ActualHeight);
                else visual.RelativeSizeAdjustment = Vector2.One;
            }
        }

        public UiResourceCounts Release()
        {
            var released = Resident;
            if (_root is not null)
            {
                ElementCompositionPreview.SetElementChildVisual(_host, null);
                _root.Children.RemoveAll();
            }
            foreach (var (visual, shadow) in _layers)
            {
                visual.Shadow = null;
                shadow.Mask = null;
                visual.Dispose();
                shadow.Dispose();
                // GetAlphaMask is shared with XAML, not an app-owned brush. Closing it leaves
                // XAML's cached brush permanently closed on resume. Detach our reference only;
                // XAML hides/releases its backing surface on unload/platform suspend.
                // microsoft-ui-xaml: components/graphics/AlphaMask.cpp (Ensure/Hide).
            }
            _layers.Clear();
            _root?.Dispose();
            _root = null;
            return released;
        }

        public void Dispose()
        {
            _host.Loaded -= OnLoaded;
            _host.Unloaded -= OnUnloaded;
            _mask.SizeChanged -= OnSizeChanged;
            Release();
            UiResidency.Forget(_host, this);
        }
    }
}
