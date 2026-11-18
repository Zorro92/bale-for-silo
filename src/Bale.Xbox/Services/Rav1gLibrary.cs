#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>
/// Whether the package carries rav1g.dll (scripts/rav1g.json; CI packages none while that pin is empty). The fork
/// loads it with LoadPackagedLibrary from the package root and falls back to dav1d when it is missing.
/// </summary>
internal static class Rav1gLibrary
{
    private static readonly Lazy<bool> PresentValue = new(() =>
    {
        try { return File.Exists(Path.Combine(Windows.ApplicationModel.Package.Current.InstalledLocation.Path, "rav1g.dll")); }
        catch { return false; }
    });

    internal static bool Present => PresentValue.Value;
}
#endif
