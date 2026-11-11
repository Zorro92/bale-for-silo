using System.Globalization;

namespace Bale.Core.Playback;

/// <summary>
/// silo-server subtitle_stream.go: windowed=1 opts PGS into positioned extraction with source PTS.
/// Keep a lead-in for an in-progress display set, and refresh ahead of the tail. The window's full
/// history stays seekable in Rust; crossing either edge fetches again, including while paused.
/// </summary>
public sealed record PgsSubtitleRange(double Start, double End)
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public const double LeadSeconds = 30, DurationSeconds = 180, RefreshSeconds = 45;

    public static PgsSubtitleRange At(double sourceSeconds)
    {
        var start = Math.Max(0, double.IsFinite(sourceSeconds) ? sourceSeconds - LeadSeconds : 0);
        return new(start, start + DurationSeconds);
    }

    public bool Contains(double sourceSeconds) => sourceSeconds >= Start && sourceSeconds < End;
    public bool NeedsRefresh(double sourceSeconds, double? duration) => !Contains(sourceSeconds)
        || (sourceSeconds >= End - RefreshSeconds && !(duration is > 0 && End >= duration));

    public Uri RequestUri(Uri source)
    {
        // The artifact may already carry window parameters; retain its authenticated track identity,
        // replacing only the window. The resolver validates these keys before accepting the artifact.
        var query = source.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(pair => pair.Split('=')[0] is not ("windowed" or "position" or "duration"));
        var builder = new UriBuilder(source)
        {
            Query = string.Join("&", query.Append("windowed=1")
                .Append("position=" + Start.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("duration=" + (End - Start).ToString("0.###", CultureInfo.InvariantCulture))),
        };
        return builder.Uri;
    }
}
