#if DEBUG_SERVER
using System.Buffers.Binary;
using System.Globalization;

namespace Bale.Xbox.Services;

/// <summary>Formats the Media Foundation colour attributes on a video media type for debug readouts.</summary>
internal static class DebugVideoColour
{
    // mfapi.h attribute GUIDs; values are MFVideoTransferMatrix / MFVideoTransferFunction /
    // MFVideoPrimaries / MFNominalRange (mfobjects.h). Unnamed values print as numbers only.
    private static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid TransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    private static readonly Guid VideoPrimaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    private static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    // Microsoft windows-rs windows 0.62.2, Windows/Win32/Media/MediaFoundation/mod.rs
    // (MF_MT_* constants generated from the Windows SDK). Luminances print raw UINT32 values.
    private static readonly Guid MaxLuminance = new("50253128-c110-4de4-98ae-46a324fae6da");
    private static readonly Guid MaxFrameAverageLuminance = new("58d4bf57-6f52-4733-a195-a9e29ecf9e27");
    private static readonly Guid MinMasteringLuminance = new("839a4460-4e7e-4b4f-ae79-cc08905c7b27");
    private static readonly Guid MaxMasteringLuminance = new("d6c6b997-272f-4ca1-8d00-8042111a0ff6");
    private static readonly Guid CustomVideoPrimaries = new("47537213-8cfb-4722-aa34-fbc9e24d77b8");

    private static readonly Dictionary<uint, string> MatrixNames = new()
    {
        [0] = "unknown", [1] = "BT709", [2] = "BT601", [3] = "SMPTE240M", [4] = "BT2020_10", [5] = "BT2020_12",
    };

    private static readonly Dictionary<uint, string> TransferNames = new()
    {
        [0] = "unknown", [1] = "linear", [4] = "gamma2.2", [5] = "709", [6] = "240M", [7] = "sRGB", [8] = "gamma2.8",
        [12] = "2020_const", [13] = "2020", [15] = "2084/PQ", [16] = "HLG",
    };

    private static readonly Dictionary<uint, string> PrimariesNames = new()
    {
        [0] = "unknown", [1] = "reserved", [2] = "BT709", [3] = "BT470M", [4] = "BT470BG", [5] = "SMPTE170M",
        [6] = "SMPTE240M", [7] = "EBU3213", [8] = "SMPTE_C", [9] = "BT2020", [10] = "XYZ", [11] = "DCI_P3",
    };

    private static readonly Dictionary<uint, string> RangeNames = new()
    {
        [0] = "unknown", [1] = "full", [2] = "limited", [3] = "48-208",
    };

    internal static string Describe(IDictionary<Guid, object> properties) =>
        $"matrix={Value(properties, YuvMatrix, MatrixNames)} transfer={Value(properties, TransferFunction, TransferNames)}"
        + $" primaries={Value(properties, VideoPrimaries, PrimariesNames)} range={Value(properties, NominalRange, RangeNames)}"
        + $" maxCLL={Value(properties, MaxLuminance)} maxFALL={Value(properties, MaxFrameAverageLuminance)}"
        + $" minMasteringLuminance={Value(properties, MinMasteringLuminance)} maxMasteringLuminance={Value(properties, MaxMasteringLuminance)}"
        + $" customPrimaries={CustomPrimaries(properties)}";

    private static string Value(IDictionary<Guid, object> properties, Guid key, Dictionary<uint, string>? names = null)
    {
        if (!properties.TryGetValue(key, out var boxed) || boxed is null) return "unset";
        uint value;
        try { value = boxed is uint u ? u : Convert.ToUInt32(boxed, CultureInfo.InvariantCulture); }
        catch { return "?"; }
        var number = value.ToString(CultureInfo.InvariantCulture);
        return names is not null && names.TryGetValue(value, out var name) ? $"{number}({name})" : number;
    }

    private static string CustomPrimaries(IDictionary<Guid, object> properties)
    {
        if (!properties.TryGetValue(CustomVideoPrimaries, out var boxed) || boxed is null) return "unset";
        if (boxed is not byte[] { Length: 32 } bytes) return "present";
        // MF_MT_CUSTOM_VIDEO_PRIMARIES is eight little-endian floats in Windows' blob layout.
        var values = new string[8];
        for (var i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4)).ToString("R", CultureInfo.InvariantCulture);
        return "[" + string.Join(",", values) + "]";
    }
}
#endif
