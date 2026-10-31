using System.Numerics;
using Windows.UI.Composition;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox.Services;

/// <summary>
/// A normal Frame journal with two presentation slots, so both pages remain rendered during a fade.
/// Full-page navigation fades the entire shell; its nested frame fades only the tab content.
/// </summary>
public sealed partial class PageFrame : Frame
{
    // Silo Android TV: TvAppNavigation.kt:497,916–919 and TvMainShell.kt:1250–1253.
    // tween(500) uses FastOutSlowInEasing, i.e. CubicBezierEasing(0.4, 0, 0.2, 1).
    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(500);
    private static int _activeTransitions;

    private ContentPresenter? _current;
    private ContentPresenter? _spare;
    private CancellationTokenSource? _preparing;
    private CompositionScopedBatch? _batch;
    private bool _blocksInput;
    private bool _wasHitTestVisible;
    private int _generation;

    /// <summary>Root and nested transitions share an input gate; a held key must not push twice.</summary>
    internal static bool IsTransitioning => _activeTransitions > 0;

    public PageFrame()
    {
        InitializeComponent();
        // No UWP entrance/slide animation on top of the Compose-style fade.
        ContentTransitions = new TransitionCollection();
        Navigating += OnNavigating;
        Navigated += OnNavigated;
        NavigationStopped += (_, _) => FinishTransition();
        NavigationFailed += (_, _) => FinishTransition();
        Unloaded += (_, _) => FinishTransition();
        GettingFocus += OnGettingFocus;
    }

    protected override void OnApplyTemplate()
    {
        FinishTransition();
        if (_current is not null)
        {
            _current.Content = null;
        }

        base.OnApplyTemplate();
        var first = GetTemplateChild("FirstPresenter");
        var second = GetTemplateChild("SecondPresenter");
        if (first is not ContentPresenter firstPresenter || second is not ContentPresenter secondPresenter)
        {
            // Native AOT can hand template parts back as an unprojected base type. Without both
            // slots, FirstPresenter's TemplateBinding still shows the page; it just won't fade.
            Log.Info($"PageFrame: no fade slots ({first?.GetType().FullName ?? "null"}, {second?.GetType().FullName ?? "null"})");
            _current = null;
            _spare = null;
            return;
        }

        _current = firstPresenter;
        _spare = secondPresenter;
        _current.Content = Content;
        SetOpacity(_current, 1);
        SetOpacity(_spare, 0);
    }

    private void OnNavigating(object sender, NavigatingCancelEventArgs e)
    {
        // Programmatic redirects (notably session expiry) must not be dropped by the input gate.
        // Finish the old fade before starting their replacement; stale preparation is canceled.
        FinishTransition();
        if (!e.Cancel && _current?.Content is not null)
        {
            _blocksInput = true;
            _activeTransitions++;
            _wasHitTestVisible = IsHitTestVisible;
            IsHitTestVisible = false;
        }
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        if (!ReferenceEquals(e.Content, Content))
        {
            return;
        }

        ApplyTemplate();
        if (_current is null || _spare is null || ReferenceEquals(_current.Content, e.Content))
        {
            // The initial destination has no outgoing page, as with a NavHost's start destination.
            FinishTransition();
            return;
        }

        var incoming = _spare;
        _spare = _current;
        _current = incoming;
        SetOpacity(incoming, 0);
        Canvas.SetZIndex(incoming, 1);
        Canvas.SetZIndex(_spare, 0);
        incoming.Content = e.Content;

        _preparing = new CancellationTokenSource();
        _ = PrepareFadeAsync((FrameworkElement)e.Content, e.NavigationMode == NavigationMode.Back,
            _generation, _preparing.Token);
    }

    private async Task PrepareFadeAsync(FrameworkElement page, bool restoreFocus, int generation, CancellationToken cancellation)
    {
        try
        {
            await WhenLoadedAsync(page, cancellation);
            // Let Windows make its initial focus pick before restoring the remembered card. Nothing
            // in the incoming page is visible yet, including focus rings and focus-driven scrolling.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (cancellation.IsCancellationRequested) return;
                UpdateLayout();
                if (restoreFocus && page is IRestoresFocus restores) restores.RestoreFocus();
                UpdateLayout();
            });
            await FocusRetry.WhenSettledAsync(page, cancellation);
            cancellation.ThrowIfCancellationRequested();
            if (generation == _generation) StartFade();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A redirect, failed navigation or unloaded frame already retired this transition.
        }
        catch (Exception ex)
        {
            Log.Error("Page fade", ex);
            if (generation == _generation) FinishTransition();
        }
    }

    private static async Task WhenLoadedAsync(FrameworkElement page, CancellationToken cancellation)
    {
        if (page.IsLoaded) return;
        var loaded = new TaskCompletionSource<bool>();
        void OnLoaded(object sender, RoutedEventArgs args) => loaded.TrySetResult(true);
        page.Loaded += OnLoaded;
        try
        {
            await loaded.Task.WaitAsync(cancellation);
        }
        finally
        {
            page.Loaded -= OnLoaded;
        }
    }

    private void StartFade()
    {
        if (_current is null || _spare is null) return;
        var incoming = ElementCompositionPreview.GetElementVisual(_current);
        var outgoing = ElementCompositionPreview.GetElementVisual(_spare);
        var compositor = incoming.Compositor;
        using var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0), new Vector2(0.2f, 1));
        using var enter = compositor.CreateScalarKeyFrameAnimation();
        enter.InsertKeyFrame(0, 0);
        enter.InsertKeyFrame(1, 1, easing);
        enter.Duration = Fade;
        using var exit = compositor.CreateScalarKeyFrameAnimation();
        exit.InsertKeyFrame(0, 1);
        exit.InsertKeyFrame(1, 0, easing);
        exit.Duration = Fade;

        var generation = _generation;
        _batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _batch.Completed += (_, _) =>
        {
            if (generation == _generation) FinishTransition();
        };
        incoming.StartAnimation(nameof(Visual.Opacity), enter);
        outgoing.StartAnimation(nameof(Visual.Opacity), exit);
        _batch.End();
    }

    private void FinishTransition()
    {
        _generation++;
        _preparing?.Cancel();
        _preparing?.Dispose();
        _preparing = null;
        _batch?.Dispose();
        _batch = null;
        if (_current is not null) SetOpacity(_current, 1);
        if (_spare is not null)
        {
            SetOpacity(_spare, 0);
            _spare.Content = null;
        }

        if (_blocksInput)
        {
            _blocksInput = false;
            _activeTransitions--;
            IsHitTestVisible = _wasHitTestVisible;
        }
    }

    private static void SetOpacity(UIElement element, float opacity)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.Opacity = opacity;
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        try
        {
            // The outgoing page remains loaded for artwork, but must never regain focus or replace
            // the incoming page's remembered card while its layout/focus retries are settling.
            for (var node = args.NewFocusedElement; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (ReferenceEquals(node, _spare))
                {
                    args.Cancel = true;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Page fade focus", ex);
        }
    }
}
