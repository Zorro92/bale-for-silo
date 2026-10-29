using Bale.Core.Connection;
using Windows.ApplicationModel;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.System.Profile;

namespace Bale.Xbox.Services;

internal static class DeviceIdentity
{
    public static bool IsXbox => AnalyticsInfo.VersionInfo.DeviceFamily == "Windows.Xbox";

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
