#if DEBUG_SERVER
namespace Bale.Xbox.Services;

/// <summary>The words bale_gpu's tone map accepts in BALE_TONEMAP_DEBUG (debug builds only).</summary>
internal static class DebugToneMapSwitches
{
    private static readonly string[] Known = ["noselftest", "nostate"];

    public static bool Valid(string flags) =>
        flags.Split(',', StringSplitOptions.RemoveEmptyEntries).All(word => Known.Contains(word));
}
#endif
