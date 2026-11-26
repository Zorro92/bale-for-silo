using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

/// <summary>The marker kinds of the server's contract (MarkerOccurrence.kind; silo-android MarkerTimeline.kt MarkerKind).</summary>
public enum MarkerKind { Intro, Credits, Recap, Preview }

/// <summary>One marker occurrence in source seconds, end-exclusive (silo-android model/catalog TimeRange).</summary>
public readonly record struct MarkerRange(double Start, double End)
{
    /// <summary>The spec's "inside": <c>start ≤ p &lt; end</c>.</summary>
    public bool Contains(double position) => position >= Start && position < End;
}

/// <summary>
/// Every marker occurrence of one file, in source-time order (silo-android shared/.../domain/player/MarkerTimeline.kt,
/// which mirrors the web player's <c>resolveMarkerRegions</c> / <c>markerOccurrenceAtTime</c>). The server's
/// <c>marker_segments</c> may list a kind more than once (two credits ranges around a mid-credits scene); its singular
/// <c>intro</c>/<c>credits</c>/<c>recap</c>/<c>preview</c> fields project only the first occurrence. Skips act on the
/// occurrence playing and never span the content between two of them.
/// </summary>
public sealed class MarkerTimeline
{
    // Per kind, in source-time order; read on every player tick, so split once here.
    private readonly MarkerRange[][] _byKind;

    private MarkerTimeline(IReadOnlyList<(MarkerKind Kind, MarkerRange Range)> regions) =>
        _byKind = Enum.GetValues<MarkerKind>().Select(kind => regions.Where(region => region.Kind == kind).Select(region => region.Range).ToArray())
            .ToArray();

    public static MarkerTimeline Empty { get; } = new([]);

    /// <summary>No valid occurrence of any kind.</summary>
    public bool IsEmpty => _byKind.All(ranges => ranges.Length == 0);

    /// <summary>
    /// Prefers <paramref name="segments"/>; the singular fields stand in when a server sends none. An empty list means no
    /// markers, even with stale singular fields. Invalid ranges (non-finite, negative start, end not after start) and
    /// unknown kinds are dropped; the rest are ordered by start, then end.
    /// </summary>
    public static MarkerTimeline Resolve(IEnumerable<(MarkerKind Kind, double Start, double End)>? segments,
        MarkerRange? intro = null, MarkerRange? credits = null, MarkerRange? recap = null, MarkerRange? preview = null)
    {
        var regions = segments?.Select(segment => (segment.Kind, new MarkerRange(segment.Start, segment.End))).ToList()
            ?? new List<(MarkerKind, MarkerRange)>();
        if (segments is null)
        {
            if (intro is { } i) regions.Add((MarkerKind.Intro, i));
            if (credits is { } c) regions.Add((MarkerKind.Credits, c));
            if (recap is { } r) regions.Add((MarkerKind.Recap, r));
            if (preview is { } p) regions.Add((MarkerKind.Preview, p));
        }
        return new(regions
            .Where(region => double.IsFinite(region.Item2.Start) && double.IsFinite(region.Item2.End)
                && region.Item2.Start >= 0 && region.Item2.End > region.Item2.Start)
            .OrderBy(region => region.Item2.Start).ThenBy(region => region.Item2.End)
            .ToList());
    }

    /// <summary>A catalog detail version's markers: the singular fields only (the catalog read carries no marker_segments).</summary>
    public static MarkerTimeline From(FileVersion? version) => version is null ? Empty
        : Resolve(null, Range(version.Intro), Range(version.Credits), Range(version.Recap), Range(version.Preview));

    /// <summary>
    /// The file's effective markers (getFileMarkers): every occurrence from <c>marker_segments</c>; the singular fields
    /// stand in only when a server leaves the list out.
    /// </summary>
    public static MarkerTimeline From(FileMarkers markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var segments = markers.MarkerSegments?
            .Where(segment => segment.Kind is not null && segment.StartSeconds is not null && segment.EndSeconds is not null)
            .Select(segment => (Kind(segment.Kind!.Value), segment.StartSeconds!.Value, segment.EndSeconds!.Value))
            .ToList();
        return Resolve(segments, Range(markers.Intro), Range(markers.Credits), Range(markers.Recap), Range(markers.Preview));
    }

    /// <summary>Valid occurrences of <paramref name="kind"/>, ordered by start then end.</summary>
    public IReadOnlyList<MarkerRange> Ranges(MarkerKind kind) => _byKind[(int)kind];

    /// <summary>How many occurrences of <paramref name="kind"/> the file has (logging).</summary>
    public int Count(MarkerKind kind) => _byKind[(int)kind].Length;

    /// <summary>The occurrence of <paramref name="kind"/> playing at <paramref name="position"/>; ranges are end-exclusive.</summary>
    public MarkerRange? Active(MarkerKind kind, double position)
    {
        if (!double.IsFinite(position)) return null;
        MarkerRange? active = null;
        foreach (var range in _byKind[(int)kind])
            if (range.Contains(position)) active = range;
        return active;
    }

    /// <summary>
    /// The occurrence a prompt for <paramref name="kind"/> is about: the one playing or, between occurrences, the one that
    /// last started, so an undo offered after a skip stays anchored to the range it skipped. Before any starts, the
    /// first, so scrubbing back past it keeps that undo too. The prompt still checks the playhead against the range
    /// before offering anything.
    /// </summary>
    public MarkerRange? OccurrenceAt(MarkerKind kind, double position)
    {
        if (!double.IsFinite(position)) return null;
        var occurrences = Ranges(kind);
        MarkerRange? latest = null;
        MarkerRange? active = null;
        foreach (var range in occurrences)
        {
            if (range.Start > position) break;
            if (latest is not { } last || range.End > last.End) latest = range;
            if (position < range.End) active = range;
        }
        // Equal-end occurrences keep one identity before and after a skip.
        if (active is { } playing) return occurrences.First(range => range.End == playing.End);
        return latest ?? (occurrences.Count > 0 ? occurrences[0] : null);
    }

    /// <summary>
    /// Where the closing credits begin: the start of the last credits range, extended back over credits ranges that run
    /// into it (1 s of rounding allowed), so a scene between two credits ranges still counts as part of the episode.
    /// </summary>
    public double? FinalCreditsStart()
    {
        var credits = Ranges(MarkerKind.Credits);
        if (credits.Count == 0) return null;
        var start = credits[^1].Start;
        for (var i = credits.Count - 1; i >= 0; i--)
            if (credits[i].End >= start - 1) start = Math.Min(start, credits[i].Start);
        return start;
    }

    private static MarkerRange? Range(Marker? marker) =>
        marker is { Start: { } start, End: { } end } ? new MarkerRange(start, end) : null;

    private static MarkerRange? Range(MarkerSegment? segment) =>
        segment is { StartSeconds: { } start, EndSeconds: { } end } ? new MarkerRange(start, end) : null;

    private static MarkerKind Kind(MarkerOccurrence_kind kind) => kind switch
    {
        MarkerOccurrence_kind.Intro => MarkerKind.Intro,
        MarkerOccurrence_kind.Credits => MarkerKind.Credits,
        MarkerOccurrence_kind.Recap => MarkerKind.Recap,
        _ => MarkerKind.Preview,
    };
}
