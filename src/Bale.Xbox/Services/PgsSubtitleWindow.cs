using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// A worker-decoded .sup window. Commit runs on the UI thread only after mount and request identity
/// checks. A canceled fetch/decode therefore cannot replace the track of a later seek or remount.
/// </summary>
internal sealed unsafe class PgsSubtitleWindow : SafeHandleZeroOrMinusOneIsInvalid
{
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_sub_pgs_decode(byte* data, nuint length,
        long endMs, uint videoWidth, uint videoHeight, nint* window);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_sub_pgs_bind(ulong token, nint window, long sourceMs);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern void bale_sub_pgs_destroy(nint window);

    private PgsSubtitleWindow(nint native) : base(true) => SetHandle(native);

    internal static PgsSubtitleWindow Decode(byte[] data, PgsSubtitleRange range, uint videoWidth, uint videoHeight)
    {
        nint native = 0;
        int hr;
        fixed (byte* bytes = data)
            hr = bale_sub_pgs_decode(bytes, (nuint)data.Length, SubtitleClockPush.Milliseconds(range.End), videoWidth, videoHeight, &native);
        if (hr < 0 || native == 0) throw new InvalidDataException("PGS subtitle decoding failed.");
        return new(native);
    }

    internal int Bind(ulong token, long sourceMs)
    {
        var held = false;
        try
        {
            // DisableRuntimeMarshalling requires a blittable pointer; keep the SafeHandle alive
            // explicitly across the call instead of asking the runtime to marshal it.
            DangerousAddRef(ref held);
            return bale_sub_pgs_bind(token, DangerousGetHandle(), sourceMs);
        }
        finally { if (held) DangerousRelease(); }
    }

    protected override bool ReleaseHandle()
    {
        bale_sub_pgs_destroy(handle);
        return true;
    }
}
