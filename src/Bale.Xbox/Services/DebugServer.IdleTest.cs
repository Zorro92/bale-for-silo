#if DEBUG_SERVER
using System.Text;
using Windows.Media;
using Windows.System.Display;

namespace Bale.Xbox.Services;

/// <summary>
/// Input-idle experiments. About 10 s after the last input the console drops its CPU clock and DRAM
/// bandwidth until the next input (docs/player-plan.md). These try the documented "media is
/// playing" signals instead of injecting input: the media session's Playing status (Microsoft's
/// Xbox media samples) and a DisplayRequest pulse (Kodi's win10 WinIdleTimer resets the display
/// idle timer that way).
/// </summary>
internal sealed partial class DebugServer
{
    internal const string IdleTestUsage =
        "usage: GET /idle-test or POST /idle-test?action={display-pulse|smtc-playing|smtc-stopped}\n";

    private static DisplayRequest? _idlePulse;

    private static bool IsIdleTestAction(string? action) =>
        action is "display-pulse" or "smtc-playing" or "smtc-stopped";

    /// <summary>The player's own media session while one is mounted, else the view's.</summary>
    private static SystemMediaTransportControls IdleTestControls(out string owner)
    {
        if (CurrentPage(out _) is PlayerPage page && page.DebugMediaPlayer is { } player)
        {
            owner = "player (command manager " + (player.CommandManager.IsEnabled ? "on" : "off") + ")";
            return player.SystemMediaTransportControls;
        }
        owner = "view";
        return SystemMediaTransportControls.GetForCurrentView();
    }

    private static string DescribeIdleTest()
    {
        var controls = IdleTestControls(out var owner);
        return new StringBuilder("media session: ").Append(owner)
            .Append(" enabled=").Append(controls.IsEnabled ? "on" : "off")
            .Append(" play=").Append(controls.IsPlayEnabled ? "on" : "off")
            .Append(" pause=").Append(controls.IsPauseEnabled ? "on" : "off")
            .Append(" status=").Append(controls.PlaybackStatus)
            .Append('\n').ToString();
    }

    private static string RunIdleTest(string action)
    {
        if (action == "display-pulse")
        {
            // Kodi's order: activate, then release at once. A fresh request, so the player's own
            // continuous request (PlayerPage._display) is untouched.
            _idlePulse ??= new DisplayRequest();
            _idlePulse.RequestActive();
            _idlePulse.RequestRelease();
            return "display request pulsed\n";
        }

        var controls = IdleTestControls(out _);
        controls.IsEnabled = true;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.PlaybackStatus = action == "smtc-playing" ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Stopped;
        return DescribeIdleTest();
    }
}
#endif
