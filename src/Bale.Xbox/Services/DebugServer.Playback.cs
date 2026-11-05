#if DEBUG_SERVER
namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    private static async Task<(string Status, string Body)> SeekPlaybackAsync(string? seconds, string? pause)
    {
        if (!DebugPlaybackSeek.TryParse(seconds, pause, out var request))
            return ("400 Bad Request", "seconds must be finite and non-negative; pause must be true or false\n");
        try
        {
            // Use the media-safe dispatcher: never format native/media exception messages.
            var seek = await OnFfmpegUiAsync(() => CurrentPage(out _) is PlayerPage player
                ? player.DebugSeekAsync(request)
                : Task.FromResult((Status: "409 Conflict", Body: "no active player pipeline\n")));
            return await seek;
        }
        catch (Exception ex)
        {
            var failure = $"debug seek failed hr=0x{unchecked((uint)ex.HResult):X8}\n";
            Log.Info(failure.TrimEnd());
            return ("500 Internal Server Error", failure);
        }
    }
}
#endif
