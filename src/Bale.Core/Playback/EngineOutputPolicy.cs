using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// The IMFMediaEngine swap-chain format of one mount. <paramref name="Target"/> is <c>bgra</c> (B8G8R8A8) or
/// <c>rgb10</c> (R10G10B10A2); <paramref name="Frames"/> is what the engine is handed (<c>nv12</c>, <c>p010</c> or
/// <c>unknown</c>); <paramref name="Reason"/> is fixed log text. <paramref name="StageOutputDepth"/> is the console
/// stage's requested depth: 8 asks the fork for dithered NV12 (<c>bale_tonemap_output_depth=8</c>), 10 keeps P010
/// (key unset), 0 means no stage; <paramref name="StageReason"/> is fixed log text for it.
/// </summary>
public sealed record EngineOutputDecision(string Target, string Frames, string Reason, int StageOutputDepth = 0,
    string StageReason = "no console stage")
{
    /// <summary>bale_gpu's engine target code: 0 bgra, 2 rgb10 (1 fp16 is debug only).</summary>
    public uint Code => Target == "rgb10" ? 2u : 0u;
}

/// <summary>
/// Owner's bit-depth rule (AGENTS.md): work at source depth, deliver at display depth. P010 gets R10G10B10A2
/// for HDR passthrough or SDR on a 30+ bpp display; AV1 keeps 10-bit output until rav1g is stable. Other P010,
/// NV12 and unknown frames get B8G8R8A8. Phase 2: on an 8-bit SDR display the console stage writes dithered NV12
/// (AV1 excepted) when bale_gpu dithers it. Implemented in Rust (native/bale-core/src/engine_output.rs).
/// </summary>
public static class EngineOutputPolicy
{
    /// <param name="stageP010">bale_gpu's console stage writes P010 (ConsoleToneMapRuntime.SupportsP010).</param>
    /// <param name="displayBitsPerPixel">The current HDMI mode's depth; unknown caps SDR at 8 bits.</param>
    /// <param name="ceiling">display (default), or source to keep P010 at 10 bits regardless of the display.</param>
    /// <param name="stageNv12Dithered">bale_gpu dithers the stage's NV12 (ConsoleToneMapRuntime.SupportsDitheredNv12).</param>
    public static EngineOutputDecision Decide(PlaybackPlan plan, PlaybackRouteDecision route,
        IReadOnlyList<FileVersion>? versions = null, bool stageP010 = true,
        int? displayBitsPerPixel = null, string ceiling = "display", bool stageNv12Dithered = false) =>
        BaleCoreNative.Call("engine_output.decide", writer =>
        {
            writer.Server("plan", plan);
            writer.Server("versions", versions);
            writer.Record("route", route, BaleCoreJson.Default.PlaybackRouteDecision);
            writer.WriteBoolean("stage_p010", stageP010);
            writer.WriteBoolean("stage_nv12_dithered", stageNv12Dithered);
            if (displayBitsPerPixel is { } bpp) writer.WriteNumber("display_bits_per_pixel", bpp);
            else writer.WriteNull("display_bits_per_pixel");
            writer.WriteString("ceiling", ceiling);
        }, BaleCoreJson.Default.EngineOutputDecision);
}
