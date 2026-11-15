using Bale.Core.Api.Models;
using Bale.Core.Playback;

// The C# originals, before the Rust port (docs/rust-port.md), for differential tests only.
namespace Bale.Core.Tests.Reference;

internal static class PlaybackSubtitleResolver
{
    public static PlaybackEmbeddedSubtitle? ResolveEmbedded(PlaybackPlan? plan) =>
        plan?.Subtitle is { Mode: "render", Embedded.StreamIndex: >= 0 and <= PlaybackEmbeddedSubtitle.MaxStreamIndex and var index }
            ? new((int)index) : null;

    internal static uint? Format(string? format, string? mimeType) =>
        Named(format) ?? Named(mimeType?.Split(';')[0]);

    private static uint? Named(string? name) => name?.Trim().TrimStart('.').ToLowerInvariant() switch
    {
        "ass" or "ssa" or "text/x-ssa" or "text/x-ass" => PlaybackSubtitleSource.Ass,
        "srt" or "subrip" or "application/x-subrip" => PlaybackSubtitleSource.Srt,
        "vtt" or "webvtt" or "text/vtt" => PlaybackSubtitleSource.WebVtt,
        "sup" or "pgs" or "hdmv_pgs_subtitle" or "application/pgs" or "application/x-pgs" => PlaybackSubtitleSource.Pgs,
        _ => null,
    };
}
