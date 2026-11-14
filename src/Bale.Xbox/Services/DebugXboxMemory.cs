#if DEBUG_SERVER
using System.Globalization;
using System.Runtime.InteropServices;

namespace Bale.Xbox.Services;

/// <summary>
/// Xbox-only split of the app's memory budget, from Microsoft's "4K video playback for UWP apps on
/// Xbox" (hevc-xbox): Windows.Xbox.ApplicationModel.ApplicationResources' IApplicationResourcesStatics2
/// GetExtendedMemoryInfo. appMemory is the general heap; extendedMemory is the graphics partition on
/// Xbox Series X|S. Debug readout only; production claims and limits do not use it.
/// </summary>
internal static class DebugXboxMemory
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct ExtendedMemoryInfo
    {
        public ulong AppMemoryUsage;
        public ulong AppMemoryLimit;
        public ulong ExtendedMemoryUsage;
        public ulong ExtendedMemoryLimit;
    }

    private const string ClassName = "Windows.Xbox.ApplicationModel.ApplicationResources";
    private static readonly Guid StaticsIid = new("dd36a017-b640-45f7-a023-1615cf098923");

    [DllImport("combase.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern unsafe int WindowsCreateString(char* source, uint length, IntPtr* hstring);

    [DllImport("combase.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern unsafe int RoGetActivationFactory(IntPtr activatableClassId, Guid* iid, IntPtr* factory);

    /// <summary>The four values, or the failing HRESULT (no exception text).</summary>
    internal static unsafe (ExtendedMemoryInfo? Info, int? Error) Read()
    {
        var name = IntPtr.Zero;
        var statics = IntPtr.Zero;
        try
        {
            fixed (char* chars = ClassName)
            {
                var hr = WindowsCreateString(chars, (uint)ClassName.Length, &name);
                if (hr < 0) return (null, hr);
            }
            var iid = StaticsIid;
            var activation = RoGetActivationFactory(name, &iid, &statics);
            if (activation < 0) return (null, activation);
            // IUnknown (0–2), IInspectable (3–5), then GetExtendedMemoryInfo.
            var vtable = *(void***)statics;
            var getInfo = (delegate* unmanaged[Stdcall]<IntPtr, ExtendedMemoryInfo*, int>)vtable[6];
            var info = default(ExtendedMemoryInfo);
            var result = getInfo(statics, &info);
            return result < 0 ? (null, result) : (info, null);
        }
        catch (Exception ex)
        {
            return (null, ex.HResult);
        }
        finally
        {
            if (statics != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)statics)[2])(statics);
            if (name != IntPtr.Zero) WindowsDeleteString(name);
        }
    }

    /// <summary>One line: general heap and graphics partition usage/limit in MiB.</summary>
    internal static string Describe()
    {
        var (info, error) = Read();
        if (info is not { } value)
            return "unavailable (0x" + unchecked((uint)(error ?? 0)).ToString("X8", CultureInfo.InvariantCulture) + ")";
        static string MiB(ulong bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);
        return $"general {MiB(value.AppMemoryUsage)} of {MiB(value.AppMemoryLimit)} MiB · graphics "
            + $"{MiB(value.ExtendedMemoryUsage)} of {MiB(value.ExtendedMemoryLimit)} MiB · total "
            + $"{MiB(value.AppMemoryUsage + value.ExtendedMemoryUsage)} of {MiB(value.AppMemoryLimit + value.ExtendedMemoryLimit)} MiB";
    }
}
#endif
