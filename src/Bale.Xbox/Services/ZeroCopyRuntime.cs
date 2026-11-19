using System.Runtime.InteropServices;

namespace Bale.Xbox.Services;

/// <summary>
/// Dormant zero-copy setup, used only by debug dav1d-path zerocopy (wrapper patch 0015).
/// bale_gpu.dll repacks dav1d pictures on D3D12: register d3d12.dll before the upload device arrives.
/// Default dav1d output and grain use the CPU on D3D11 without this runtime (owner, 2026-11-19).
/// </summary>
internal static class ZeroCopyRuntime
{
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_zc_register_d3d12(nint d3d12Module);

    private static int _registered;

    /// <summary>
    /// Registers d3d12.dll (loaded here and never freed) once per process. False when the DLL or export is
    /// missing or registration fails; the debug route then keeps D3D11 CPU output.
    /// </summary>
    internal static bool Register()
    {
        var state = Volatile.Read(ref _registered);
        if (state != 0) return state > 0;
        var registered = false;
        try
        {
            if (!NativeLibrary.TryLoad("d3d12.dll", out var module)) Log.Info("Zero-copy: d3d12.dll missing; keeping dav1d D3D11 CPU output");
            else
            {
                var hr = bale_zc_register_d3d12(module);
                registered = hr >= 0;
                Log.Info(registered ? "Zero-copy: d3d12.dll registered"
                    : $"Zero-copy: register FAILS 0x{unchecked((uint)hr):X8}; keeping dav1d D3D11 CPU output");
            }
        }
        catch (Exception ex)
        {
            // Includes EntryPointNotFoundException for an older DLL; type and HRESULT only.
            Log.Info($"Zero-copy: register FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}); keeping dav1d D3D11 CPU output");
        }
        // Concurrent first calls may both register: the same module, so harmless.
        Volatile.Write(ref _registered, registered ? 1 : -1);
        return registered;
    }
}
