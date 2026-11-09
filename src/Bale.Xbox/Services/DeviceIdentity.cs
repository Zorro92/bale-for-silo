using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Bale.Core.Connection;
using Bale.Core.Playback;
using Windows.ApplicationModel;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.System.Profile;

namespace Bale.Xbox.Services;

internal static partial class DeviceIdentity
{
    public static bool IsXbox => AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";

    /// <summary>Whether this console may claim bundled software video (AV1); decided once per launch.</summary>
    public static bool SoftwareVideoSupported => SoftwareVideo.Value;

    private static readonly Lazy<bool> SoftwareVideo = new(() =>
    {
        // Either AVX2 signal is enough, so a quirk in one check can't drop AV1 on a Series console.
        var runtime = Read(() => Avx2.IsSupported);
        var platform = Read(() => IsProcessorFeaturePresent(PfAvx2InstructionsAvailable) != 0);
        string? form = null;
        try { form = AnalyticsInfo.DeviceForm; } catch { }
        var supported = SoftwareDecodePolicy.SoftwareVideoSupported(runtime || platform, form);
        Log.Info($"software video: form={form ?? "unknown"} avx2={(runtime ? "yes" : "no")}/{(platform ? "yes" : "no")} supported={supported}");
        return supported;
    });

    private const uint PfAvx2InstructionsAvailable = 40; // winnt.h PF_AVX2_INSTRUCTIONS_AVAILABLE

    [LibraryImport("kernel32.dll")]
    private static partial int IsProcessorFeaturePresent(uint processorFeature); // Win32 BOOL, not managed bool.

    private static bool Read(Func<bool> query)
    {
        try { return query(); }
        catch { return false; }
    }

    public static ClientIdentity Current()
    {
        var info = new EasClientDeviceInformation();
        var version = Package.Current.Id.Version;
        var product = string.IsNullOrWhiteSpace(info.SystemProductName) ? "Xbox" : info.SystemProductName;

        return new ClientIdentity(
            DeviceId: AppSettings.DeviceId,
            DeviceName: IsXbox ? product : info.FriendlyName,
            Platform: IsXbox ? "xbox" : "windows",
            ClientName: "Bale",
            ClientVersion: $"{version.Major}.{version.Minor}.{version.Build}",
            ClientBuild: version.Build.ToString(),
            ClientChannel: "sideload",
            ClientFamily: IsXbox ? "tv" : "desktop");
    }
}
