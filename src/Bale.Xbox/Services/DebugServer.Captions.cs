#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// bale-debug.sh captions: the open player's in-band closed captions (GET: the where line's Captions: state) and the subtitle
/// menu's CC row of a channel or its Off row (POST action=cc|cc1..cc4|off, cc = CC1) through the player's own pick path, for
/// console checks where the menu is slow to drive with keys. Fixed messages and counters only, never caption text.
/// </summary>
internal sealed partial class DebugServer
{
    private const string CaptionsUsage = "usage: GET /captions | POST /captions?action=cc|cc1|cc2|cc3|cc4|off\n";

    private static async Task<(string Status, string Body)> CaptionsAsync(string? action)
    {
        try
        {
            // The media-safe dispatcher: never format native or media exception messages.
            if (action is null)
                return await OnFfmpegUiAsync(() => CurrentPage(out _) is PlayerPage player
                    ? ("200 OK", "Captions: " + player.DebugCaptions + "\n")
                    : ("409 Conflict", "no active player page\n"));
            Log.Info($"Debug captions: {action}");
            var pick = await OnFfmpegUiAsync(() => CurrentPage(out _) is PlayerPage player
                ? player.DebugPickCaptionsAsync(action)
                : Task.FromResult((Status: "409 Conflict", Body: "no active player page\n")));
            return await pick;
        }
        catch (Exception ex)
        {
            var failure = $"debug captions failed hr=0x{unchecked((uint)ex.HResult):X8}\n";
            Log.Info(failure.TrimEnd());
            return ("500 Internal Server Error", failure);
        }
    }
}
#endif
