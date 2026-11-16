#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only HLS engine switch, captured per playback attempt; native after restart. "ffmpeg" plays
/// HLS through the Rust HLS reader's forward-only stream and FFmpeg (docs/rust-port.md, "HLS through
/// FFmpeg"), which also lets the console tone map an HLS remux; "native" keeps Windows'
/// AdaptiveMediaSource. A playlist the reader won't play falls back to native for that attempt.
/// </summary>
internal static class DebugHlsEngineSwitch
{
    private static string _mode = "native";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("native" or "ffmpeg")) return false;
        Volatile.Write(ref _mode, mode);
        return true;
    }
}
#endif
