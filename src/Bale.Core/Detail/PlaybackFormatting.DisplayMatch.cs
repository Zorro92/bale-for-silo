using Bale.Core.Api.Models;
using Bale.Core.Native;
using Bale.Core.Playback;

namespace Bale.Core.Detail;

public static partial class PlaybackFormatting
{
    /// <summary>
    /// Whether any claim admits the version's first video track. Mirrors the bounds the server
    /// checks (silo-server internal/playback/capabilities_v3.go videoDecodeTakesSourceV3: codec,
    /// size, bit depth, frame rate, bitrate, profile); unknown facts don't reject. Implemented in
    /// Rust (native/bale-core/src/formatting.rs), which also picks Auto's display match.
    /// </summary>
    internal static bool Decodes(FileVersion version, IReadOnlyList<VideoDecodeCapability> claims) =>
        BaleCoreNative.Call("formatting.decodes", writer =>
        {
            writer.Server("version", version);
            writer.Records("claims", claims, BaleCoreJson.Default.VideoDecodeCapability);
        }, BaleCoreJson.Default.Boolean);
}
