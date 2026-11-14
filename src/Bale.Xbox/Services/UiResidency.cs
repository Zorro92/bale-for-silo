using System.Runtime.CompilerServices;
using Bale.Core.Playback;
using Windows.Foundation;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Shapes;

namespace Bale.Xbox.Services;

/// <summary>
/// Drop decoded artwork, not the navigation/model tree. Empty BitmapImages stay bound so x:Bind,
/// recycled rail containers, focus and scroll memories keep their identity. Only visible sources
/// are decoded again; a later layout/navigation/scroll reveals the rest through the same gate.
/// </summary>
internal static class UiResidency
{
    private static readonly UiResidencyState State = new(ex =>
        Log.Info($"ui resources: cleanup hr=0x{unchecked((uint)ex.HResult):X8}"));
    private static readonly ConditionalWeakTable<FrameworkElement, List<IUiResource>> Hosts = new();
    private static readonly ConditionalWeakTable<FrameworkElement, List<IUiResource>> SourceOwners = new();
    private static readonly ConditionalWeakTable<ScrollViewer, object> ScrollViewers = new();
    private static readonly List<WeakReference<FrameworkElement>> Pages = new();
    private static FrameworkElement? _root;
    private static bool _queued;
    private static bool _hasReleased;
    private static string _description = "foreground releases=0 resumes=0";

    internal static bool IsSuppressed => State.IsSuppressed;
    internal static int Generation => State.Generation;

    internal static void Attach(FrameworkElement root)
    {
        if (_root is not null) _root.LayoutUpdated -= OnLayoutUpdated;
        _root = root;
        root.LayoutUpdated += OnLayoutUpdated;
    }

    internal static void TrackPage(FrameworkElement page)
    {
        Pages.RemoveAll(reference => !reference.TryGetTarget(out _));
        if (!Pages.Any(reference => reference.TryGetTarget(out var old) && ReferenceEquals(old, page)))
            Pages.Add(new(page));
        QueueVisible();
    }

    internal static void NavigationChanged(PageFrame frame, object content)
    {
        if (content is FrameworkElement page) TrackPage(page);
        if (ReferenceEquals(frame, _root) && content is not PlayerPage)
            Resume(UiResidencyReason.Playback);
    }

    internal static void Register(IUiResource resource) => State.Register(resource);

    internal static void Register(FrameworkElement host, IUiResource resource)
    {
        Hosts.GetOrCreateValue(host).Add(resource);
        Register(resource);
    }

    internal static void Forget(FrameworkElement host, IUiResource resource)
    {
        if (Hosts.TryGetValue(host, out var resources)) resources.Remove(resource);
    }

    internal static void EnterPlayback()
    {
        if (State.IsPlayback) return; // pause/retry/replan keep the same hidden-UI lifetime
        Release(UiResidencyReason.Playback);
        _ = PlaybackMemoryRelease.Shared.Schedule();
    }

    internal static void Release(UiResidencyReason reason = UiResidencyReason.Background)
    {
        _hasReleased = true;
        // A start waits for the outgoing fade while holding the player's lifecycle lock. A
        // hidden compositor may stop completing animations: settle that fade now so suspend
        // shutdown can acquire the lock, rather than waiting on an offscreen animation forever.
        if (reason == UiResidencyReason.Background && _root is PageFrame frame) frame.FinishForBackground();
        // Discover XAML/package ImageBrushes as well as Artwork's registered model bitmaps. The
        // weak page list includes unloaded Frame cache pages and both PageFrame fade slots.
        if (_root is not null) CaptureSources(_root);
        foreach (var reference in Pages.ToArray())
            if (reference.TryGetTarget(out var page)) CaptureSources(page);
        VisitPopups(visibleOnly: false);
        var counts = State.Release(reason);
        var logos = Artwork.ReleaseMemoryCache();
        AmbientTint.ReleaseMemory();
        UpdateDescription();
        Log.Info($"ui resources: {(reason == UiResidencyReason.Playback ? "playback" : "suspend")} released {counts} logos={logos} samples_cancelled=true");
    }

    internal static void Resume(UiResidencyReason reason = UiResidencyReason.Background)
    {
        if (!State.Resume(reason)) return;
        // No navigation, UpdateLayout, focus or ItemsSource changes here. A low-priority pass
        // follows if LeavingBackground precedes the first loaded/measured foreground layout.
        RestoreVisible();
        Log.Info($"ui resources: {(reason == UiResidencyReason.Playback ? "playback-return" : "resume")} restored {State.Restored} (visible; remaining lazy)");
        QueueVisible();
    }

    private static void OnLayoutUpdated(object? sender, object args) => QueueVisible();
    private static void OnViewChanged(object sender, ScrollViewerViewChangedEventArgs args) => QueueVisible();

