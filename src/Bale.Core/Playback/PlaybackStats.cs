using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>The playback phase the Stats tab's "Playback status" row names (Apple's playerStatsPhase labels).</summary>
public enum PlaybackStatsPhase
{
    Loading,
    Playing,
    Paused,
    Seeking,
    Rebuffering,
    Ended,
    Error,
}

/// <summary>One section of the Stats tab: Apple's id (playback, stream or engine), its title and its label/value rows.</summary>
public sealed record PlaybackStatsSection(string Id, string Title, IReadOnlyList<(string Label, string Value)> Rows);

/// <summary>
/// An engine-independent, immutable readout for the Options panel's Stats tab and the debug overlay.
/// The Stats tab is Apple's tvOS panel (PlaybackStatsPanel.swift, PlaybackStats.swift): Playback, Stream and Engine, with
/// Apple's English labels and value formats (owner, 2026-11-27). A row Bale cannot measure is left out, never faked.
/// </summary>
public sealed record PlaybackStats
{
    public string? BackendDisplayName { get; init; }
    public string? BackendRoute { get; init; }
    /// <summary>Playback: how the file reaches the device, in viewer terms ("Direct play").</summary>
    public string? PlayMethod { get; init; }
    /// <summary>Playback: the file's container, as Apple names it ("MKV").</summary>
    public string? Container { get; init; }
    /// <summary>Diagnostics: the decision reason, then the version swap and the server's warnings, one line each.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
    /// <summary>Playback: the selected subtitle track, or "Off".</summary>
    public string? Subtitles { get; init; }
    /// <summary>Playback rate: shown only while it is not 1×.</summary>
    public double? PlaybackRate { get; init; }
    /// <summary>Stream: the playback phase.</summary>
    public PlaybackStatsPhase? Phase { get; init; }
    /// <summary>Stream: network throughput, the throughput meter's estimate (never the media bitrate).</summary>
    public long? EstimatedBandwidthBps { get; init; }
    /// <summary>Stream: the engine's dropped-frame count; null where the route does not measure it.</summary>
    public long? DroppedFrames { get; init; }
    /// <summary>Stream: Missed frames, Bale's smoothness measure (engine drops plus stall equivalents).</summary>
    public PlaybackSmoothnessSnapshot? Smoothness { get; init; }
    /// <summary>Engine: the server's delivery, as Apple names it ("Server Remux HLS").</summary>
    public string? Delivery { get; init; }
    /// <summary>Engine: the decoders in use, for Video and Audio.</summary>
    public string? VideoDecoderName { get; init; }
    public string? AudioDecoderName { get; init; }
    /// <summary>Playback › Video: the codec id, shown as Apple labels it ("HEVC").</summary>
    public string? VideoCodec { get; init; }
    /// <summary>Playback › Video: the picture size, "3840×2160" (see <see cref="Dimensions"/>).</summary>
    public string? Resolution { get; init; }
    public double? FrameRate { get; init; }
    /// <summary>Playback › Dynamic range: "SDR", "HDR10", "Dolby Vision"...</summary>
    public string? HdrMode { get; init; }
    /// <summary>Playback › Audio: the codec id (shown as "EAC3") and the channel count (shown as "5.1").</summary>
    public string? AudioCodec { get; init; }
    public long? AudioChannelCount { get; init; }
    /// <summary>The server's decision text ("Direct play (above max resolution)"), which the Info pane shows.</summary>
    public string? Decision { get; init; }
    /// <summary>Debug overlay only: the file's own description (Apple's Source row names a host, which Bale never shows).</summary>
    public string? Source { get; init; }
    /// <summary>Debug overlay only.</summary>
    public long? MediaBitrateBps { get; init; }
    public int BufferingCount { get; init; }
    public double BufferingSeconds { get; init; }

    /// <summary>
    /// The Stats tab (every build): Apple's three sections in order, Playback, Stream and Engine. A section without rows is
    /// left out, as tvOS leaves it out.
    /// </summary>
    public IReadOnlyList<PlaybackStatsSection> OfficialSections()
    {
        var sections = new List<PlaybackStatsSection>(3);
        AddSection(sections, "playback", "Playback", PlaybackRows());
        AddSection(sections, "stream", "Stream", StreamRows());
        AddSection(sections, "engine", "Engine", EngineRows());
        return sections;
    }

