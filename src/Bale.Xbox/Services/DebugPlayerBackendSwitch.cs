#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Memory-only player switch, captured per playback attempt; engine after restart, as in release builds (owner,
/// 2026-11-17: IMFMediaEngine is the main player). "engine" plays the routed source through <see cref="EngineBackend"/>
/// with the page's own controls, Up Next, tracks and subtitles (MediaPlayer only if the engine cannot open it);
/// "mediaplayer" keeps Windows MediaPlayer for comparison.
/// </summary>
internal static class DebugPlayerBackendSwitch
{
    private static string _mode = "engine";

    internal static string Mode => Volatile.Read(ref _mode);

    internal static bool TrySet(string? mode)
    {
        if (mode is not ("mediaplayer" or "engine")) return false;
        Volatile.Write(ref _mode, mode);
        return true;
    }
}
#endif
