using System.Globalization;
using System.Text.RegularExpressions;
using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Detail;

/// <summary>The detail hero's read-only playback labels (TvPlaybackFormatting.kt).</summary>
public static partial class PlaybackFormatting
{
    /// <summary>
    /// The file a detail page shows and Play starts (DetailVersionSelector.kt): an explicit Version pick,
    /// else Auto inside the current edition (the Edition pick, else the last-played edition, else the
    /// preferred cut by name whose Auto file compares best). History picks only the edition, except a later part of an item in
    /// progress. Plain Auto applies <see cref="DisplayVersionMatch.Current"/> (Bale's display
    /// resolution matching) inside the edition.
    /// </summary>
    public static FileVersion? SelectDisplayVersion(ItemDetail detail, PlaybackSelection selection, string? preferredQuality = null) =>
        SelectDisplayVersion(detail, selection.FileId, selection.EditionId, preferredQuality);

    public static FileVersion? SelectDisplayVersion(ItemDetail detail, string? selectedFileId, string? editionId,
        string? preferredQuality = null) =>
        SelectVersion(detail.Versions, DisplayVersionMatch.Current, selectedFileId, detail.LastFileId, preferredQuality,
            detail.PlaybackVariants, editionId, detail.LastEditionKey, detail.PlaybackPositionSeconds > 0);

    /// <summary>Versions without a detail: editions come from the files' edition keys alone.</summary>
    public static FileVersion? SelectDisplayVersion(IReadOnlyList<FileVersion> versions,
        string? selectedFileId = null, string? lastFileId = null, string? preferredQuality = null) =>
        SelectVersion(versions, DisplayVersionMatch.Current, selectedFileId, lastFileId, preferredQuality);

    /// <summary>
    /// <see cref="SelectDisplayVersion(ItemDetail, PlaybackSelection, string?)"/> with an explicit match;
    /// null is the official Auto. Implemented in Rust (native/bale-core/src/formatting.rs).
    /// </summary>
    public static FileVersion? SelectVersion(IReadOnlyList<FileVersion> versions, DisplayVersionMatch? match,
        string? selectedFileId = null, string? lastFileId = null, string? preferredQuality = null,
        IReadOnlyList<PlaybackVariant>? variants = null, string? editionId = null, string? lastEditionKey = null,
        bool inProgress = false)
    {
        if (versions.Count == 0) return null;
        var index = BaleCoreNative.Call("formatting.select_version", writer =>
        {
            writer.Server("versions", versions);
            if (match is null) writer.WriteNull("match");
            else writer.Record("match", match, BaleCoreJson.Default.DisplayVersionMatch);
            writer.Text("selected_file_id", selectedFileId);
            writer.Text("last_file_id", lastFileId);
            writer.Text("preferred_quality", preferredQuality);
            writer.Server("variants", variants ?? []);
            writer.Text("edition_id", editionId);
            writer.Text("last_edition_key", lastEditionKey);
            writer.WriteBoolean("in_progress", inProgress);
        }, BaleCoreJson.Default.NullableInt32);
        return index is { } chosen ? versions[chosen] : null;
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

    /// <summary>
    /// A Dolby Vision codec (dvhe/dvh1/dva1/dvav) or any track with DV metadata, a DV profile or a
    /// DV range. Implemented in Rust (native/bale-core/src/formatting.rs).
    /// </summary>
    public static bool IsDolbyVision(FileVersion version) =>
        BaleCoreNative.Call("formatting.is_dolby_vision", writer => writer.Server("version", version), BaleCoreJson.Default.Boolean);

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

    /// <summary>
    /// A version label's height: the first 3–4 digit number (optionally p/i), else named tiers (8K,
    /// 4K/UHD, QHD, FHD, HD, SD), else 0. Implemented in Rust (native/bale-core/src/formatting.rs).
    /// </summary>
    internal static int ResolutionRank(string? value) =>
        BaleCoreNative.Call("formatting.resolution_rank", value, BaleCoreJson.Default.String, BaleCoreJson.Default.Int32);

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

}
