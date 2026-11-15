using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Native;

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

    /// <summary>
    /// The notice text, or null when the plan plays the picked version (or there was no explicit
    /// pick). Built only from fixed phrases and bounded resolution labels, never server text.
    /// Implemented in Rust (native/bale-core/src/version_notice.rs).
    /// </summary>
    public static string? Describe(PlaybackPlan plan, PlaybackSelection selection, IReadOnlyList<FileVersion> versions,
        IReadOnlyList<VideoDecodeCapability>? videoDecode = null)
    {
        var asked = selection.FileId is { } picked ? versions.FirstOrDefault(version => version.FileId == picked) : null;
        return BaleCoreNative.Call("version_notice.describe", writer =>
        {
            writer.Server("plan", plan);
            writer.Text("picked_file_id", selection.FileId);
            writer.Server("versions", versions);
            // Still C# (Detail/PlaybackFormatting) until the Detail slice.
            writer.Numbers("resolution_ranks", versions.Select(version => PlaybackFormatting.ResolutionRank(version.Resolution)));
            if (asked is not null && videoDecode is not null)
                writer.WriteBoolean("asked_decodes", PlaybackFormatting.Decodes(asked, videoDecode));
            else writer.WriteNull("asked_decodes");
        }, BaleCoreJson.Default.String);
    }

    private static string? EffectiveFileId(PlaybackPlan plan) => plan.EffectiveMediaFileId ?? plan.Source?.MediaFileId;
}
