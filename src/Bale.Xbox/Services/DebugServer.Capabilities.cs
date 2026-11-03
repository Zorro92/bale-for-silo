#if DEBUG_SERVER
using System.Globalization;
using System.Text;
using Windows.Devices.Enumeration;
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.Graphics.Display.Core;
using Windows.Media.Audio;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Render;

namespace Bale.Xbox.Services;

internal sealed partial class DebugServer
{
    // only=video|audio|display|device|graph runs one section; each logs a breadcrumb first because
    // a native failure in a media API ends the app without reaching any catch.
    private static async Task<string> DescribeCapabilitiesAsync(string? only)
    {
        bool Run(string section)
        {
            if (!string.IsNullOrEmpty(only) && !string.Equals(only, section, StringComparison.OrdinalIgnoreCase)) return false;
            Log.Info($"Debug caps: {section}");
            return true;
        }
        var builder = new StringBuilder("Decoders (installed; not a guarantee of playback/profile support or passthrough):\n");
        if (Run("video")) await AppendDecodersAsync(builder, CodecKind.Video, ["H264", "HEVC", "VP8", "VP9", "AV1", "MPEG2", "VC1"]);
        if (Run("audio")) await AppendDecodersAsync(builder, CodecKind.Audio, ["AAC", "AC3", "EAC3", "DTS", "DTS-HD", "TrueHD/MLP", "FLAC", "ALAC", "Opus", "MP3", "PCM"]);
        // These view-scoped display APIs must run on the UI thread, unlike codec/device enumeration.
        if (Run("display")) builder.Append("Display:\n").Append(await OnUiAsync(DescribeDisplayCapabilities));
        builder.Append("Audio:\n");
        await AppendAudioCapabilitiesAsync(builder, Run("device"), Run("graph"));
        Log.Info("Debug caps: done");
        return builder.ToString();
    }

    private static DebugCodecNames KnownCodecNames()
    {
        return new DebugCodecNames(
        [
            (CodecSubtypes.VideoFormatH264, "H264"),
            (CodecSubtypes.VideoFormatH264ES, "H264"),
            (CodecSubtypes.VideoFormatH265, "HEVC"),
            (CodecSubtypes.VideoFormatHevc, "HEVC"),
            (CodecSubtypes.VideoFormatHevcES, "HEVC"),
            (CodecSubtypes.VideoFormatVP80, "VP8"),
            (CodecSubtypes.VideoFormatVP90, "VP9"),
            // The UWP projection lacks an AV1 property; a runtime check cannot guard a missing
            // compile-time member. Use the Media Foundation AV01 subtype GUID directly.
            ("31305641-0000-0010-8000-00aa00389b71", "AV1"),
            (CodecSubtypes.VideoFormatMpeg2, "MPEG2"),
            (CodecSubtypes.VideoFormatWvc1, "VC1"),
            (CodecSubtypes.VideoFormatWmv3, "VC1"),
            (CodecSubtypes.AudioFormatAac, "AAC"),
            (CodecSubtypes.AudioFormatAdts, "AAC"),
            (CodecSubtypes.AudioFormatDolbyAC3, "AC3"),
            (CodecSubtypes.AudioFormatDolbyDDPlus, "EAC3"),
            (CodecSubtypes.AudioFormatDts, "DTS"),
            (CodecSubtypes.AudioFormatFlac, "FLAC"),
            (CodecSubtypes.AudioFormatAlac, "ALAC"),
            (CodecSubtypes.AudioFormatOpus, "Opus"),
            (CodecSubtypes.AudioFormatMP3, "MP3"),
            (CodecSubtypes.AudioFormatPcm, "PCM"),
            (CodecSubtypes.AudioFormatFloat, "PCM"),
            // Raw stream aliases in the Windows SDK's wmcodecdsp.h / mfapi.h. Deliberately do
            // not map IEC61937/S/PDIF output subtypes to decoder support for these codecs.
            ("e06d8033-db46-11cf-b4d1-00805f6cbbea", "DTS"),
            ("00002001-0000-0010-8000-00aa00389b71", "DTS"),
            ("a2e58eb7-0fa9-48bb-a40c-fa0e156d0645", "DTS-HD"),
            ("a61ac364-ad0e-4744-89ff-213ce0df8804", "DTS-HD"),
            ("eb27cec4-163e-4ca3-8b74-8e25f91b517e", "TrueHD/MLP"),
            // MLP FourCC, also used by LAVFilters common/includes/moreuuids.h (no SDK property).
            ("20504c4d-0000-0010-8000-00aa00389b71", "TrueHD/MLP"),
        ]);
    }

