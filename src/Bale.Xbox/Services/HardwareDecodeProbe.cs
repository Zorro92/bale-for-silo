using System.Runtime.InteropServices;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// Asks the console's D3D11 video device which decoder profiles it has and how large a picture
/// each accepts. Production decode claims follow this report (ConsoleEvidence.ForHardware), and the
/// debug <c>caps gpu</c> output prints the same walk. D3D11 only: the D3D12 video query removed the
/// device on a Series X in app mode (docs/player-plan.md).
/// </summary>
internal static class HardwareDecodeProbe
{
    internal sealed record FormatSizes(string Name, int Format, IReadOnlyList<(string Name, int Width, int Height)> Sizes);

    internal sealed record ProfileFormats(Guid Profile, IReadOnlyList<FormatSizes> Formats);

    /// <summary>What the walk got through. Error is the failing HRESULT; the earlier parts stay filled in.</summary>
    internal sealed record Report(int? FeatureLevel, uint? ProfileCount, IReadOnlyList<ProfileFormats> Profiles, int? Error);

    // DXGI_FORMAT_NV12 and DXGI_FORMAT_P010: 8-bit and 10-bit 4:2:0 decoder output.
    internal static readonly (string Name, int Format)[] OutputFormats =
    [
        ("NV12", HardwareDecodeLimits.Nv12),
        ("P010", HardwareDecodeLimits.P010),
    ];

    internal static readonly (string Name, uint Width, uint Height)[] Sizes =
    [
        ("1080p", 1920, 1080),
        ("4K", 3840, 2160),
        ("8K", 7680, 4320),
    ];

    // One probe per process, on a pool thread so launch does not wait for the device.
    private static readonly Lazy<Task<HardwareDecodeLimits?>> Probe = new(() => Task.Run(Detect));

    /// <summary>
    /// The cached limits, started on first use (App launch). Null means the probe failed or found
    /// nothing, and the claims fall back to the fixed 1080p baseline. The task never faults.
    /// </summary>
    internal static Task<HardwareDecodeLimits?> Limits => Probe.Value;

    private static HardwareDecodeLimits? Detect()
    {
        try
        {
            var report = Enumerate();
            if (report.Error is { } error)
            {
                // The HRESULT only, never the exception text.
                Log.Info($"decode probe: failed hr=0x{unchecked((uint)error):X8}; using the 1080p baseline claims");
                return null;
            }
            var limits = HardwareDecodeLimits.FromAcceptedSizes(report.Profiles.SelectMany(profile =>
                profile.Formats.SelectMany(format => format.Sizes.Select(size => (profile.Profile, format.Format, size.Width, size.Height)))));
            if (limits.IsEmpty)
            {
                Log.Info("decode probe: no decoder sizes; using the 1080p baseline claims");
                return null;
            }
            Log.Info($"decode probe: h264={Height(limits.H264)} hevc={Height(limits.HevcMain)}/{Height(limits.HevcMain10)}"
                + $" vp9={Height(limits.Vp9Profile0)}/{Height(limits.Vp9Profile2)}");
            return limits;
        }
        catch
        {
            Log.Info("decode probe: failed; using the 1080p baseline claims");
            return null;
        }

        static string Height(DecodeSize? size) => size is { } value ? $"{value.Height}p" : "none";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11VideoDecoderDesc
    {
        public Guid Guid;
        public uint SampleWidth;
        public uint SampleHeight;
        public int OutputFormat;
    }

    // Pointer outputs avoid by-ref marshalling, which DisableRuntimeMarshalling disallows.
    [DllImport("d3d11.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern unsafe int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software,
        uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        IntPtr* device, int* featureLevel, IntPtr* immediateContext);

    /// <summary>
    /// Walks every decoder profile, its NV12/P010 output support and which of 1080p, 4K and 8K has
    /// a decoder configuration. Creates its own hardware device and releases it before returning;
    /// never throws. <paramref name="trace"/> gets a fixed step name before each native stage
    /// (debug crash breadcrumbs).
    /// GUIDs, descriptor layout and ID3D11VideoDevice slots match the Windows SDK's d3d11.h
    /// (microsoft/win32metadata, generation/WinSDK/RecompiledIdlHeaders/um/d3d11.h).
    /// </summary>
    internal static unsafe Report Enumerate(Action<string>? trace = null)
    {
        var device = IntPtr.Zero;
        var context = IntPtr.Zero;
        var videoDevice = IntPtr.Zero;
        int? level = null;
        uint? profileCount = null;
        var profiles = new List<ProfileFormats>();
        int? error = null;
        try
        {
            trace?.Invoke("before device creation");
            // HARDWARE=1, VIDEO_SUPPORT=0x800, SDK_VERSION=7; never fall back to software.
            var featureLevel = 0;
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x800,
                IntPtr.Zero, 0, 7, &device, &featureLevel, &context));
            level = featureLevel;
            var videoDeviceId = new Guid("10ec4d5b-975a-4689-b9e4-d0aac30fe333");
            var deviceVtable = *(void***)device;
            var queryInterface = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)deviceVtable[0];
            Marshal.ThrowExceptionForHR(queryInterface(device, &videoDeviceId, &videoDevice));

            var videoVtable = *(void***)videoDevice;
            // ID3D11VideoDevice derives directly from IUnknown: profile queries occupy slots 11–14.
            var getProfileCount = (delegate* unmanaged[Stdcall]<IntPtr, uint>)videoVtable[11];
            var getProfile = (delegate* unmanaged[Stdcall]<IntPtr, uint, Guid*, int>)videoVtable[12];
            var checkFormat = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, int*, int>)videoVtable[13];
            var getConfigCount = (delegate* unmanaged[Stdcall]<IntPtr, D3D11VideoDecoderDesc*, uint*, int>)videoVtable[14];
            var count = getProfileCount(videoDevice);
            profileCount = count;
            trace?.Invoke($"before profile loop ({count} profiles)");
            for (uint index = 0; index < count; index++)
            {
                var profile = Guid.Empty;
                Marshal.ThrowExceptionForHR(getProfile(videoDevice, index, &profile));
                var formats = new List<FormatSizes>();
                foreach (var (formatName, format) in OutputFormats)
                {
                    var supported = 0;
                    Marshal.ThrowExceptionForHR(checkFormat(videoDevice, &profile, format, &supported));
                    if (supported == 0) continue;
                    var sizes = new List<(string Name, int Width, int Height)>();
                    foreach (var (sizeName, width, height) in Sizes)
                    {
                        var desc = new D3D11VideoDecoderDesc
                        {
                            Guid = profile,
                            SampleWidth = width,
                            SampleHeight = height,
                            OutputFormat = format,
                        };
                        uint configs = 0;
                        // A format can be supported while a particular size has no valid configuration.
                        if (getConfigCount(videoDevice, &desc, &configs) >= 0 && configs > 0)
                            sizes.Add((sizeName, (int)width, (int)height));
                    }
                    formats.Add(new(formatName, format, sizes));
                }
                profiles.Add(new(profile, formats));
            }
        }
        catch (Exception ex)
        {
            error = ex.HResult;
        }
        finally
        {
            Release(videoDevice, trace);
            Release(context, trace);
            Release(device, trace);
        }
        return new(level, profileCount, profiles, error);
    }

    private static unsafe void Release(IntPtr instance, Action<string>? trace)
    {
        if (instance == IntPtr.Zero) return;
        trace?.Invoke("before COM release");
        var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)instance)[2];
        release(instance);
    }
}
