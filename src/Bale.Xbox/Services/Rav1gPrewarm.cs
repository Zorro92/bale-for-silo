#if DEBUG_SERVER
using System.Runtime.InteropServices;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// Starts rav1g's process-wide D3D12 device and pipeline compiles ahead of the first rav1g stream (wrapper 2.1.0-bale.48,
/// the fork's bale/README.md "rav1g prewarm and threading notes": about 6.5 s of compiles on a Series X, which a first
/// open otherwise does with 2 threads). Debug only: the AV1 decoder switch calls it when set to rav1g, never production
/// or app start. If rav1g reaches production, the call moves to Detail opening an AV1 version (docs/player-plan.md).
/// </summary>
internal static class Rav1gPrewarm
{
    // A plain C export (no WinRT API change); it returns at once, the work runs on the fork's own thread.
    [DllImport("FFmpegInteropX.dll", ExactSpelling = true)]
    private static extern int bale_rav1g_prewarm();

    /// <summary>Asks for the prewarm on the thread pool and logs the result; never throws, never blocks the caller.</summary>
    internal static void Start()
    {
        if (!Rav1gPolicy.PrewarmSupported(FfmpegWrapperVersion.Value))
        {
            Log.Info("player: rav1g prewarm unavailable (wrapper before 2.1.0-bale.48)");
            return;
        }
        if (!Rav1gLibrary.Present)
        {
            Log.Info("player: rav1g prewarm skipped (rav1g.dll not packaged)");
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                var code = bale_rav1g_prewarm();
                Log.Info($"player: rav1g prewarm {Rav1gPolicy.PrewarmResult(code)} (return {code})");
            }
            catch (EntryPointNotFoundException) { Log.Info("player: rav1g prewarm unavailable (no export in FFmpegInteropX.dll)"); }
            catch (Exception ex) { Log.Info($"player: rav1g prewarm failed ({ex.GetType().Name})"); }
        });
    }
}
#endif