    /// <summary>The debug overlay's flat rows: the official rows, then Bale's own diagnostics (the overlay adds its extras).</summary>
    public IReadOnlyList<(string Label, string Value)> HudRows()
    {
        var rows = new List<(string Label, string Value)>();
        foreach (var row in OfficialSections().SelectMany(section => section.Rows))
            rows.Add(row.Label.Length == 0 ? ("Diagnostics", row.Value) : row);
        if (!string.IsNullOrWhiteSpace(Source)) rows.Add(("Source", Source));
        if (MediaBitrateBps is > 0 and { } bitrate) rows.Add(("Media bitrate", FormatBitrate(bitrate)));
        if (BufferingCount > 0)
            rows.Add(("Buffering", string.Create(CultureInfo.InvariantCulture, $"{BufferingCount} times, {BufferingSeconds:F1} s")));
        return rows;
    }

    private List<(string Label, string Value)> PlaybackRows()
    {
        var rows = new List<(string Label, string Value)>();
        Add(rows, "Playback", Join(" · ", PlayMethod, Container));
        Add(rows, "Video", Join(" · ", VideoCodecLabel(VideoCodec), Resolution, FrameRateLabel(FrameRate)));
        Add(rows, "Dynamic range", HdrMode);
        Add(rows, "Audio", Join(" ", AudioCodecLabel(AudioCodec), ChannelLayoutLabel(AudioChannelCount)));
        Add(rows, "Subtitles", Subtitles);
        // The speed is a setting the viewer chose; it is a stat only while it changes what the other rows mean.
        if (PlaybackRate is { } rate && double.IsFinite(rate) && rate > 0 && Math.Abs(rate - 1) > 0.001)
            Add(rows, "Playback rate", RateLabel(rate));
        // The reason and the warnings share one label; only the first row carries it (Apple's Diagnostics).
        var notes = Notes.Select(Sentence).Where(note => !string.IsNullOrWhiteSpace(note)).ToList();
        for (var index = 0; index < notes.Count; index++) rows.Add((index == 0 ? "Diagnostics" : string.Empty, notes[index]!));
        return rows;
    }

    private List<(string Label, string Value)> StreamRows()
    {
        var rows = new List<(string Label, string Value)>();
        Add(rows, "Playback status", PhaseLabel(Phase));
        if (EstimatedBandwidthBps is >= 0 and { } bandwidth) Add(rows, "Network throughput", FormatBitrate(bandwidth));
        if (DroppedFrames is >= 0 and { } dropped) Add(rows, "Dropped frames", dropped.ToString("N0", CultureInfo.InvariantCulture));
        // Owner-approved (2026-11-17): Bale's smoothness reading, which the engine's drops feed but do not equal.
        if (Smoothness is { MissedFrames: not null } smoothness) Add(rows, "Missed frames", smoothness.MissedText);
        return rows;
    }

    private List<(string Label, string Value)> EngineRows()
    {
        var rows = new List<(string Label, string Value)>();
        Add(rows, "Route", BackendRoute);
        Add(rows, "Delivery", Delivery);
        Add(rows, "Backend", BackendDisplayName);
        Add(rows, "Video", VideoDecoderName);
        Add(rows, "Audio", AudioDecoderName);
        return rows;
    }

    private static void AddSection(List<PlaybackStatsSection> sections, string id, string title, List<(string Label, string Value)> rows)
    {
        if (rows.Count > 0) sections.Add(new(id, title, rows));
    }

