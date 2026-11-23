#if DEBUG_SERVER
namespace Bale.Xbox.Services;

internal static class DebugFfmpegLogFilter
{
    // Only our fixed native diagnostics are safe. Reject everything else without allocating.
    // The fork's fixed decoder-pool line (wrapper bale.68, from get_format) has no "Bale " prefix.
    internal static string? Message(string? message) => message is not null
        && (message.StartsWith("Bale ", StringComparison.Ordinal) || message.StartsWith("D3D11 decoder pool: ", StringComparison.Ordinal))
        ? "ffmpeg: " + message.TrimEnd('\r', '\n') : null;
}
#endif
