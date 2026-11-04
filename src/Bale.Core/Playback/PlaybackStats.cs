using System.Globalization;
using Bale.Core.Api.Models;

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

    /// <summary>Copy only diagnostic metadata, never the plan's stream URL, headers or credentials.</summary>
    public static PlaybackStats FromPlan(PlaybackPlan plan)
    {
        var delivery = MetadataId(plan.Delivery);
        var route = delivery switch
        {
            "original_http" => "Direct play",
            "server_transcode_hls" => "Transcode",
            not null when delivery.Contains("remux", StringComparison.OrdinalIgnoreCase) => "Remux",
            _ => delivery,
        };
        if (route is not null && MetadataId(plan.DecisionReason) is { } reason) route += $" ({reason})";
        var dynamicRange = MetadataId(plan.EffectiveRecipe?.DynamicRange) ?? MetadataId(plan.Source?.DynamicRange);
        return new()
        {
            BackendRoute = route,
            SubtitleRendering = MetadataId(plan.Subtitle?.Mode),
            HdrMode = dynamicRange?.ToLowerInvariant() switch
            {
                null or "sdr" => "SDR",
                "hdr10" => "HDR10",
                "hdr10_plus" or "hdr10+" => "HDR10+",
                "hlg" => "HLG",
                "dolby_vision" or "dv" => "Dolby Vision",
                _ => dynamicRange,
            },
            Source = DescribeSource(plan.Source),
            MediaBitrateBps = plan.EffectiveRecipe?.BitrateKbps is > 0 and <= long.MaxValue / 1000 and { } kbps ? kbps * 1000 : null,
        };
    }

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

    // Plan fields are protocol ids/codecs, not free-form server diagnostics. Reject rather than
    // redact unexpected values so neither the screen nor debug 'where' can disclose request data.
    private static string? MetadataId(string? value)
    {
        value = value?.Trim();
        return value is { Length: > 0 and <= 160 }
            && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or '+' or ' ')
            ? value : null;
    }
}
