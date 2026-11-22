#if DEBUG_SERVER
using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

internal sealed record DebugPlayUrlRequest(Uri Url, double? StartSeconds)
{
    internal const string Usage = "play-url requires url=<absolute http(s) file URL without user info or fragment>, optional start=<finite non-negative seconds>";
    internal const string FileId = "debug-file";

    internal static bool TryParse(IReadOnlyDictionary<string, string> query, out DebugPlayUrlRequest request)
    {
        request = null!;
        if (query.Keys.Any(key => key is not ("url" or "start"))
            || !query.TryGetValue("url", out var raw) || raw.Length is 0 or > 8192
            || raw.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || raw.Contains('\\')
            || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) return false;
        // Reuse /play's invariant source-seconds validation, including its TimeSpan ceiling.
        var time = new Dictionary<string, string> { ["id"] = FileId };
        if (query.TryGetValue("start", out var start)) time["start"] = start;
        if (!DebugNavigationRequest.TryParse(time, play: true, out var navigation)) return false;
        request = new(uri, navigation.StartSeconds);
        return true;
    }

    // Records otherwise include every property, including a possibly signed URL.
    public override string ToString() => "play-url [redacted-url]";

    internal PlaybackPlan Plan(DebugFileProbe? probe = null) => new()
    {
        Delivery = "original_http",
        EffectiveMediaFileId = FileId,
        DecisionReason = "direct_play",
        Stream = new() { Url = Url.AbsoluteUri, Protocol = "http", Container = probe?.Container, MimeType = probe?.MimeType },
        Source = new()
        {
            MediaFileId = FileId, Container = probe?.Container,
            VideoCodec = probe?.Video.Codec, Width = probe?.Video.Width, Height = probe?.Video.Height,
            BitDepth = probe?.Video.BitDepth, VideoProfile = probe?.Video.Profile,
            FrameRate = probe?.FrameRate, DynamicRange = probe?.DynamicRange, ColorRange = probe?.Video.ColorRange,
            AudioCodec = probe?.AudioCodec, DurationSeconds = probe?.DurationSeconds,
        },
        Timeline = new() { SourceStartSeconds = 0, PlayerStartSeconds = 0, TimelineOffsetSeconds = 0 },
    };

    internal ItemDetail Detail(DebugFileProbe? probe = null) => new(FileId, "movie", "Debug file", null, null, null,
        null, null, [], null, [], [], null, [], null, FileId, null, null, new(false, false, false), [])
    {
        Versions = probe is null ? [] : [new()
        {
            FileId = FileId, Container = probe.Container, CodecVideo = probe.Video.Codec, CodecAudio = probe.AudioCodec,
            VideoTracks = [probe.Video],
            Hdr = probe.DynamicRange is "hdr10" or "hlg",
        }],
    };
}

/// <summary>Only technical stream facts; no URL, file name, tags or media titles leave the probe.</summary>
internal sealed record DebugFileProbe(string Container, VideoTrack Video, string? AudioCodec, double? FrameRate, double? DurationSeconds)
{
    internal string DynamicRange => Video.ColorTransfer switch { "smpte2084" => "hdr10", "arib-std-b67" => "hlg", _ => "sdr" };
    internal string MimeType => Container switch
    {
        "mp4" => "video/mp4", "matroska" => "video/x-matroska", "webm" => "video/webm",
        "avi" => "video/x-msvideo", "mpegts" => "video/mp2t", _ => "application/octet-stream",
    };

    internal string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"probe codec={Video.Codec} size={Video.Width}x{Video.Height} fps={FrameRate:0.###} depth={Video.BitDepth} transfer={Video.ColorTransfer ?? "unknown"} container={Container}");
}
#endif
