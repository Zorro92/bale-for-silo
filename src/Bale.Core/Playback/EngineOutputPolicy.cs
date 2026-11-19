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
/// Owner's bit-depth rule (AGENTS.md): work at source depth, deliver at display depth. P010 gets R10G10B10A2
/// for HDR passthrough or SDR on a 30+ bpp display; AV1 keeps 10-bit output until rav1g is stable. Other P010,
/// NV12 and unknown frames get B8G8R8A8. Implemented in Rust (native/bale-core/src/engine_output.rs).
/// </summary>
public static class EngineOutputPolicy
{
    /// <param name="stageP010">bale_gpu's console stage writes P010 (ConsoleToneMapRuntime.SupportsP010).</param>
    /// <param name="displayBitsPerPixel">The current HDMI mode's depth; unknown caps SDR at 8 bits.</param>
    /// <param name="ceiling">display (default), or source to keep P010 at 10 bits regardless of the display.</param>
    public static EngineOutputDecision Decide(PlaybackPlan plan, PlaybackRouteDecision route,
        IReadOnlyList<FileVersion>? versions = null, bool stageP010 = true,
        int? displayBitsPerPixel = null, string ceiling = "display") =>
        BaleCoreNative.Call("engine_output.decide", writer =>
        {
            writer.Server("plan", plan);
            writer.Server("versions", versions);
            writer.Record("route", route, BaleCoreJson.Default.PlaybackRouteDecision);
            writer.WriteBoolean("stage_p010", stageP010);
            if (displayBitsPerPixel is { } bpp) writer.WriteNumber("display_bits_per_pixel", bpp);
            else writer.WriteNull("display_bits_per_pixel");
            writer.WriteString("ceiling", ceiling);
        }, BaleCoreJson.Default.EngineOutputDecision);
}
