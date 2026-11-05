#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Xbox;

public sealed partial class PlayerPage
{
    internal async Task<(string Status, string Body)> DebugSeekAsync(DebugPlaybackSeek request)
    {
        var result = await SeekAsync(new PlaybackSeek(request.Seconds, request.Pause));
        return result.Outcome switch
        {
            SeekOutcome.Completed => ("200 OK", string.Create(CultureInfo.InvariantCulture,
                $"Position: source={result.Source:0.###}s player={result.Player:0.###}s state={result.State}\n")),
            SeekOutcome.Busy => ("409 Conflict", "player is changing its playback pipeline\n"),
            SeekOutcome.Unavailable => ("409 Conflict", "no active seekable player pipeline or supported reanchor\n"),
            SeekOutcome.InvalidPosition => ("400 Bad Request", "position is outside the current source timeline\n"),
            SeekOutcome.TimedOut => ("408 Request Timeout", "player seek did not complete within 10 seconds\n"),
            SeekOutcome.Stopped => ("409 Conflict", "player stopped during seek\n"),
            _ => ("500 Internal Server Error", $"seek failed hr=0x{unchecked((uint)result.HResult):X8}\n"),
        };
    }
}
#endif
