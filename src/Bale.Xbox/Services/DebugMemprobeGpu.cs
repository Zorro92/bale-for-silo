#if DEBUG_SERVER
using System.Runtime.InteropServices;

namespace Bale.Xbox.Services;

// No delegate marshalling or callbacks: the same dedicated worker drives every native step.
internal sealed partial class DebugMemprobeGpu : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Segment
    {
        public ulong Budget, Usage, Available, Reservation;
        public int Hr;
        public uint Reserved;
        public override readonly string ToString() => Hr < 0 ? $"hr={DebugMemprobeReport.Hr(Hr)}"
            : $"budget_mib={DebugMemprobeReport.MiB(Budget)},usage_mib={DebugMemprobeReport.MiB(Usage)},available_mib={DebugMemprobeReport.MiB(Available)},reservation_mib={DebugMemprobeReport.MiB(Reservation)}";
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Sample
    {
        public ulong Bytes;
        public double CopyMs;
        public Segment Local, NonLocal;
        public int RemovedHr;
        public uint Stage;
        public override readonly string ToString() => $"gpu_mib={DebugMemprobeReport.MiB(Bytes)} copy_ms={DebugMemprobeReport.Milliseconds(CopyMs)} LOCAL[{Local}] NON_LOCAL[{NonLocal}] removed={DebugMemprobeReport.Hr(RemovedHr)}";
    }

    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern uint bale_memprobe_abi_version();
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern unsafe int bale_memprobe_create(uint api, nint module, nint* handle);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern unsafe int bale_memprobe_step(nint handle, ulong bytes, Sample* sample);
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern void bale_memprobe_destroy(nint handle);
    private nint _handle, _module;

    public unsafe DebugMemprobeGpu(bool d3d12)
    {
        try
        {
            if (bale_memprobe_abi_version() != 1) throw new InvalidOperationException("memprobe ABI");
            if (d3d12) _module = NativeLibrary.Load("d3d12.dll");
            nint handle = 0;
            var hr = bale_memprobe_create(d3d12 ? 12u : 11u, _module, &handle);
            _handle = handle;
            Marshal.ThrowExceptionForHR(hr);
        }
        catch { Dispose(); throw; }
    }

    public unsafe int Step(ulong bytes, out Sample sample)
    {
        Sample snapshot = default;
        var hr = bale_memprobe_step(_handle, bytes, &snapshot);
        sample = snapshot;
        return hr;
    }

    public void Dispose()
    {
        try
        {
            if (_handle != 0) { bale_memprobe_destroy(_handle); _handle = 0; }
        }
        finally
        {
            if (_module != 0) { NativeLibrary.Free(_module); _module = 0; }
        }
    }
}
#endif
