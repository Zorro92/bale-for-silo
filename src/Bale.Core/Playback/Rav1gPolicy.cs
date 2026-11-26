using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// The delivered file's facts for the rav1g switch: depth from the plan, colour (Media Foundation's
/// values for the container's description) and frame rate from its catalog track; null where unknown.
/// </summary>
public sealed record Av1SourceFacts(long? BitDepth, uint? Primaries, uint? Transfer, uint? Matrix, double? FrameRate);

/// <summary>
/// What a mount sets for rav1g; <see cref="Rav1g"/> false is the dav1d D3D11 CPU route.
/// <see cref="DecodeAheadFrames"/> is <c>bale_decode_ahead_frames</c> on the rav1g route (null: the software default).
/// <see cref="DownscaleStage"/>: rav1g's display pass (or the fork's stage) scales this SDR source down to the display, a
/// stage for EngineOutputPolicy's depth like the console downscale (route.rs keeps AV1 off that one).
/// </summary>
public sealed record Rav1gRouteDecision(bool Rav1g, int? BufferMs, int? OutputWidth, int? OutputHeight, bool ToneMap,
    Av1SourceFacts Source, int? DecodeAheadFrames = null, int? OutputTextures = null, bool DownscaleStage = false);

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

/// <summary>Whether rav1g.dll is usable (the fork will load it) and fixed words saying why or why not.</summary>
public sealed record Rav1gLibraryStatus(bool Usable, string Detail);

/// <summary>
/// The AV1 decoder (owner, 2026-11-26: rav1g is the default in every build, after debug builds since 2026-11-19): software
/// AV1 decodes with rav1g through the FFmpegInteropX fork's GPU provider (<c>bale_av1_decoder=1</c>, wrapper 2.1.0-bale.44)
/// wherever rav1g.dll is usable, else with dav1d on D3D11. The chain: rav1g, the fork's in-open dav1d fallback (at most
/// 1080 lines), one dav1d remount (<see cref="RemountOnDav1d"/>), then the ordinary replan or server transcode. Debug
/// builds can switch it per attempt (<c>av1-decoder</c>). Implemented in Rust (native/bale-core/src/av1_decoder.rs).
/// </summary>
public static class Rav1gPolicy
{
    /// <summary>The exports the fork resolves from rav1g.dll (BaleRav1gExports; scripts/rav1g-common.ps1).</summary>
    public static IReadOnlyList<string> LibraryExports { get; } = Array.AsReadOnly(BaleCoreNative.Call(
        "av1_decoder.library_exports", (string?)null, BaleCoreJson.Default.String, BaleCoreJson.Default.StringArray));

    /// <summary>The rav1g interface version the pinned wrapper is written against (scripts/rav1g.json abi_version).</summary>
    public static uint LibraryAbiVersion { get; } = BaleCoreNative.Call(
        "av1_decoder.library_abi_version", (string?)null, BaleCoreJson.Default.String, BaleCoreJson.Default.UInt32);

    /// <summary>
    /// Whether rav1g.dll is usable, as the fork's loader will judge it: packaged, loaded, every export of
    /// <see cref="LibraryExports"/>, interface <see cref="LibraryAbiVersion"/>. Not usable keeps AV1 on dav1d at 1080p60.
    /// </summary>
    public static Rav1gLibraryStatus Library(bool present, bool loaded, string? missingExport, uint? abiVersion) =>
        BaleCoreNative.Call("av1_decoder.library_status", writer =>
        {
            writer.WriteBoolean("present", present);
            writer.WriteBoolean("loaded", loaded);
            writer.Text("missing_export", missingExport);
            if (abiVersion is { } abi) writer.WriteNumber("abi_version", abi);
            else writer.WriteNull("abi_version");
        }, BaleCoreJson.Default.Rav1gLibraryStatus);

    /// <summary>The fork's mark on direct rav1g output (media-type attribute, UINT32 1; BaleRav1gDecoder::OutputMark).</summary>
    public static readonly Guid OutputMark = new("6d553633-9287-4856-bc9f-0c23af00e184");

    /// <summary>The fork's log prefix for rav1g lines.</summary>
    public const string LogPrefix = "Bale rav1g: ";

