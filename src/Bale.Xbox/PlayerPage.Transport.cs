using Bale.Core.Playback;
using Windows.UI.Xaml;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    private readonly PlaybackTransportFeedback _transportFeedback = new();

    private void ShowSkipFeedback(int deltaSeconds, double target)
    {
        _transportFeedback.ShowSkip(deltaSeconds, target, Environment.TickCount64);
        UpdateTransport();
    }

    private void HideTransport()
    {
        _transportFeedback.Hide();
        TransportFeedback.Visibility = Visibility.Collapsed;
    }

    private void UpdateTransport()
    {
        try
        {
            if (!_active || _leaving || _suspended || !_transportFeedback.Visible(Environment.TickCount64)
                || _attempt is not { } attempt)
            {
                TransportFeedback.Visibility = Visibility.Collapsed;
                return;
            }
            TransportFeedback.Visibility = Visibility.Visible;
            var position = _skipTarget ?? SourcePosition(attempt);
            var duration = _duration is > 0 and { } end && double.IsFinite(end) ? end : (double?)null;
            SkipDelta.Text = PlaybackTransportFeedback.Delta(_transportFeedback.DeltaSeconds);
            SkipLanding.Text = PlaybackTransportFeedback.Clock(_transportFeedback.TargetSeconds, forceHours: duration >= 3600);
            TransportElapsed.Text = PlaybackTransportFeedback.Clock(position);
            TransportPlayed.Width = TransportTrack.ActualWidth * PlaybackTransportFeedback.Progress(position, duration);
            TransportRemaining.Text = duration is { } total ? PlaybackTransportFeedback.Remaining(position, total) : "—";
            if (duration is { } known)
            {
                var remaining = Math.Max(0, known - position);
                var speed = attempt.Media?.Player?.PlaybackSession.PlaybackRate ?? 1;
                var wallRemaining = speed > 0 && double.IsFinite(speed) ? remaining / speed : remaining;
                var now = DateTime.Now;
                // TvPlayerScrubber.kt: device-local 12/24h finish time, omitted at the end.
                if (wallRemaining > 0 && wallRemaining < (DateTime.MaxValue - now).TotalSeconds)
                    TransportRemaining.Text += " · Ends " + now.AddSeconds(wallRemaining).ToString("t");
            }
        }
        catch (Exception ex) { LogFailure("transport", ex); }
    }

    private void OnTransportSizeChanged(object sender, SizeChangedEventArgs e) => UpdateTransport();
}
