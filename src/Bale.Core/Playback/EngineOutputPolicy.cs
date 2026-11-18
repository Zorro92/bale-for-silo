using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// The IMFMediaEngine swap-chain format of one mount. <paramref name="Target"/> is <c>bgra</c> (B8G8R8A8) or
/// <c>rgb10</c> (R10G10B10A2); <paramref name="Frames"/> is what the engine is handed (<c>nv12</c>, <c>p010</c> or
/// <c>unknown</c>); <paramref name="Reason"/> is fixed log text.
/// </summary>
public sealed record EngineOutputDecision(string Target, string Frames, string Reason)
{
    /// <summary>bale_gpu's engine target code: 0 bgra, 2 rgb10 (1 fp16 is debug only).</summary>
    public uint Code => Target == "rgb10" ? 2u : 0u;
}

/// <summary>
/// Owner's bit-depth rule (AGENTS.md): the engine's output follows the frames Bale hands it. 10-bit frames (P010:
/// a source above 8 bits, the console tone map, the SDR downscale) get R10G10B10A2; 8-bit frames (NV12) keep
/// B8G8R8A8; unknown depth keeps B8G8R8A8. Implemented in Rust (native/bale-core/src/engine_output.rs).
/// </summary>
public static class EngineOutputPolicy
{
    /// <param name="stageP010">bale_gpu's console stage writes P010 (ConsoleToneMapRuntime.SupportsP010).</param>
    public static EngineOutputDecision Decide(PlaybackPlan plan, PlaybackRouteDecision route,
        IReadOnlyList<FileVersion>? versions = null, bool stageP010 = true) =>
        BaleCoreNative.Call("engine_output.decide", writer =>
        {
            writer.Server("plan", plan);
            writer.Server("versions", versions);
            writer.Record("route", route, BaleCoreJson.Default.PlaybackRouteDecision);
            writer.WriteBoolean("stage_p010", stageP010);
        }, BaleCoreJson.Default.EngineOutputDecision);
}
