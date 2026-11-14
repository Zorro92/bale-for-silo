using System.Globalization;
using Bale.Core.Api.Models;
using Bale.Core.Detail;

namespace Bale.Core.Playback;

/// <summary>The original-file executor's bounded HDR-to-SDR promise, never a sink-HDR claim.</summary>
public static class ConsoleToneMapPolicy
{
    public static bool HasHdrOutput(HdrCapabilities hdr) =>
        hdr.Hdr10 || hdr.Hdr10Plus || hdr.Hlg || hdr.DolbyVisionProfiles.Count > 0;

    // bale.29 adds HLG eligibility; earlier stages only promised PQ. Keep the whole production
    // claim behind that revision and a runtime ABI/export check in the Xbox app.
    public static bool WrapperSupported(string version)
    {
        const string prefix = "2.1.0-bale.";
        return version.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(version.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            && revision >= 29;
    }

    public static bool CanManage(FileVersion version, DeviceCapabilities device)
    {
        if (!device.OriginalHttpSupported || !device.OriginalDynamicRangeMappingSupported
            || device.DisplayHdrEvidence != "exact" || HasHdrOutput(device.Hdr)
            || version.VideoTracks is not { Count: > 0 } tracks) return false;
        var track = tracks[0];
        // DV7 explicitly selects the first video in demux order, including dual-track BL+EL.
        // Other range-management routes still require an unambiguous single video stream.
        if (track.DvProfile == 7 ? !device.OriginalDolbyVision7BaseLayerSupported : tracks.Count != 1) return false;
        var codec = SoftwareDecodePolicy.NormalizeCodec(string.IsNullOrWhiteSpace(track.Codec) ? version.CodecVideo : track.Codec);
        if (codec is not ("hevc" or "vp9") || track.BitDepth != 10
            || !device.VideoCodecs.Contains(codec)
            || !PlaybackFormatting.Decodes(version, device.VideoDecode.Where(decode => decode.Hardware).ToArray())) return false;
        // d3d11va supplies 4:2:0 P010. Do not promise the stage for known 4:2:2/4:4:4 input.
        if (!string.IsNullOrWhiteSpace(track.PixelFormat)
            && track.PixelFormat.Trim().ToLowerInvariant() is not ("yuv420p10le" or "p010le")) return false;
        var transfer = track.ColorTransfer?.Trim().ToLowerInvariant();
        var pq = transfer is "smpte2084" or "16";
        var hlg = transfer is "arib-std-b67" or "18";
        if (!pq && !hlg) return false;
        // Mirror bale_gpu's frame eligibility: PQ supports BT.2020 NCL or BT.709; HLG needs
        // BT.2020 NCL. Unknown/mixed primaries and matrix cannot establish a safe conversion.
        var primaries = track.ColorPrimaries?.Trim().ToLowerInvariant();
        var matrix = track.ColorSpace?.Trim().ToLowerInvariant();
        var bt2020 = (primaries is "bt2020" or "9") && (matrix is "bt2020nc" or "9");
        var bt709 = (primaries is "bt709" or "1") && (matrix is "bt709" or "1");
        if (!bt2020 && !(pq && bt709)) return false;
        if (!PlaybackFormatting.IsDolbyVision(version)) return true;
        if (codec != "hevc" || track.DvBlPresent != true || track.DvConfigPresent != true) return false;
        // Profile 7 defines an HDR10 base layer; neither MEL nor FEL enhancement is presented.
        // Profile 8 needs its explicit compatibility id: 8.1 = HDR10, 8.4 = HLG, 8.2 = SDR.
        return track.DvProfile switch
        {
            7 => pq && bt2020 && (track.DvBlCompatId is null or 0 or 1),
            8 => track.DvElPresent != true && track.DvBlCompatIdPresent == true && (pq && track.DvBlCompatId == 1 || hlg && track.DvBlCompatId == 4),
            _ => false,
        };
    }

    /// <summary>Protect even unexpected HDR originals; never let the native container source present them on SDR.</summary>
    public static bool RequiresToneMap(PlaybackPlan plan, bool sdrOutput)
    {
        if (!sdrOutput || !string.Equals(plan.Delivery, "original_http", StringComparison.OrdinalIgnoreCase)) return false;
        // The existing DV 8.2 fallback explicitly promises an SDR base layer, not HDR pixels.
        if (string.Equals(plan.DecisionReason, PlaybackProtocol.ClientDolbyVision8BaseLayerReason, StringComparison.OrdinalIgnoreCase)
            && plan.Source?.DolbyVisionProfile == 8 && plan.Source.DvBlCompatId == 2
            && string.Equals(plan.EffectiveRecipe?.DynamicRange, "sdr", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(plan.DecisionReason, PlaybackProtocol.ClientManagedDynamicRangeReason, StringComparison.OrdinalIgnoreCase)
            || plan.Source?.DynamicRange?.Trim().ToLowerInvariant() is not (null or "" or "sdr")
            || plan.Source?.DolbyVisionProfile is > 0 || plan.Source?.Hdr10Plus == true
            || plan.Claims?.Video is { } video && (video.Hdr10 == true || video.Hdr10Plus == true || video.Hlg == true || video.DolbyVision == true);
    }
}
