using Bale.Core.Detail;

namespace Bale.Xbox.Services;

internal static class XboxAudioCapabilities
{
    // android-shared/src/androidMain/kotlin/org/siloserver/silo/common/player/PlaybackCapabilityDetector.kt
    // supplies the Android device/output snapshot consumed by TrackSelectionPresets.kt. On Xbox One/Series,
    // use a conservative Media Foundation baseline: AAC, AC3/EAC3 decode and PCM (stereo/5.1/7.1 output).
    // Installed codec enumeration in Windows.Media.Core would not prove this playback/output route's
    // DTS/TrueHD decode or encoded passthrough support. Leave DTS, DTS-HD, TrueHD and all passthrough
    // unclaimed rather than add COM probing/reflection to the Native AOT path or guess from HDMI settings.
    // Decoder channel counts are not encoded sink limits: decoded audio can be output/downmixed as PCM.
    public static ClientAudioCapabilities Current { get; } = new(["aac", "ac3", "eac3", "pcm"]);
}
