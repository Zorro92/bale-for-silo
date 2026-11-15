using System.Globalization;
using System.Text.RegularExpressions;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;

// The C# originals, before the Rust port (docs/rust-port.md), for differential tests only.
namespace Bale.Core.Tests.Reference;

internal static partial class PlaybackFormatting
{
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

    public static bool IsDolbyVision(FileVersion version) =>
        IsDolbyVisionCodec(version.CodecVideo) || (version.VideoTracks ?? []).Any(track =>
            NonEmpty(track.DolbyVision) is not null || track.DvProfile is not null
                || HdrFormatIndicatesDolbyVision(track.VideoRangeType)
                || HdrFormatIndicatesDolbyVision(track.VideoRange)
                || IsDolbyVisionCodec(track.Codec));

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

    private static string? NonEmpty(string? value) => value?.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    [GeneratedRegex(@"\b(\d{3,4})(?:p|i)?\b", RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionHeight();

    internal static FileVersion? MatchDisplay(IReadOnlyList<FileVersion> ranked, DisplayVersionMatch match)
    {
        bool FitsDisplay(FileVersion version) => VersionHeight(version) <= match.DisplayMaxHeight;
        return ranked.FirstOrDefault(version => FitsDisplay(version) && Decodes(version, match.VideoDecode))
            ?? ranked.FirstOrDefault(FitsDisplay)
            ?? ranked.OrderBy(VersionHeight)
                .ThenByDescending(version => Decodes(version, match.VideoDecode))
                .FirstOrDefault();
    }

    /// <summary>The label height (what the readout shows), else the first video track's height.</summary>
    internal static int VersionHeight(FileVersion version) =>
        ResolutionRank(version.Resolution) is > 0 and var rank ? rank
            : version.VideoTracks?.FirstOrDefault(track => track.Height is > 0) is { Height: { } height }
                ? (int)Math.Min(height, int.MaxValue) : 0;

    /// <summary>
    /// Whether any claim admits the version's first video track. Mirrors the bounds the server
    /// checks (silo-server internal/playback/capabilities_v3.go videoDecodeTakesSourceV3: codec,
    /// size, bit depth, frame rate, bitrate, profile); unknown facts don't reject.
    /// </summary>
    internal static bool Decodes(FileVersion version, IReadOnlyList<VideoDecodeCapability> claims)
    {
        var track = version.VideoTracks?.FirstOrDefault();
        // SourceDescriptorFromFileV3: the track's codec before the file's, and the video track's
        // own bitrate before the file's, which includes audio.
        var codec = SoftwareDecodePolicy.NormalizeCodec(NonEmpty(track?.Codec) ?? version.CodecVideo);
        if (codec is null) return false;
        var bitrate = track?.Bitrate is > 0 ? track.Bitrate : version.Bitrate is > 0 ? version.Bitrate : null;
        // normalizeBitrateKbpsV3: a value this large is bits per second, not kbps.
        if (bitrate > 10_000_000) bitrate /= 1000;
        var width = track?.Width is > 0 ? track.Width : null;
        var height = track?.Height is > 0 ? track.Height : VersionHeight(version) is > 0 and var rank ? rank : null;
        var fps = FrameRate(track?.FrameRate);
        var profile = NonEmpty(track?.Profile)?.ToLowerInvariant();
        return claims.Any(claim => claim.Codec == codec
            && (width is null || claim.MaxWidth is null || width <= claim.MaxWidth)
            && (height is null || claim.MaxHeight is null || height <= claim.MaxHeight)
            && (track?.BitDepth is not { } depth || claim.BitDepths.Count == 0 || claim.BitDepths.Contains((int)depth))
            && (fps is null || claim.MaxFrameRate is null || fps <= claim.MaxFrameRate + 0.01)
            && (bitrate is null || claim.MaxBitrateKbps is null || bitrate <= claim.MaxBitrateKbps)
            // The server fails an empty or unknown profile against a profile list.
            && (claim.Profiles.Count == 0 || (profile is not null && claim.Profiles.Contains(profile))));
    }

    /// <summary>"23.976", "24000/1001" or "29.97 fps"; null when unreadable.</summary>
    private static double? FrameRate(string? value)
    {
        var text = NonEmpty(value)?.Split(' ')[0];
        if (text is null) return null;
        var parts = text.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator > 0)
        {
            return numerator / denominator;
        }

        return parts.Length == 1 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate)
            && rate > 0 ? rate : null;
    }
}
