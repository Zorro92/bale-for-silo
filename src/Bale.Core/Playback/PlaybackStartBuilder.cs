using Bale.Core.Connection;
using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>
/// Detail-to-start handoff from androidTvApp/src/androidMain/kotlin/org/siloserver/silo/tv/ui/screens/player/TvVideoPlaybackStarter.kt.
/// Pass the playable episode's detail, not its series/season container.
/// </summary>
public static class PlaybackStartBuilder
{
    public static PlaybackStartRequestV3 Build(ItemDetail detail, PlaybackSelection selection, string profileId,
        string attemptId, double? startPosition, DeviceCapabilities device, ClientIdentity identity)
    {
        if (selection.FileId is { } picked && !detail.Versions.Any(version => version.FileId == picked))
            throw new ArgumentException("The selected version is no longer available.", nameof(selection));
        var version = PlaybackFormatting.SelectDisplayVersion(detail.Versions, selection.FileId, detail.LastFileId)
            ?? throw new InvalidOperationException("This item has no playable version.");
        return Build(version.FileId!, profileId, attemptId, startPosition, selection, device, identity);
    }

    /// <summary>fileId is the resolved Auto version; an explicit Detail version pick takes precedence.</summary>
    public static PlaybackStartRequestV3 Build(string fileId, string profileId, string attemptId, double? startPosition,
        PlaybackSelection selection, DeviceCapabilities device, ClientIdentity identity)
    {
        fileId = selection.FileId ?? fileId;
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptId);
        if (startPosition is { } position && (!double.IsFinite(position) || position < 0))
            throw new ArgumentOutOfRangeException(nameof(startPosition));
        if (selection.AudioIndex is < 0) throw new ArgumentOutOfRangeException(nameof(selection));
        if (selection.SubtitleIndex is < -1) throw new ArgumentOutOfRangeException(nameof(selection));
        if (device.MaxChannels < 1) throw new ArgumentOutOfRangeException(nameof(device));
        if (device.VideoDecode.Any(decode => !decode.Hardware && !SoftwareDecodePolicy.VideoCodecs.Contains(decode.Codec)))
            throw new ArgumentException("Software video decoding is restricted to the owner's allowlist.", nameof(device));

        var passthrough = new AudioPassthroughCapabilities(device.AudioPassthroughCodecs, device.MaxChannels, device.PassthroughEntries);
        var caps = new ClientCodecCapabilities(device.VideoEvidence, device.AudioEvidence, device.VideoCodecs,
            device.HardwareVideoCodecs, device.VideoDecode, device.AudioDecodeCodecs, device.Containers,
            device.Hdr.Hdr10 || device.Hdr.Hdr10Plus || device.Hdr.Hlg || device.Hdr.DolbyVisionProfiles.Count > 0,
            device.Hdr, passthrough);
        var output = new PlaybackOutputContext(device.Hdr, passthrough)
        {
            CurrentSink = device.AudioPassthroughCodecs.Count > 0 ? "passthrough_sink" : "local_output",
            Display = new(device.DisplayHdrEvidence, device.Hdr),
        };
        var original = new DeliveryCapability
        {
            Enabled = device.OriginalHttpSupported,
            SupportedOnDevice = device.OriginalHttpSupported,
            Containers = device.Containers,
            VideoCodecs = device.VideoCodecs,
            AudioDecodeCodecs = device.AudioDecodeCodecs,
            AudioPassthroughCodecs = device.AudioPassthroughCodecs,
            MaxChannels = device.MaxChannels,
            HdrDetails = device.Hdr,
            Subtitles = device.Subtitles,
            // No Android track-switch, Dolby Vision transform, or auth-refresh promise for Windows yet.
        };
        // PlaybackCapabilityDetector.kt disables progressive until its seekable transport is proven.
        var progressive = original with
        {
            Enabled = device.ProgressiveSupported,
            SupportedOnDevice = device.ProgressiveSupported,
            FailureReason = device.ProgressiveSupported ? null : "disabled_pending_seekable_transport",
            // In-container subtitles are an original_http claim only (the server reads it there).
            Subtitles = device.Subtitles with { NativeEmbedded = [] },
        };
        var hls = original with
        {
            Enabled = device.HlsSupported,
            SupportedOnDevice = device.HlsSupported,
            Containers = ["m3u8", "hls"],
            // AdaptiveMediaSource cannot use the bundled FFmpeg fallbacks.
            VideoCodecs = original.VideoCodecs.Where(codec => !SoftwareDecodePolicy.VideoCodecs.Contains(codec)).ToArray(),
            AudioDecodeCodecs = original.AudioDecodeCodecs.Where(codec => !SoftwareDecodePolicy.AudioCodecs.Contains(codec)).ToArray(),
            Subtitles = device.Subtitles with { NativeEmbedded = [] },
        };
        var context = new ClientPlaybackContext("tv", identity.ClientVersion ?? "unknown",
            new PlaybackDeviceContext(identity.Platform), output,
            new Dictionary<string, DeliveryCapability>
            {
                ["original_http"] = original,
                ["progressive"] = progressive,
                ["hls"] = hls,
            })
        {
            AppBuild = identity.ClientBuild,
            AppChannel = identity.ClientChannel,
        };

        // TvPlaybackSelectorRow.kt: Auto = null, subtitle Off = -1, explicit choices keep their ordinals.
        // android-shared/src/androidMain/kotlin/org/siloserver/silo/common/player/PlaybackSessionManager.kt
        // filters Off from the V3 body: the server rejects negative indexes and an absent pair means Off.
        var subtitle = selection.SubtitleIndex is >= 0 ? selection.SubtitleIndex : null;
        return new(fileId, profileId, attemptId, PlaybackProtocol.SoloClientFeatures(context, caps.VideoDecode), caps, context)
        {
            StartPosition = startPosition,
            AudioTrackIndex = selection.AudioIndex,
            AudioTrackId = selection.AudioIndex is { } audio ? $"file:{fileId}:audio:{audio}" : null,
            SubtitleTrackIndex = subtitle,
            SubtitleTrackId = subtitle is { } index ? $"file:{fileId}:subtitle:{index}" : null,
        };
    }
}