    private static void Add(List<(string Label, string Value)> rows, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value.Trim()));
    }

    /// <summary>The non-empty parts joined with <paramref name="separator"/>, or null when none is left.</summary>
    private static string? Join(string separator, params string?[] parts)
    {
        var present = parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()).ToArray();
        return present.Length == 0 ? null : string.Join(separator, present);
    }

    /// <summary>The reason and notes are the server's words, lower case; Apple's Diagnostics starts them with a capital.</summary>
    private static string? Sentence(string? text) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>A picture size, "3840×2160": Apple's dimension label, with the multiplication sign.</summary>
    public static string Dimensions(int width, int height) => string.Create(CultureInfo.InvariantCulture, $"{width}×{height}");

    /// <summary>Apple's video codec names ("HEVC", "H.264", "AV1"...). A codec it does not name shows in capitals.</summary>
    public static string? VideoCodecLabel(string? codec)
    {
        var id = codec?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id)) return null;
        return id switch
        {
            "hevc" or "h265" or "hvc1" or "hev1" => "HEVC",
            "h264" or "avc" or "avc1" or "avc3" => "H.264",
            "av1" or "av01" => "AV1",
            "vp9" or "vp09" or "vp90" => "VP9",
            "mpeg2video" or "mpeg2" or "mpg2" => "MPEG-2",
            "mpeg4" or "mp4v" => "MPEG-4",
            "vc1" or "wvc1" => "VC-1",
            _ => id.ToUpperInvariant(),
        };
    }

    /// <summary>Apple's audio codec names (TrackLabels.audioCodec): "EAC3", "AAC", "TrueHD"... Others show in capitals.</summary>
    public static string? AudioCodecLabel(string? codec)
    {
        var id = codec?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(id)) return null;
        if (id.Contains("eac3", StringComparison.Ordinal) || id.Contains("e-ac-3", StringComparison.Ordinal)
            || id.Contains("ec-3", StringComparison.Ordinal)) return "EAC3";
        if (id.Contains("ac3", StringComparison.Ordinal) || id.Contains("ac-3", StringComparison.Ordinal)) return "AC3";
        if (id.Contains("aac", StringComparison.Ordinal)) return "AAC";
        if (id.Contains("mp3", StringComparison.Ordinal)) return "MP3";
        if (id.Contains("truehd", StringComparison.Ordinal)) return "TrueHD";
        if (id.Contains("dts", StringComparison.Ordinal)) return "DTS";
        if (id.Contains("flac", StringComparison.Ordinal)) return "FLAC";
        return id.ToUpperInvariant();
    }

    /// <summary>Apple's channel layouts: "Mono", "Stereo", "5.1", "7.1", otherwise a count ("3 channels").</summary>
    public static string? ChannelLayoutLabel(long? channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        6 => "5.1",
        8 => "7.1",
        > 0 and var count => FormattableString.Invariant($"{count} channels"),
        _ => null,
    };

    /// <summary>A frame rate as Apple shows it: at most three decimals, trailing zeros dropped ("23.976 fps", "24 fps").</summary>
    public static string? FrameRateLabel(double? fps) =>
        fps is { } rate && double.IsFinite(rate) && rate > 0 ? TrimmedNumber(rate) + " fps" : null;

    /// <summary>A playback rate as Apple shows it: "1×", "1.25×".</summary>
    public static string RateLabel(double rate) => TrimmedNumber(rate) + "×";

    public static string? PhaseLabel(PlaybackStatsPhase? phase) => phase switch
    {
        PlaybackStatsPhase.Loading => "Loading",
        PlaybackStatsPhase.Playing => "Playing",
        PlaybackStatsPhase.Paused => "Paused",
        PlaybackStatsPhase.Seeking => "Seeking",
        PlaybackStatsPhase.Rebuffering => "Rebuffering",
        PlaybackStatsPhase.Ended => "Ended",
        PlaybackStatsPhase.Error => "Error",
        _ => null,
    };

    /// <summary>Apple's delivery labels for the server's delivery ids; another id shows with its underscores as spaces.</summary>
    public static string? DeliveryText(string? delivery) => MetadataId(delivery)?.ToLowerInvariant() switch
    {
        null => null,
        "original_http" => "Original HTTP",
        "progressive" => "Progressive",
        "server_remux_progressive" => "Server Remux",
        "server_remux_hls" => "Server Remux HLS",
        "server_transcode_hls" => "Server Transcode HLS",
        var other => other.Replace('_', ' '),
    };

    /// <summary>Apple's play method: direct play, or the server's remux or transcode.</summary>
    public static string? PlayMethodText(string? delivery) => MetadataId(delivery)?.ToLowerInvariant() switch
    {
        "original_http" or "progressive" => "Direct play",
        "server_remux_progressive" or "server_remux_hls" => "Remuxed by the server",
        "server_transcode_hls" => "Transcoded by the server",
        _ => null,
    };

    /// <summary>Apple's container names ("MKV", "MPEG-TS", "MOV", "MP4"); another container shows as the server names it.</summary>
    public static string? ContainerText(string? container)
    {
        var value = MetadataId(container);
        if (value is null) return null;
        return value.ToLowerInvariant() switch
        {
            "matroska" or "mkv" => "MKV",
            "mpegts" or "mpeg-ts" or "ts" => "MPEG-TS",
            "quicktime" or "mov" => "MOV",
            "mp4" or "m4v" => value.ToUpperInvariant(),
            _ => value,
        };
    }

    private static string TrimmedNumber(double value) =>
        Math.Round(value, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>A runtime readout (codec name) on one line, at most <paramref name="maxLength"/> characters.</summary>
    public static string? CompactValue(string? text, int maxLength = 80)
    {
        if (text is null) return null;
        var line = string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= maxLength ? line : line[..(maxLength - 3)] + "...";
    }

    public static string FormatBitrate(long bps) => bps switch
    {
        // Java Formatter rounds halves up; .NET's F format alone uses ties-to-even.
        >= 1_000_000 => Math.Round(bps / 1_000_000.0, 1, MidpointRounding.AwayFromZero).ToString("F1", CultureInfo.InvariantCulture) + " Mbps",
        >= 1_000 => Math.Round(bps / 1_000.0, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture) + " Kbps",
        _ => bps.ToString(CultureInfo.InvariantCulture) + " bps",
    };

    /// <summary>
    /// Copy only diagnostic metadata, never the plan's stream URL, headers or credentials.
    /// <paramref name="versions"/> are the title's file versions, to name a version swap.
    /// </summary>
    public static PlaybackStats FromPlan(PlaybackPlan plan, bool? isHls = null, IReadOnlyList<FileVersion>? versions = null)
    {
        var delivery = MetadataId(plan.Delivery);
        var decision = delivery switch
        {
            "original_http" or "progressive" => "Direct play",
            "server_transcode_hls" => "Transcode",
            not null when delivery.Contains("remux", StringComparison.OrdinalIgnoreCase) => "Remux",
            _ => delivery,
        };
        var reason = DolbyVision7Policy.RequiresBaseLayer(plan)
            && string.Equals(plan.DecisionReason, PlaybackProtocol.ClientManagedDynamicRangeReason, StringComparison.OrdinalIgnoreCase)
            ? "Dolby Vision 7 → HDR10 base layer → SDR" : DescribeDecisionReason(plan.DecisionReason);
        var swap = DescribeVersionSwap(plan, versions);
        if (decision is not null && reason is not null) decision += $" ({reason})";
        if (swap is not null) decision = decision is null ? swap : $"{decision} · {swap}";
        // Diagnostics: the reason, the version swap, then the server's warnings (by their codes, never their free text).
        var notes = new List<string>();
        if (reason is not null) notes.Add(reason);
        if (swap is not null) notes.Add(swap);
        foreach (var warning in plan.DegradationWarnings ?? [])
            if (DescribeDecisionReason(warning?.Code) is { } text) notes.Add(text);
        var hls = isHls ?? (string.Equals(plan.Stream?.Protocol, "hls", StringComparison.OrdinalIgnoreCase)
            || string.Equals(delivery, "hls", StringComparison.OrdinalIgnoreCase)
            || delivery?.EndsWith("_hls", StringComparison.OrdinalIgnoreCase) == true);
        var dynamicRange = MetadataId(plan.EffectiveRecipe?.DynamicRange) ?? MetadataId(plan.Source?.DynamicRange);
        return new()
        {
            // PlayerViewModel.kt uses PlaybackRoute's engine/source names, not the server decision.
            // Windows MediaPlayer is Native Direct for progressive; FFmpegInteropX overrides this
            // with its actual mounted route rather than a prediction from the server's decision.
            BackendRoute = plan.Stream is null && delivery is null && isHls is null ? null : hls ? "HLS" : "Native Direct",
            PlayMethod = PlayMethodText(plan.Delivery),
            Container = ContainerText(plan.Source?.Container),
            Delivery = DeliveryText(plan.Delivery),
            Notes = notes,
            FrameRate = ResolveFrameRate(plan, hls, null),
            HdrMode = dynamicRange?.ToLowerInvariant() switch
            {
                null or "sdr" => "SDR",
                "hdr10" => "HDR10",
                "hdr10_plus" or "hdr10+" => "HDR10+",
                "hlg" => "HLG",
                "dolby_vision" or "dv" => "Dolby Vision",
                _ => dynamicRange,
            },
            Decision = decision,
            Source = DescribeSource(plan.Source),
            MediaBitrateBps = plan.EffectiveRecipe?.BitrateKbps is > 0 and <= long.MaxValue / 1000 and { } kbps ? kbps * 1000 : null,
        };
    }

    // silo-server preserves frame timing when transcoding (no -r/fps filter). Windows' HLS track
    // encoding properties can report a placeholder, so use the recipe/source instead.
    public static double? ResolveFrameRate(PlaybackPlan? plan, bool isHls, double? trackFrameRate) => isHls
        ? plan?.EffectiveRecipe?.FrameRate ?? plan?.Source?.FrameRate
        : trackFrameRate ?? plan?.Source?.FrameRate;

    private static string? DescribeSource(PlaybackSource? source)
    {
        if (source is null) return null;
        var parts = new List<string>();
        Add(source.Container);
        Add(source.VideoCodec);
        Add(source.VideoProfile);
        if (source.BitDepth is > 0 and { } depth) parts.Add(FormattableString.Invariant($"{depth}-bit"));
        if (source.Width is > 0 and { } width && source.Height is > 0 and { } height)
            parts.Add(FormattableString.Invariant($"{width}x{height}"));
        var audio = MetadataId(source.AudioCodec);
        if (source.AudioChannels is > 0 and { } channels)
            audio = (audio is null ? string.Empty : audio + " ") + FormattableString.Invariant($"{channels} ch");
        if (audio is not null) parts.Add(audio);
        return parts.Count == 0 ? null : string.Join(" · ", parts);

        void Add(string? value)
        {
            if (MetadataId(value) is { } id) parts.Add(id);
        }
    }

    /// <summary>
    /// Readable text for silo-server's plan decision_reason (internal/playback/plan_v3.go). The
    /// official clients never show it, so the wording is Bale's. Unknown codes still show, with
    /// underscores as spaces; the ordinary direct-play reason adds nothing and is dropped.
    /// </summary>
    public static string? DescribeDecisionReason(string? code) => MetadataId(code)?.ToLowerInvariant() switch
    {
        null or "validated_original_playback" => null,
        "client_managed_dynamic_range" => "player handles HDR",
        "client_dv8_base_layer" => "Dolby Vision 8 as its base layer",
        "client_dv7_to_dv81" => "Dolby Vision 7 converted to 8.1",
        "client_dv7_to_hdr10" => "Dolby Vision 7 → HDR10 base layer",
        "container_normalization" => "container not supported",
        "audio_adaptation" or "hls_audio_adaptation" => "audio converted",
        "hls_packaging_required" => "repackaged for streaming",
        "subtitle_burn_in_required" => "subtitles burned in",
        "evidence_insufficient_for_direct" => "video support unconfirmed",
        "quality_auto_source" => "video not supported",
        "quality_original" => "original quality",
        "quality_fixed_rung" => "quality setting",
        "quality_device_limit" => "above max resolution",
        "quality_bandwidth_limit" => "connection too slow",
        "quality_bandwidth_cap" => "bandwidth cap",
        { } other => other.Replace('_', ' '),
    };

    /// <summary>
    /// The server may play another file than the one asked for (silo-server's alternate-version
    /// fallback, "prefer lower version transcode": a version that plays as it is instead of a
    /// transcode of the requested one). Names both with the Detail readout's labels, e.g.
    /// "1080P version instead of 4K · DV"; null when the plan played the requested file or names none.
    /// </summary>
    public static string? DescribeVersionSwap(PlaybackPlan plan, IReadOnlyList<FileVersion>? versions)
    {
        var requested = plan.RequestedMediaFileId?.Trim();
        var effective = plan.EffectiveMediaFileId?.Trim();
        if (string.IsNullOrEmpty(requested) || string.IsNullOrEmpty(effective)
            || string.Equals(requested, effective, StringComparison.Ordinal)) return null;
        if (Label(effective) is not { } played || Label(requested) is not { } asked) return "other version played";
        // Two versions can share a readout label (same resolution and range, another codec or cut).
        return played == asked ? $"other {played} version played" : $"{played} version instead of {asked}";

        string? Label(string fileId)
        {
            if (versions?.FirstOrDefault(version => version.FileId == fileId) is not { } version) return null;
            var label = PlaybackFormatting.VersionCompactLabel(version);
            // "Auto" is the label's empty value. A version's resolution is server text, so hold
            // it to the same characters as a plan id, plus the label's own separator.
            return label != "Auto" && label.Length <= 40 && MetadataId(label.Replace(" · ", " ")) is not null ? label : null;
        }
    }

    // Plan fields are protocol ids/codecs, not free-form server diagnostics. Reject rather than
    // redact unexpected values so neither the screen, debug 'where' nor the log can disclose
    // request data.
    internal static string? MetadataId(string? value)
    {
        value = value?.Trim();
        return value is { Length: > 0 and <= 160 }
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '+' or ' ')
            ? value : null;
    }
}
