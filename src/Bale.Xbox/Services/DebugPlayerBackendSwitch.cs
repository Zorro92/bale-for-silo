#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only player switch, captured per playback attempt; mediaplayer after restart. "engine" plays the
/// routed source through <see cref="EngineBackend"/> (IMFMediaEngine rendering mode) with the page's own
/// controls, Up Next, tracks and subtitles; "mediaplayer" keeps Windows MediaPlayer (owner, 2026-11-17: the
/// engine becomes the main path once it matches).
/// </summary>
internal static class DebugPlayerBackendSwitch
{
    private static string _mode = "mediaplayer";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("mediaplayer" or "engine")) return false;
        Volatile.Write(ref _mode, mode);
        return true;
    }
}
#endif
