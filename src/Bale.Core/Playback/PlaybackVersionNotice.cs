using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>A passive notice for an explicit version pick replaced by the server's effective file.</summary>
public sealed class PlaybackVersionNotice
{
    // TvPlayerViewModel.kt showPlayerMessage: a short informational message lasts five seconds.
    public const int HideMilliseconds = 5000;
    private PlaybackPlan? _observedPlan;
    private string? _lastFileId;
    public string? Text { get; private set; }
    public long? ShownAtMilliseconds { get; private set; }

    /// <summary>Observe only after the mount starts, so loading cannot consume the reading time.</summary>
    public void Observe(PlaybackPlan plan, PlaybackSelection selection, IReadOnlyList<FileVersion> versions,
        IReadOnlyList<VideoDecodeCapability> videoDecode, long nowMilliseconds)
    {
        if (ReferenceEquals(plan, _observedPlan)) return;
        _observedPlan = plan;
        var text = Describe(plan, selection, versions, videoDecode);
        if (text is null)
        {
            _lastFileId = null;
            Text = null;
            ShownAtMilliseconds = null;
            return;
        }
        var fileId = EffectiveFileId(plan);
        if (fileId == _lastFileId) return; // Seek reanchors and recovery of the same file stay quiet.
        _lastFileId = fileId;
        Text = text;
        ShownAtMilliseconds = nowMilliseconds;
    }

    public bool Visible(long nowMilliseconds) => ShownAtMilliseconds is { } shown
        && nowMilliseconds - shown >= 0 && nowMilliseconds - shown < HideMilliseconds;

    public static string? Describe(PlaybackPlan plan, PlaybackSelection selection, IReadOnlyList<FileVersion> versions,
        IReadOnlyList<VideoDecodeCapability>? videoDecode = null)
    {
        var picked = selection.FileId;
        var effective = EffectiveFileId(plan);
        // Auto resolves a file id for Start too; only the Detail selection proves an explicit pick.
        if (string.IsNullOrWhiteSpace(picked) || string.IsNullOrWhiteSpace(effective) || picked == effective) return null;
        var asked = versions.FirstOrDefault(version => version.FileId == picked);
        var played = versions.FirstOrDefault(version => version.FileId == effective);
        var requestedLabel = Resolution(asked);
        var original = string.Equals(plan.Delivery, "original_http", StringComparison.OrdinalIgnoreCase);
        var playedLabel = Resolution(played, original ? plan.Source?.Height : plan.EffectiveRecipe?.Height ?? plan.Source?.Height);
        var prefix = playedLabel is null ? "Playing another version"
            : playedLabel == requestedLabel ? $"Playing another {playedLabel} version" : $"Playing {playedLabel}";
        var selected = requestedLabel is null ? "the selected version" : $"the selected {requestedLabel} version";

        // These are the successful plan's quality constraints, not the discarded file's refusal.
        // Do not echo server messages: they are free-form and can contain private request data.
        var reason = plan.DecisionReason?.ToLowerInvariant();
        if (reason == "quality_bandwidth_cap" || plan.DegradationWarnings?.Any(warning => warning.Code == "bandwidth_cap_applied") == true)
            return prefix + " — limited by the playback bandwidth cap.";
        if (reason == "quality_bandwidth_limit") return prefix + " — adjusted for the connection speed.";
        if (reason == "quality_fixed_rung") return prefix + " — using the selected playback quality.";
        if (reason == "quality_device_limit") return prefix + " — adjusted for this console's video limits.";
        if (asked is not null && videoDecode is not null
            && SoftwareDecodePolicy.NormalizeCodec(asked.VideoTracks?.FirstOrDefault()?.Codec ?? asked.CodecVideo) is not null
            && !PlaybackFormatting.Decodes(asked, videoDecode))
            return prefix + $" — this console can't play {selected} directly.";

        // planAlternateVersionV3 returns the alternate's reason (often validated_original_playback),
        // not the original 4K-policy/codec/subtitle refusal. Stay neutral when that cause is absent.
        return prefix + (playedLabel == requestedLabel ? " instead of the one selected." : $" instead of {selected}.");
    }

    private static string? EffectiveFileId(PlaybackPlan plan) => plan.EffectiveMediaFileId ?? plan.Source?.MediaFileId;

    private static string? Resolution(FileVersion? version, long? outputHeight = null)
    {
        var height = outputHeight is > 0 ? outputHeight.Value
            : PlaybackFormatting.ResolutionRank(version?.Resolution) is > 0 and var rank ? rank
            : version?.VideoTracks?.FirstOrDefault()?.Height ?? 0;
        // Use only bounded numeric facts and fixed labels, never a file name or arbitrary label.
        return height switch
        {
            2160 => "4K",
            4320 => "8K",
            > 0 and <= 9999 => height.ToString(CultureInfo.InvariantCulture) + "p",
            _ => null,
        };
    }
}
