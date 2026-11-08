#if DEBUG_SERVER
namespace Bale.Xbox.Services;

internal static class DebugFfmpegLogFilter
{
    // Only our fixed native diagnostics are safe. Reject everything else without allocating.
    internal static string? Message(string? message) => message is not null && message.StartsWith("Bale ", StringComparison.Ordinal)
        ? "ffmpeg: " + message.TrimEnd('\r', '\n') : null;
}
#endif
