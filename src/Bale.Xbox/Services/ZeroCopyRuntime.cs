using System.Runtime.InteropServices;

namespace Bale.Xbox.Services;

/// <summary>
/// Production zero-copy setup (wrapper patch 0015): bale_gpu.dll repacks dav1d's pictures on D3D12, so
/// d3d12.dll must be registered with it before the wrapper's upload device arrives.
/// </summary>
internal static class ZeroCopyRuntime
{
    [DllImport("bale_gpu.dll", ExactSpelling = true)] private static extern int bale_zc_register_d3d12(nint d3d12Module);

    private static int _registered;

    /// <summary>
    /// Registers d3d12.dll (loaded here and never freed) once per process. False when the DLL or export is
    /// missing or registration fails; the wrapper option then stays off.
    /// </summary>
    internal static bool Register()
    {
        var state = Volatile.Read(ref _registered);
        if (state != 0) return state > 0;
        var registered = false;
        try
        {
            if (!NativeLibrary.TryLoad("d3d12.dll", out var module)) Log.Info("Zero-copy: d3d12.dll missing; zero-copy stays off");
            else
            {
                var hr = bale_zc_register_d3d12(module);
                registered = hr >= 0;
                Log.Info(registered ? "Zero-copy: d3d12.dll registered"
                    : $"Zero-copy: register FAILS 0x{unchecked((uint)hr):X8}; zero-copy stays off");
            }
        }
        catch (Exception ex)
        {
            // Includes EntryPointNotFoundException for an older DLL; type and HRESULT only.
            Log.Info($"Zero-copy: register FAILS ({ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}); zero-copy stays off");
        }
        // Concurrent first calls may both register: the same module, so harmless.
        Volatile.Write(ref _registered, registered ? 1 : -1);
        return registered;
    }
}