    private static void QueueVisible()
    {
        if (!_hasReleased || _queued || IsSuppressed || _root is null) return;
        _queued = true;
        _ = _root.Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
        {
            _queued = false;
            if (!IsSuppressed) RestoreVisible();
        });
    }

    private static void RestoreVisible()
    {
        if (_root is null || IsSuppressed) return;
        try
        {
            Visit(_root, visibleOnly: true);
            VisitPopups(visibleOnly: true);
        }
        catch (Exception ex) { Log.Info($"ui resources: visible scan hr=0x{unchecked((uint)ex.HResult):X8}"); }
        UpdateDescription();
    }

    private static void VisitPopups(bool visibleOnly)
    {
        if (Window.Current is not { } window) return;
        foreach (var popup in VisualTreeHelper.GetOpenPopups(window))
            if (!visibleOnly && popup.Child is FrameworkElement host) CaptureSources(host);
            else if (popup.Child is { } child) Visit(child, visibleOnly);
    }

    private static void CaptureSources(FrameworkElement page)
    {
        // An Image.Source held only by native XAML need not keep its original managed wrapper
        // alive after the logo cache is cleared. Anchor the small reload recipes to the page,
        // including for one-time x:Bind. Replace this list on each sweep so retired art can die.
        var sources = new List<IUiResource>();
        try { Visit(page, visibleOnly: false, sources); }
        catch (Exception ex) { Log.Info($"ui resources: source scan hr=0x{unchecked((uint)ex.HResult):X8}"); }
        SourceOwners.Remove(page);
        SourceOwners.Add(page, sources);
    }

    private static void Visit(DependencyObject node, bool visibleOnly, List<IUiResource>? sources = null, Rect? viewport = null)
    {
        if (visibleOnly && node is FrameworkElement element
            && (!element.IsLoaded || element.Visibility != Visibility.Visible)) return;
        var bounds = viewport ?? Window.Current.Bounds;
        if (node is ScrollViewer scroller)
        {
            // Independent/composited scrolling need not produce LayoutUpdated. Observe the
            // offset as well, so already-realized rail/grid containers reload when revealed.
            if (!ScrollViewers.TryGetValue(scroller, out _))
            {
                ScrollViewers.Add(scroller, new object());
                scroller.ViewChanged += OnViewChanged;
            }
            if (visibleOnly && scroller.ActualWidth > 0 && scroller.ActualHeight > 0)
            {
                bounds.Intersect(Bounds(scroller));
                if (bounds.Width <= 0 || bounds.Height <= 0) return;
            }
        }
        // Only transform actual image/shadow hosts, not every text/template node. Carry the
        // intersected scroll viewport down the tree rather than rewalking every ancestor per card.
        if (node is FrameworkElement host && visibleOnly && Hosts.TryGetValue(host, out var resources)
            && IsVisible(host, bounds))
            foreach (var resource in resources.ToArray()) State.Restore(resource);

        if (node is Image image && (!visibleOnly || IsVisible(image, bounds))) ImageSource(image.Source, visibleOnly, sources);
        if (node is Border border)
        {
            Brush(border.Background, node as FrameworkElement, bounds, visibleOnly, sources);
            Brush(border.BorderBrush, node as FrameworkElement, bounds, visibleOnly, sources);
        }
        else if (node is Panel panel) Brush(panel.Background, node as FrameworkElement, bounds, visibleOnly, sources);
        else if (node is Control control)
        {
            Brush(control.Background, node as FrameworkElement, bounds, visibleOnly, sources);
            Brush(control.Foreground, node as FrameworkElement, bounds, visibleOnly, sources);
        }
        else if (node is Shape shape)
        {
            Brush(shape.Fill, node as FrameworkElement, bounds, visibleOnly, sources);
            Brush(shape.Stroke, node as FrameworkElement, bounds, visibleOnly, sources);
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            Visit(VisualTreeHelper.GetChild(node, i), visibleOnly, sources, bounds);
    }

    private static void Brush(Brush? brush, FrameworkElement? owner, Rect bounds, bool restore, List<IUiResource>? sources)
    {
        if (brush is ImageBrush image && (!restore || owner is not null && IsVisible(owner, bounds)))
            ImageSource(image.ImageSource, restore, sources);
    }

    private static void ImageSource(ImageSource? source, bool restore, List<IUiResource>? sources)
    {
        if (source is not BitmapImage bitmap) return;
        if (Artwork.Track(bitmap) is not { } resource) return;
        sources?.Add(resource);
        if (restore) State.Restore(resource);
    }

    private static Rect Bounds(FrameworkElement element) => element.TransformToVisual(null)
        .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static bool IsVisible(FrameworkElement element, Rect bounds)
    {
        // Opacity deliberately isn't tested: a logo waits at opacity zero for ImageOpened, and
        // an incoming PageFrame fade starts at zero while its visible artwork is prepared.
        if (!element.IsLoaded || element.Visibility != Visibility.Visible) return false;
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0) return true;
        try
        {
            var rect = Bounds(element);
            rect.Intersect(bounds);
            return rect.Width > 0 && rect.Height > 0;
        }
        catch (ArgumentException) { return false; } // A just-retired fade slot is no longer rooted.
    }

    private static void UpdateDescription() => Volatile.Write(ref _description,
        $"{(State.IsBackground ? "background" : State.IsPlayback ? "playback" : "foreground")} releases={State.Releases} resumes={State.Resumes} "
        + $"resident {State.Resident}; released {State.LastReleased}; restored {State.Restored}");

    // Debug server is on a worker: never touch XAML or iterate the UI registry from that thread.
    internal static string Describe() => Volatile.Read(ref _description);
}
