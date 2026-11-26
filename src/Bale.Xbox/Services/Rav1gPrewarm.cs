using System.Runtime.InteropServices;
using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// Starts rav1g's process-wide D3D12 device and its ~99 pipeline compiles ahead of the first rav1g stream (wrapper
/// 2.1.0-bale.48, the fork's bale/README.md "rav1g prewarm and threading notes": about 6.5 s and 77 MiB on a Series X,
/// which the first open otherwise spends itself, with 2 compile threads instead of 6). Every build (owner, 2026-11-26):
/// the Detail page asks when its selected or Auto version will play on rav1g (<see cref="Rav1gPolicy.PrewarmCandidate"/>),
/// once per process and on the thread pool; never at app start or on Home, never with an unusable rav1g.dll, never while
/// a playback runs. Debug builds' <c>av1-decoder rav1g</c> asks at once as well (docs/player-plan.md, "rav1g").
/// </summary>
internal static class Rav1gPrewarm
{
    // A plain C export outside FFmpegMediaSource (no WinRT API change): it takes no source lock, so it never waits on a
    // stream's sample thread, and it returns at once; the device and the compiles run on the fork's own threads.
    [DllImport("FFmpegInteropX.dll", ExactSpelling = true)]
    private static extern int bale_rav1g_prewarm();

    // Set once the fork was asked: later Detail pages cost one read (the fork's call is idempotent besides).
    private static int _requested;

#if DEBUG_SERVER
    // As a playback attempt captures them (PlayerPage.Attempt): the av1-decoder switch, and av1-claim 2160 (rav1g's entry).
    private static string Decoder => DebugAv1DecoderSwitch.Mode;
    private static bool Rav1gClaim => DebugAv1ClaimSwitch.MaxHeight == 2160;
#else
    private const string Decoder = "rav1g";
    private const bool Rav1gClaim = true;
#endif

    /// <summary>
    /// The Detail page shows <paramref name="version"/>, its selected or Auto version (UI thread). Returns at once; on the
    /// thread pool, the process's first version that will play on rav1g asks the fork, unless a playback runs.
    /// </summary>
    internal static void ForDetail(FileVersion? version)
    {
        if (version is null || Volatile.Read(ref _requested) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                // Started by the launch's version match, done long before a Detail page shows; it never faults.
                var library = await Rav1gLibrary.Status.ConfigureAwait(false);
                if (!Rav1gPolicy.PrewarmCandidate(Decoder, Rav1gClaim, library.Usable, DeviceIdentity.SoftwareVideoSupported, version)) return;
                // A rav1g playback's own open makes the device; beside any other, 6 compile threads would only compete with it.
                if (PlaybackResources.Lifetime.Active) return;
                if (Interlocked.Exchange(ref _requested, 1) != 0) return;
                Request();
            }
            catch (Exception ex) { Log.Info($"player: rav1g prewarm failed ({ex.GetType().Name})"); }
        });
    }

#if DEBUG_SERVER
    /// <summary>Debug <c>av1-decoder rav1g</c>: asks the fork now, on the thread pool (it answers 1 once started).</summary>
    internal static void Start()
    {
        Interlocked.Exchange(ref _requested, 1);
        _ = Task.Run(Request);
    }
#endif

    /// <summary>Asks the fork on the calling pool thread and logs its answer in one fixed line; never throws.</summary>
    private static void Request()
    {
        try
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
            var code = bale_rav1g_prewarm();
            Log.Info($"player: rav1g prewarm {Rav1gPolicy.PrewarmResult(code)} (return {code})");
        }
        catch (EntryPointNotFoundException) { Log.Info("player: rav1g prewarm unavailable (no export in FFmpegInteropX.dll)"); }
        catch (Exception ex) { Log.Info($"player: rav1g prewarm failed ({ex.GetType().Name})"); }
    }
}
