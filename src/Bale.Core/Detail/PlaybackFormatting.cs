using System.Globalization;
using System.Text.RegularExpressions;
using Bale.Core.Api.Models;

namespace Bale.Core.Detail;

/// <summary>The detail hero's read-only playback labels (TvPlaybackFormatting.kt).</summary>
public static partial class PlaybackFormatting
{
    /// <summary>
    /// TvDetailVersionSelection.kt / PlaybackVersionSelector.kt: explicit file, last played,
    /// then the best resolution at or below the quality cap; bitrate and size break ties.
    /// File IDs remain strings, as supplied by Bale's generated API contract. Auto applies
    /// <see cref="DisplayVersionMatch.Current"/> (Bale's display resolution matching).
    /// </summary>
    public static FileVersion? SelectDisplayVersion(IReadOnlyList<FileVersion> versions,
        string? selectedFileId = null, string? lastFileId = null, string? preferredQuality = null) =>
        SelectVersion(versions, DisplayVersionMatch.Current, selectedFileId, lastFileId, preferredQuality);

    /// <summary><see cref="SelectDisplayVersion"/> with an explicit match; null is the official Auto.</summary>
    public static FileVersion? SelectVersion(IReadOnlyList<FileVersion> versions, DisplayVersionMatch? match,
        string? selectedFileId = null, string? lastFileId = null, string? preferredQuality = null)
    {
        if (selectedFileId is not null && versions.FirstOrDefault(version => version.FileId == selectedFileId) is { } selected)
        {
            return selected;
        }

        if (lastFileId is not null && versions.FirstOrDefault(version => version.FileId == lastFileId) is { } lastPlayed)
        {
            return lastPlayed;
        }

        var ranked = versions.OrderByDescending(version => ResolutionRank(version.Resolution))
            .ThenByDescending(version => version.Bitrate ?? 0)
            .ThenByDescending(version => version.FileSize ?? 0).ToList();
        var quality = NonEmpty(preferredQuality)?.ToLowerInvariant();
        // Only plain Auto matches the display; an "original"-style choice asks for the best.
        if (quality is null or "auto" && match is not null) return MatchDisplay(ranked, match);
        return quality is null or "auto" or "original" or "source" or "max"
            ? ranked.FirstOrDefault()
            : ranked.FirstOrDefault(version => ResolutionRank(version.Resolution) <= ResolutionRank(quality))
                ?? ranked.FirstOrDefault();
    }

    /// <summary>Only resolution and dynamic range appear in the quiet readout, never codecs.</summary>
    public static string VersionCompactLabel(FileVersion? version)
    {
        if (version is null)
        {
            return "Auto";
        }

        var resolution = NonEmpty(version.Resolution);
        // The pinned API has dimensions instead of VideoTrack.resolution (TvPlaybackFormatting.kt).
        resolution ??= version.VideoTracks?.FirstOrDefault(track => track.Height is > 0) is { Height: { } height }
            ? height.ToString(CultureInfo.InvariantCulture) + "p" : null;
        var tokens = new[]
        {
            DisplayResolution(resolution),
            IsDolbyVision(version) ? "DV" : IsHdr(version) ? "HDR" : null,
        }.OfType<string>();
        var label = string.Join(" · ", tokens);
        return label.Length == 0 ? "Auto" : label;
    }

    public static bool IsDolbyVision(FileVersion version) =>
        IsDolbyVisionCodec(version.CodecVideo) || (version.VideoTracks ?? []).Any(track =>
            NonEmpty(track.DolbyVision) is not null || track.DvProfile is not null
                || HdrFormatIndicatesDolbyVision(track.VideoRangeType)
                || HdrFormatIndicatesDolbyVision(track.VideoRange)
                || IsDolbyVisionCodec(track.Codec));

    private static bool IsHdr(FileVersion version) =>
        version.Hdr == true || (version.VideoTracks ?? []).Any(track =>
            track.Hdr10Plus == true || IsHdrRange(track.VideoRangeType) || IsHdrRange(track.VideoRange));

    // The pinned VideoTrack has video_range[_type], not Android's hdr / hdr_format fields.
    private static bool IsHdrRange(string? value) => NonEmpty(value)?.ToLowerInvariant() is { } range
        && (range.Contains("hdr") || range.Contains("hlg") || HdrFormatIndicatesDolbyVision(range));

    private static bool HdrFormatIndicatesDolbyVision(string? value)
    {
        var lowered = value?.Trim().ToLowerInvariant();
        return lowered is not null && (lowered.Contains("dolby") || lowered.Contains("dovi")
            || Regex.Split(lowered, "[^a-z0-9]+", RegexOptions.CultureInvariant).Contains("dv"));
    }

    private static bool IsDolbyVisionCodec(string? value)
    {
        var token = string.Concat((value ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();
        return token.Contains("dolbyvision") || token.StartsWith("dvhe", StringComparison.Ordinal)
            || token.StartsWith("dvh1", StringComparison.Ordinal) || token.StartsWith("dva1", StringComparison.Ordinal)
            || token.StartsWith("dvav", StringComparison.Ordinal);
    }

    internal static int ResolutionRank(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        var numeric = ResolutionHeight().Match(normalized);
        if (numeric.Success && int.TryParse(numeric.Groups[1].Value, CultureInfo.InvariantCulture, out var height))
        {
            return height;
        }

        return normalized switch
        {
            var label when label.Contains("4320") || label.Contains("8k") => 4320,
            var label when label.Contains("2160") || label.Contains("4k") || label.Contains("uhd") => 2160,
            var label when label.Contains("1440") || label.Contains("qhd") => 1440,
            var label when label.Contains("1080") || label.Contains("fhd") => 1080,
            var label when label.Contains("720") || label.Contains("hd") => 720,
            var label when label.Contains("576") => 576,
            var label when label.Contains("480") || label.Contains("sd") => 480,
            _ => 0,
        };
    }

    private static string? DisplayResolution(string? value)
    {
        var token = NonEmpty(value);
        var lowered = token?.ToLowerInvariant();
        return lowered switch
        {
            null => null,
            var label when label.Contains("4320") || label.Contains("8k") => "8K",
            var label when label.Contains("2160") || label == "4k" || label.Contains("uhd") => "4K",
            var label when label.Contains("1080") => "1080P",
            var label when label.Contains("720") => "720P",
            _ => token!.ToUpperInvariant(),
        };
    }

    private static string? NonEmpty(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    [GeneratedRegex(@"\b(\d{3,4})(?:p|i)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionHeight();
}