    /// <summary>
    /// Whether this mount takes rav1g (the attempt's decoder, a usable rav1g.dll, a software AV1 route, not withdrawn by
    /// an earlier failure) and what it sets: BufferTime (250 ms at every resolution), 8 decode-ahead frames, the
    /// player-side texture cap (frames in 250 ms + 4 in flight + 2, by frame rate; none when unknown), the display size
    /// for SDR larger than the display, and the tone map on a known SDR display.
    /// </summary>
    /// <param name="selected">The attempt's AV1 decoder: rav1g in release builds, the captured debug switch otherwise.</param>
    /// <param name="libraryUsable">rav1g.dll is usable (<see cref="Library"/>); false keeps dav1d.</param>
    public static Rav1gRouteDecision Route(string selected, PlaybackRouteDecision route, PlaybackPlan plan,
        IReadOnlyList<FileVersion>? versions, (int Width, int Height)? display, bool sdrDisplay, bool withdrawn,
        bool libraryUsable) =>
        BaleCoreNative.Call("av1_decoder.route", writer =>
        {
            writer.WriteString("selected", selected);
            writer.WriteBoolean("library_usable", libraryUsable);
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
    /// more P010) and colour; stage output is marked limited BT.709 P010, or NV12 when bale_gpu lacks P010 or Bale asked
    /// for dithered 8-bit output (<paramref name="nv12Requested"/>, an 8-bit SDR display); no mark is the open's dav1d
    /// fallback, accepted (the chain's next decoder).
    /// </summary>
    public static Rav1gVerdict OutputVerdict(Av1SourceFacts source, Rav1gOutputFacts output, bool sdrDisplay, bool allowNv12Stage,
        bool nv12Requested = false) =>
        BaleCoreNative.Call("av1_decoder.output_verdict", writer =>
        {
            writer.Record("source", source, BaleCoreJson.Default.Av1SourceFacts);
            writer.Record("output", output, BaleCoreJson.Default.Rav1gOutputFacts);
            writer.WriteBoolean("sdr_display", sdrDisplay);
            writer.WriteBoolean("allow_nv12_stage", allowNv12Stage);
            writer.WriteBoolean("nv12_requested", nv12Requested);
        }, BaleCoreJson.Default.Rav1gVerdict);

    /// <summary>
    /// A failed rav1g mount at a known height of at most 1080 remounts once on dav1d (no server replan), like the
    /// console-downscale-declined remount. HDMI, subtitle and start-budget failures, an open that already fell
    /// back to dav1d, taller/unknown sources and any second failure take the ordinary single replan.
    /// </summary>
    public static bool RemountOnDav1d(bool rav1gRoute, bool rav1gActive, bool remounted, bool hdrOutputFailed,
        bool subtitleFailure, bool startBudget, long? sourceHeight) =>
        BaleCoreNative.Call("av1_decoder.remount_on_dav1d", writer =>
        {
            writer.WriteBoolean("rav1g_route", rav1gRoute);
            writer.WriteBoolean("rav1g_active", rav1gActive);
            writer.WriteBoolean("remounted", remounted);
            writer.WriteBoolean("hdr_output_failed", hdrOutputFailed);
            writer.WriteBoolean("subtitle_failure", subtitleFailure);
            writer.WriteBoolean("start_budget", startBudget);
            writer.Number("source_height", sourceHeight);
        }, BaleCoreJson.Default.Boolean);

    /// <summary>The fork's rav1g log line, or null for any other line.</summary>
    public static Rav1gLogEvent? ParseLog(string line) => line.StartsWith(LogPrefix, StringComparison.Ordinal)
        ? BaleCoreNative.Call("av1_decoder.parse_log", line, BaleCoreJson.Default.String, BaleCoreJson.Default.Rav1gLogEvent)
        : null;

    /// <summary>Whether the pinned wrapper exports <c>bale_rav1g_prewarm</c> from FFmpegInteropX.dll (2.1.0-bale.48+).</summary>
    public static bool PrewarmSupported(string wrapperVersion) =>
        BaleCoreNative.Call("av1_decoder.prewarm_supported", wrapperVersion, BaleCoreJson.Default.String, BaleCoreJson.Default.Boolean);

    /// <summary>The log words for <c>bale_rav1g_prewarm</c>'s return: 0 started, 1 already started, -1 failed.</summary>
    public static string PrewarmResult(int code) =>
        BaleCoreNative.Call("av1_decoder.prewarm_result", code, BaleCoreJson.Default.Int32, BaleCoreJson.Default.String);

    /// <summary>The stats/where row: which decoder the open actually used, from the engine, the guard's mark and the logs.</summary>
    /// <param name="library">Why rav1g.dll is not usable (<see cref="Rav1gLibraryStatus.Detail"/>); null when it is.</param>
    public static string Describe(string selected, bool rav1gRoute, string? library, bool? gpuEngine, string? verdict,
        string? fallback, string? failed, string? output, bool remounted) =>
        BaleCoreNative.Call("av1_decoder.describe", writer =>
        {
            writer.WriteString("selected", selected);
            writer.WriteBoolean("rav1g_route", rav1gRoute);
            writer.Text("library", library);
            if (gpuEngine is { } gpu) writer.WriteBoolean("gpu_engine", gpu);
            else writer.WriteNull("gpu_engine");
            writer.Text("verdict", verdict);
            writer.Text("fallback", fallback);
            writer.Text("failed", failed);
            writer.Text("output", output);
            writer.WriteBoolean("remounted", remounted);
        }, BaleCoreJson.Default.String);
}
