using Windows.UI.Xaml;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    private void UpdateVersionNotice()
    {
        try
        {
            var now = Environment.TickCount64;
            if (!_active || _leaving || _suspended || _state is not ("playing" or "paused")
                || _attempt is not { Plan: { } plan, Start: { } start, Media: { Started: true, FailureQueued: false } } attempt)
            {
                VersionNotice.Visibility = Visibility.Collapsed;
                return;
            }
            var notice = attempt.VersionNotice;
            notice.Observe(plan, attempt.Request.Selection, attempt.Request.Detail.Versions,
                start.ClientCapabilities.VideoDecode, now);
            if (!notice.Visible(now))
            {
                VersionNotice.Visibility = Visibility.Collapsed;
                return;
            }
            VersionNoticeText.Text = notice.Text;
            VersionNotice.Visibility = Visibility.Visible;
            // TvPlayerNoticeOverlay.kt: fade + slide from half the pill's width; fade on exit.
            // Plain elements avoid resource lookups and template transitions under Native AOT.
            var elapsed = now - notice.ShownAtMilliseconds!.Value;
            var enter = Math.Clamp(elapsed / 300.0, 0, 1);
            VersionNotice.Opacity = Math.Min(enter, Math.Clamp((Bale.Core.Playback.PlaybackVersionNotice.HideMilliseconds - elapsed) / 300.0, 0, 1));
            VersionNoticeSlide.X = -VersionNotice.ActualWidth / 2 * (1 - enter);
        }
        catch (Exception ex) { LogFailure("version notice", ex); }
    }
}
