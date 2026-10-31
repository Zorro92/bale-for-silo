using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Services;

/// <summary>
/// Keeps trying a focus move on each layout pass until it succeeds. A page coming back from a
/// detail page (or loading) has its containers before they can take focus, so a single Focus()
/// call returns false and Windows' own first-focus pick wins.
/// </summary>
internal static class FocusRetry
{
    private static readonly HashSet<PendingMove> Pending = new();

    public static void Until(FrameworkElement layoutSource, Func<bool> tryFocus, int attempts = 60)
    {
        if (!tryFocus())
        {
            var move = new PendingMove(layoutSource, tryFocus, attempts);
            Pending.Add(move);
        }
    }

    /// <summary>PageFrame keeps return focus and its scrolling hidden until the retries settle.</summary>
    public static async Task WhenSettledAsync(FrameworkElement page, CancellationToken cancellation)
    {
        // A page with no further layouts must not leave navigation/input locked indefinitely.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            while (true)
            {
                var moves = Pending.Where(move => IsWithin(move.Source, page)).ToArray();
                if (moves.Length == 0) return;
                await Task.WhenAll(moves.Select(move => move.Completion.Task)).WaitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            // A timed-out retry must not steal focus after the page has started fading in.
            foreach (var move in Pending.Where(move => IsWithin(move.Source, page)).ToArray()) move.Stop();
        }
    }

    private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ancestor)) return true;
        }

        return false;
    }

    private sealed class PendingMove
    {
        private readonly Func<bool> _tryFocus;
        private int _left;

        public FrameworkElement Source { get; }
        public TaskCompletionSource<bool> Completion { get; } = new();

        public PendingMove(FrameworkElement source, Func<bool> tryFocus, int attempts)
        {
            Source = source;
            _tryFocus = tryFocus;
            _left = attempts;
            Source.LayoutUpdated += OnLayout;
            Source.Unloaded += OnUnloaded;
        }

        private void OnLayout(object? sender, object e)
        {
            if (_tryFocus() || --_left <= 0) Stop();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Stop();

        public void Stop()
        {
            Source.LayoutUpdated -= OnLayout;
            Source.Unloaded -= OnUnloaded;
            Pending.Remove(this);
            Completion.TrySetResult(true);
        }
    }
}
