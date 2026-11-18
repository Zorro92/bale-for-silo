using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// The delivered file's facts for the rav1g switch: depth from the plan, colour (Media Foundation's
/// values for the container's description) and frame rate from its catalog track; null where unknown.
/// </summary>
public sealed record Av1SourceFacts(long? BitDepth, uint? Primaries, uint? Transfer, uint? Matrix, double? FrameRate);

/// <summary>What a mount sets for rav1g; <see cref="Rav1g"/> false is today's dav1d zero-copy route, unchanged.</summary>
public sealed record Rav1gRouteDecision(bool Rav1g, int? BufferMs, int? OutputWidth, int? OutputHeight, bool ToneMap,
    Av1SourceFacts Source);

/// <summary>The media type the output guard read off a rav1g route's video sample.</summary>
public sealed record Rav1gOutputFacts(string? Subtype, bool Rav1gMark, bool ToneMapMark, uint? Primaries, uint? Transfer,
    uint? Matrix, uint? Range);

/// <summary>direct, stage, dav1d (unmarked: the open fell back; accepted) or reject with a fixed reason.</summary>
public sealed record Rav1gVerdict(string Kind, string? Reason)
{
    public bool Accepted => Kind != "reject";
}

/// <summary>One of the fork's fixed "Bale rav1g:" log lines: loaded, active, fallback, failed, output, destroyed or other.</summary>
public sealed record Rav1gLogEvent(string Kind, string Detail);

/// <summary>
/// The debug AV1 decoder switch (owner, 2026-11-18): software AV1 may decode with rav1g through the
/// FFmpegInteropX fork's GPU provider (<c>bale_av1_decoder=1</c>, wrapper 2.1.0-bale.44) instead of
/// dav1d zero-copy. Debug builds only. Implemented in Rust (native/bale-core/src/av1_decoder.rs).
/// </summary>
public static class Rav1gPolicy
{
    /// <summary>The fork's mark on direct rav1g output (media-type attribute, UINT32 1; BaleRav1gDecoder::OutputMark).</summary>
    public static readonly Guid OutputMark = new("6d553633-9287-4856-bc9f-0c23af00e184");

    /// <summary>The fork's log prefix for rav1g lines.</summary>
    public const string LogPrefix = "Bale rav1g: ";

    /// <summary>
    /// Whether this mount takes rav1g (the switch, a software AV1 route, not withdrawn by an earlier failure)
    /// and what it sets: BufferTime (200 ms, at most 5 frames: the fork's pool holds 8 images), the display
    /// size for SDR larger than the display, and the tone map on a known SDR display.
    /// </summary>
    public static Rav1gRouteDecision Route(string selected, PlaybackRouteDecision route, PlaybackPlan plan,
        IReadOnlyList<FileVersion>? versions, (int Width, int Height)? display, bool sdrDisplay, bool withdrawn) =>
        BaleCoreNative.Call("av1_decoder.route", writer =>
        {
            writer.WriteString("selected", selected);
            writer.WriteBoolean("software_video", route.SoftwareVideo);
            writer.Text("video_codec", route.VideoCodec);
            writer.Number("video_width", route.VideoWidth);
            writer.Number("video_height", route.VideoHeight);
            writer.WriteNumber("display_width", display?.Width ?? 0);
            writer.WriteNumber("display_height", display?.Height ?? 0);
            writer.WriteBoolean("sdr_display", sdrDisplay);
            writer.WriteBoolean("withdrawn", withdrawn);
            writer.Server("plan", plan);
            writer.Server("versions", versions);
        }, BaleCoreJson.Default.Rav1gRouteDecision);

    /// <summary>
    /// The guard's rule on a rav1g route: direct output needs the rav1g mark, the source's depth (8-bit NV12,
    /// more P010) and colour; stage output is marked limited BT.709 P010 (NV12 only when bale_gpu lacks P010);
    /// no mark is the open's dav1d fallback, accepted on this debug route.
    /// </summary>
    public static Rav1gVerdict OutputVerdict(Av1SourceFacts source, Rav1gOutputFacts output, bool sdrDisplay, bool allowNv12Stage) =>
        BaleCoreNative.Call("av1_decoder.output_verdict", writer =>
        {
            writer.Record("source", source, BaleCoreJson.Default.Av1SourceFacts);
            writer.Record("output", output, BaleCoreJson.Default.Rav1gOutputFacts);
            writer.WriteBoolean("sdr_display", sdrDisplay);
            writer.WriteBoolean("allow_nv12_stage", allowNv12Stage);
        }, BaleCoreJson.Default.Rav1gVerdict);

    /// <summary>
    /// A failed rav1g mount remounts the same plan once on dav1d zero-copy (no server replan), like the
    /// console-downscale-declined remount. HDMI, subtitle and start-budget failures, an open that already fell
    /// back to dav1d, and any second failure take the ordinary single replan.
    /// </summary>
    public static bool RemountOnDav1d(bool rav1gRoute, bool rav1gActive, bool remounted, bool hdrOutputFailed,
        bool subtitleFailure, bool startBudget) =>
        BaleCoreNative.Call("av1_decoder.remount_on_dav1d", writer =>
        {
            writer.WriteBoolean("rav1g_route", rav1gRoute);
            writer.WriteBoolean("rav1g_active", rav1gActive);
            writer.WriteBoolean("remounted", remounted);
            writer.WriteBoolean("hdr_output_failed", hdrOutputFailed);
            writer.WriteBoolean("subtitle_failure", subtitleFailure);
            writer.WriteBoolean("start_budget", startBudget);
        }, BaleCoreJson.Default.Boolean);

    /// <summary>The fork's rav1g log line, or null for any other line.</summary>
    public static Rav1gLogEvent? ParseLog(string line) => line.StartsWith(LogPrefix, StringComparison.Ordinal)
        ? BaleCoreNative.Call("av1_decoder.parse_log", line, BaleCoreJson.Default.String, BaleCoreJson.Default.Rav1gLogEvent)
        : null;

    /// <summary>The stats/where row: which decoder the open actually used, from the engine, the guard's mark and the logs.</summary>
    public static string Describe(string selected, bool rav1gRoute, bool libraryPresent, bool? gpuEngine, string? verdict,
        string? fallback, string? failed, string? output, bool remounted) =>
        BaleCoreNative.Call("av1_decoder.describe", writer =>
        {
            writer.WriteString("selected", selected);
            writer.WriteBoolean("rav1g_route", rav1gRoute);
            writer.WriteBoolean("library_present", libraryPresent);
            if (gpuEngine is { } gpu) writer.WriteBoolean("gpu_engine", gpu);
            else writer.WriteNull("gpu_engine");
            writer.Text("verdict", verdict);
            writer.Text("fallback", fallback);
            writer.Text("failed", failed);
            writer.Text("output", output);
            writer.WriteBoolean("remounted", remounted);
        }, BaleCoreJson.Default.String);
}
