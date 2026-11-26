using System.Runtime.InteropServices;
using Bale.Core.Playback;

namespace Bale.Xbox.Services;

/// <summary>
/// rav1g.dll, the GPU AV1 decoder (owner, 2026-11-26: the default AV1 decoder in every build). Every build packages the
/// pinned release (scripts/rav1g.json; an empty pin packages none), and the FFmpegInteropX fork loads it from the package
/// root with LoadPackagedLibrary. Once per process, on the thread pool, Bale checks it as the fork's loader will
/// (BaleRav1g.h BaleRav1gExports: it loads, has every export the fork resolves and the pinned interface version), short of
/// creating rav1g's D3D12 device, then frees it: the fork loads it again for its first stream. Only a usable library gets
/// the rav1g route and its 2160p60 AV1 claim; otherwise AV1 stays on dav1d at 1080p60, as before rav1g.
/// </summary>
internal static class Rav1gLibrary
{
    private static readonly Lazy<bool> PresentValue = new(() =>
    {
        try { return File.Exists(Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "rav1g.dll")); }
        catch { return false; }
    });

    private static readonly Lazy<Task<Rav1gLibraryStatus>> StatusValue = new(() => Task.Run(Check));

    /// <summary>The package carries rav1g.dll.</summary>
    internal static bool Present => PresentValue.Value;

    /// <summary>The check, started on first use (the launch's version match) on the thread pool; it never faults.</summary>
    internal static Task<Rav1gLibraryStatus> Status => StatusValue.Value;

    /// <summary>The completed check found a usable library; false while it runs (dav1d is the safe default) or when not.</summary>
    internal static bool Usable => Completed is { Usable: true };

    /// <summary>The check's fixed words, for the log and the debug stats.</summary>
    internal static string Detail => Completed?.Detail ?? "rav1g.dll check pending";

    private static Rav1gLibraryStatus? Completed =>
        StatusValue.IsValueCreated && StatusValue.Value.IsCompletedSuccessfully ? StatusValue.Value.Result : null;

    // UWP API sets, as the other imports (DebugAudioProbe); pointer-only signatures for DisableRuntimeMarshalling.
    [DllImport("api-ms-win-core-libraryloader-l2-1-0.dll", ExactSpelling = true)]
    private static extern unsafe nint LoadPackagedLibrary(char* libraryName, uint reserved);

    [DllImport("api-ms-win-core-libraryloader-l1-2-0.dll", ExactSpelling = true)]
    private static extern int FreeLibrary(nint module);

    private static unsafe nint LoadPackaged(string name, out int error)
    {
        fixed (char* text = name)
        {
            var module = LoadPackagedLibrary(text, 0);
            // No SetLastError with DisableRuntimeMarshalling (CA1420): read the thread's OS error right after the call.
            error = module == 0 ? Marshal.GetLastSystemError() : 0;
            return module;
        }
    }

    private static unsafe Rav1gLibraryStatus Check()
    {
        Rav1gLibraryStatus status;
        nint module = 0;
        try
        {
            // rav1g is built and measured for Series consoles: elsewhere software video, rav1g's AV1 entry included, is
            // never claimed (DeviceIdentity), so the library is not even loaded.
            if (!DeviceIdentity.SoftwareVideoSupported) status = new(false, "rav1g.dll not checked (no software video on this console)");
            else if (!Present) status = Rav1gPolicy.Library(present: false, loaded: false, null, null);
            // LoadPackagedLibrary, as the fork's loader: .NET's library loader (LoadLibraryEx) refused the packaged
            // rav1g.dll on the console (2026-11-26, 0.2.803: "rav1g.dll failed to load" while the fork loaded it).
            else if ((module = LoadPackaged("rav1g.dll", out var error)) == 0)
            {
                Log.Info($"player: rav1g.dll LoadPackagedLibrary error {error}");
                status = Rav1gPolicy.Library(true, false, null, null);
            }
            else
            {
                var missing = Rav1gPolicy.LibraryExports.FirstOrDefault(name => !NativeLibrary.TryGetExport(module, name, out _));
                // rav1g_abi_version returns a constant: nothing else of the library runs (rav1g docs/integration.md).
                uint? abi = missing is null && NativeLibrary.TryGetExport(module, "rav1g_abi_version", out var export)
                    ? ((delegate* unmanaged<uint>)export)() : null;
                status = Rav1gPolicy.Library(true, true, missing, abi);
            }
        }
        catch (Exception ex)
        {
            status = new(false, $"rav1g.dll check failed ({ex.GetType().Name})");
        }
        finally
        {
            if (module != 0) FreeLibrary(module);
        }
        Log.Info($"player: {status.Detail}");
        return status;
    }
}
