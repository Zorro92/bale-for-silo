using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>
/// An engine-independent, immutable readout for the debug overlay and the future HUD Stats tab.
/// Rows and number formats follow silo-android's TvPlayerHud.kt hudRows()/formatBitrate().
/// </summary>
public sealed record PlaybackStats
{
    public string? BackendDisplayName { get; init; }
    public string? BackendRoute { get; init; }
    public string? SubtitleRendering { get; init; }
    public string? HardContainers { get; init; }
    public string? VideoCodec { get; init; }
    public string? Resolution { get; init; }
    public double? FrameRate { get; init; }
    public string? HdrMode { get; init; }
    public string? VideoDecoderName { get; init; }
    public string? AudioCodec { get; init; }
    public long? AudioChannelCount { get; init; }
    public long? AudioSampleRate { get; init; }
    public string? AudioDecoderName { get; init; }
    public long? EstimatedBandwidthBps { get; init; }
    public long DroppedFrames { get; init; }
    public long AudioUnderruns { get; init; }
    public string? Delivery { get; init; }
    public string? Source { get; init; }
    public long? MediaBitrateBps { get; init; }
    public int BufferingCount { get; init; }
    public double BufferingSeconds { get; init; }

    public IReadOnlyList<(string Label, string Value)> HudRows()
    {
        var rows = new List<(string Label, string Value)>();
        Add("Backend", BackendDisplayName);
        Add("Route", BackendRoute);
        Add("Subtitles", SubtitleRendering);
        Add("Hard containers", HardContainers);
        Add("Video codec", VideoCodec);
        Add("Resolution", Resolution);
        if (FrameRate is { } fps && double.IsFinite(fps))
            Add("Frame rate", Math.Round(fps, 3, MidpointRounding.AwayFromZero).ToString("F3", CultureInfo.InvariantCulture) + " fps");
        Add("HDR mode", HdrMode);
        Add("Video decoder", VideoDecoderName);
        if (!string.IsNullOrWhiteSpace(AudioCodec))
        {
            var audio = new List<string> { AudioCodec };
            if (AudioChannelCount is > 0 and { } channels) audio.Add(FormattableString.Invariant($"{channels} ch"));
            if (AudioSampleRate is > 0 and { } sampleRate) audio.Add(FormattableString.Invariant($"{sampleRate} Hz"));
            Add("Audio codec", string.Join(" · ", audio));
        }
        Add("Audio decoder", AudioDecoderName);
        // Network throughput, never the plan's media bitrate (TvPlayerHud.kt).
        if (EstimatedBandwidthBps is >= 0 and { } bandwidth) Add("Estimated bandwidth", FormatBitrate(bandwidth));
        if (DroppedFrames > 0) Add("Dropped frames", DroppedFrames.ToString(CultureInfo.InvariantCulture));
        if (AudioUnderruns > 0) Add("Audio underruns", AudioUnderruns.ToString(CultureInfo.InvariantCulture));

        // Bale-only diagnostics stay after the official rows so a future HUD can reuse the snapshot.
        Add("Delivery", Delivery);
        Add("Source", Source);
        if (MediaBitrateBps is > 0 and { } bitrate) Add("Media bitrate", FormatBitrate(bitrate));
        if (BufferingCount > 0)
            Add("Buffering", string.Create(CultureInfo.InvariantCulture, $"{BufferingCount} times, {BufferingSeconds:F1} s"));
        return rows;

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add((label, value));
        }
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
        if (decision is not null && reason is not null) decision += $" ({reason})";
        if (DescribeVersionSwap(plan, versions) is { } swap) decision = decision is null ? swap : $"{decision} · {swap}";
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
            // VideoBackendCapabilities.kt describes the renderer, not the selection. Bale has none yet.
            SubtitleRendering = null,
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
            Delivery = decision,
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