    private static async Task AppendDecodersAsync(StringBuilder builder, CodecKind kind, string[] codecNames)
    {
        try
        {
            var codecs = await new CodecQuery().FindAllAsync(kind, CodecCategory.Decoder, string.Empty).AsTask();
            var names = KnownCodecNames();
            var subtypes = new List<string>();
            builder.Append($"  {kind}: {codecs.Count} decoders\n");
            foreach (var codec in codecs)
            {
                var supported = codec.Subtypes.ToList();
                subtypes.AddRange(supported);
                builder.Append($"    {EscapeText(DebugScreenText.Compact(codec.DisplayName, 240))} IsTrusted={codec.IsTrusted}\n")
                    .Append("      Subtypes: ").Append(supported.Count == 0 ? "none" : string.Join(", ", supported.Select(names.Describe))).Append('\n');
            }
            builder.Append(names.Summary(subtypes, codecNames));
        }
        catch (Exception ex)
        {
            builder.Append($"  {kind}: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
    }

    private static string DescribeDisplayCapabilities()
    {
        var builder = new StringBuilder();
        try
        {
            var hdmi = HdmiDisplayInformation.GetForCurrentView();
            if (hdmi is null) builder.Append("  HDMI: unavailable for this view\n");
            else
            {
                var current = hdmi.GetCurrentDisplayMode();
                builder.Append("  HDMI current: ").Append(current is null ? "none" : DescribeDisplayMode(current)).Append('\n');
                var modes = hdmi.GetSupportedDisplayModes().Select(DescribeDisplayMode).Distinct(StringComparer.Ordinal).ToList();
                builder.Append($"  HDMI supported: {modes.Count} distinct modes\n");
                foreach (var mode in modes) builder.Append("    ").Append(mode).Append('\n');
            }
        }
        catch (Exception ex)
        {
            builder.Append($"  HDMI: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
        try
        {
            if (!ApiInformation.IsMethodPresent("Windows.Graphics.Display.DisplayInformation", "GetAdvancedColorInfo"))
                builder.Append("  Advanced color: unavailable\n");
            else
            {
                var color = DisplayInformation.GetForCurrentView().GetAdvancedColorInfo();
                if (color is null) builder.Append("  Advanced color: unavailable for this view\n");
                else builder.Append(string.Create(CultureInfo.InvariantCulture,$"  Advanced color: current={color.CurrentAdvancedColorKind}"
                    + $" HDR={color.IsAdvancedColorKindAvailable(AdvancedColorKind.HighDynamicRange)}"
                    + $" WideColorGamut={color.IsAdvancedColorKindAvailable(AdvancedColorKind.WideColorGamut)}"
                    + $" SdrWhiteLevelInNits={color.SdrWhiteLevelInNits:0.###}"
                    + $" MinLuminanceInNits={color.MinLuminanceInNits:0.###} MaxLuminanceInNits={color.MaxLuminanceInNits:0.###}"
                    + $" MaxAverageFullFrameLuminanceInNits={color.MaxAverageFullFrameLuminanceInNits:0.###}\n"));
            }
        }
        catch (Exception ex)
        {
            builder.Append($"  Advanced color: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
        return builder.ToString();
    }

    private static string DescribeDisplayMode(HdmiDisplayMode mode)
    {
        var dolbyVision = ApiInformation.IsPropertyPresent("Windows.Graphics.Display.Core.HdmiDisplayMode", "IsDolbyVisionLowLatencySupported")
            ? mode.IsDolbyVisionLowLatencySupported.ToString() : "unavailable";
        return string.Create(CultureInfo.InvariantCulture,$"{mode.ResolutionWidthInRawPixels}x{mode.ResolutionHeightInRawPixels} @{mode.RefreshRate:0.###}Hz"
            + $" BitsPerPixel={mode.BitsPerPixel} ColorSpace={mode.ColorSpace} PixelEncoding={mode.PixelEncoding}"
            + $" IsSmpte2084Supported={mode.IsSmpte2084Supported} Is2086MetadataSupported={mode.Is2086MetadataSupported}"
            + $" IsDolbyVisionLowLatencySupported={dolbyVision}");
    }

    private static async Task AppendAudioCapabilitiesAsync(StringBuilder builder, bool runDevice, bool runGraph)
    {
        if (runDevice) try
        {
            var id = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
            if (string.IsNullOrEmpty(id)) builder.Append("  Default render: none\n");
            else
            {
                var device = await DeviceInformation.CreateFromIdAsync(id).AsTask();
                builder.Append($"  Default render: {EscapeText(DebugScreenText.Compact(device.Name, 240))}\n");
            }
        }
        catch (Exception ex)
        {
            builder.Append($"  Default render: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
        if (runGraph) try
        {
            // Read the default graph format without starting playback or changing audio routing.
            var created = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Media)).AsTask();
            using var graph = created.Graph;
            if (created.Status != AudioGraphCreationStatus.Success || graph is null)
                builder.Append($"  Graph mix: unavailable ({created.Status})\n");
            else
            {
                var format = graph.EncodingProperties;
                builder.Append($"  Graph mix (default settings): channels={format.ChannelCount} sampleRate={format.SampleRate}Hz bitsPerSample={format.BitsPerSample} subtype={format.Subtype}\n");
            }
        }
        catch (Exception ex)
        {
            builder.Append($"  Graph mix: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
        try
        {
            var baseline = XboxAudioCapabilities.Current;
            builder.Append("  XboxAudioCapabilities baseline: decode=").Append(string.Join(", ", baseline.DecodeCodecs))
                .Append("; passthrough=").Append(baseline.Passthrough is { } passthrough ? string.Join(", ", passthrough.Codecs) : "unclaimed").Append('\n');
        }
        catch (Exception ex)
        {
            builder.Append($"  XboxAudioCapabilities baseline: error: {DebugScreenText.Compact(ex.Message, 500)}\n");
        }
        builder.Append("  Decoder enumeration and graph mix do not prove encoded passthrough or output-channel limits.\n");
    }
}
#endif
