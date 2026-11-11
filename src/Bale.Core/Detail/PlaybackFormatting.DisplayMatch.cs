using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Playback;

namespace Bale.Core.Detail;

public static partial class PlaybackFormatting
{
    /// <summary>
    /// Auto with display matching: the best version at or below the display that the decoder
    /// claims admit, else the best at or below the display (the server transcodes it), else the
    /// smallest version above the display. Ties keep the official resolution/bitrate/size order.
    /// </summary>
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
    private static int VersionHeight(FileVersion version) =>
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
