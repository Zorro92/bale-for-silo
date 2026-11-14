#if DEBUG_SERVER
namespace Bale.Xbox.Services;

internal enum DebugPlaybackControl
{
    Play,
    Pause,
    Toggle,
}

internal static class DebugPlaybackControlParser
{
    internal static bool TryParse(string? state, out DebugPlaybackControl request)
    {
        request = default;
        switch (state)
        {
            case "play": request = DebugPlaybackControl.Play; return true;
            case "pause": request = DebugPlaybackControl.Pause; return true;
            case "toggle": request = DebugPlaybackControl.Toggle; return true;
            default: return false;
        }
    }
}
#endif
