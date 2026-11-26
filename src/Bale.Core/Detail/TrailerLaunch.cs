namespace Bale.Core.Detail;

/// <summary>Where a Trailers &amp; More card's video lives (TrailerRailEntry.kt: Local or Remote).</summary>
public abstract record TrailerSource;

/// <summary>A scanned extras file (ItemExtraInfo): the content id Silo plays it by and, when the server sends it, its file.</summary>
public sealed record LocalExtraSource(string ContentId, string? FileId) : TrailerSource;

/// <summary>A provider video (ItemVideoInfo): its site as the server spells it and the site's video key.</summary>
public sealed record RemoteVideoSource(string Site, string Key) : TrailerSource;

/// <summary>What selecting a Trailers &amp; More card does (<see cref="TrailerLaunch.For"/>).</summary>
public abstract record TrailerAction;

/// <summary>
/// TvItemDetailScreen.kt onSelectLocal: the extra plays in Bale's own player as its own playback (item type "extra"),
/// started by its content id and file through the Silo server, from its start (<see cref="TrailerLaunch.StartPosition"/>).
/// </summary>
public sealed record ExtraPlayback(string ContentId, string? FileId) : TrailerAction;

/// <summary>YoutubeTrailerLauncher.kt: the video's public watch page, for the app that opens such links.</summary>
public sealed record TrailerLink(Uri Link) : TrailerAction;

/// <summary>
/// The Trailers &amp; More rail's selection (TvDetailTrailersSection.kt onClick, TvItemDetailScreen.kt:1448-1466): a local
/// extra plays in the player, a remote trailer opens on YouTube. Only two things are ever started: a server-relative extra
/// (a catalog content id the Silo server resolves, never a link) and an https link on YouTube's own hosts.
/// </summary>
public static class TrailerLaunch
{
    /// <summary>shell_trailer_noApp: the toast YoutubeTrailerLauncher.kt shows when no app opens the trailer.</summary>
    public const string NoAppMessage = "No app is available to open this trailer";

    /// <summary>
    /// The resume override TvItemDetailScreen.kt plays an extra with: 0.0, an explicit start (isExplicitStartOver), so
    /// neither the extra's saved position nor the resume rewind applies.
    /// </summary>
    public const double StartPosition = 0.0;

    // YoutubeTrailerLauncher.kt's watch page, and the only hosts a trailer link may open on (https, default port).
    private const string WatchPage = "https://www.youtube.com/watch?v=";
    private static readonly string[] LinkHosts = ["youtube.com", "www.youtube.com", "youtu.be"];

    // contentid.go mints [a-z0-9-] ids, one URL path segment each; the cap only bounds what a card can carry.
    private const int MaxContentIdLength = 256;

    /// <summary>What selecting <paramref name="card"/> does; null when it can start nothing Bale allows.</summary>
    public static TrailerAction? For(TrailerCard card) => card.Source switch
    {
        LocalExtraSource extra when ServerContentId(extra.ContentId) is { } id => new ExtraPlayback(id, extra.FileId),
        // TrailerRail.kt: YouTube is the only remote site the clients can open.
        RemoteVideoSource video when string.Equals(video.Site, "youtube", StringComparison.OrdinalIgnoreCase)
            && WatchLink(video.Key) is { } link => new TrailerLink(link),
        _ => null,
    };

    /// <summary>
    /// YoutubeTrailerLauncher.kt's watch page for a video key, the key escaped as the query value so it can never change
    /// the host or path; null for a blank key.
    /// </summary>
    public static Uri? WatchLink(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var link = new Uri(WatchPage + Uri.EscapeDataString(key.Trim()));
        return IsAllowedLink(link) ? link : null;
    }

    /// <summary>Whether Bale may hand <paramref name="link"/> to another app: https on youtube.com or youtu.be only.</summary>
    public static bool IsAllowedLink(Uri? link) =>
        link is { IsAbsoluteUri: true } && link.Scheme == Uri.UriSchemeHttps && link.IsDefaultPort
        && string.IsNullOrEmpty(link.UserInfo) && LinkHosts.Contains(link.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The content id a local extra plays by, or null when it isn't one: a single path segment of RFC 3986 unreserved
    /// characters (plus ':') with a letter or digit, so it names no scheme, host, path or query. An extra is played
    /// through the Silo server's own catalog and playback routes, never from a link.
    /// </summary>
    public static string? ServerContentId(string? contentId)
    {
        var id = contentId?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MaxContentIdLength || !id.Any(char.IsAsciiLetterOrDigit)) return null;
        return id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or ':') ? id : null;
    }

    /// <summary>
    /// The extra's start once its own catalog detail has loaded (TvVideoPlaybackStarter.kt resolveTvPlaybackStartSelection):
    /// the extra's file when the detail still lists it, else the usual Auto choice. Null when the answer is no playable
    /// leaf (a series or season container), which Bale's player never takes.
    /// </summary>
    public static PlaybackSelection? Selection(ItemDetail detail, ExtraPlayback extra)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(extra);
        if (detail.PlaysEpisodes) return null;
        return extra.FileId is { } file && detail.Versions.Any(version => version.FileId == file)
            ? new PlaybackSelection(file) : PlaybackSelection.Auto;
    }
}
