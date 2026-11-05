using System.Text.Json;
using System.Xml.Linq;

namespace Bale.Core.Tests;

public class NativeFfmpegPackageTests
{
    [Fact]
    public void ExactNativeReferenceAndBothLockEntriesMatchTheReviewedAsset()
    {
        using var pin = JsonDocument.Parse(Read("ffmpeg-native.json"));
        var version = pin.RootElement.GetProperty("version").GetString();
        Assert.Equal("8.1.2-bale.dav1d.1.5.4", version);
        Assert.Matches("^[0-9a-f]{64}$", pin.RootElement.GetProperty("sha256").GetString()!);
        Assert.Equal("1.5.4", pin.RootElement.GetProperty("dav1d_version").GetString());
        Assert.Equal("54706fc6bc0cdecab7e9593974a4039cc038fca7", pin.RootElement.GetProperty("dav1d_commit").GetString());
        var project = XDocument.Parse(Read("Bale.Xbox.csproj.txt"));
        var native = project.Descendants("PackageReference").Single(package => package.Attribute("Include")!.Value == "FFmpegInteropX.UWP.FFmpeg");
        Assert.Equal($"[{version}]", native.Attribute("Version")!.Value);
        using var packages = JsonDocument.Parse(Read("Bale.Xbox.packages.lock.json"));
        var entries = packages.RootElement.GetProperty("dependencies").EnumerateObject()
            .Select(framework => framework.Value.GetProperty("FFmpegInteropX.UWP.FFmpeg")).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry =>
        {
            Assert.Equal(version, entry.GetProperty("resolved").GetString());
            Assert.Equal($"[{version}, {version}]", entry.GetProperty("requested").GetString());
            Assert.Equal(pin.RootElement.GetProperty("nuget_content_hash").GetString(), entry.GetProperty("contentHash").GetString());
        });
    }

    [Fact]
    public void CustomNativeIdIsMappedOnlyToTheLocalFeed()
    {
        var config = XDocument.Parse(Read("NuGet.Config"));
        var mappings = config.Descendants("packageSource").ToArray();
        var native = mappings.Single(source => source.Attribute("key")!.Value == "bale-native");
        Assert.Equal("FFmpegInteropX.UWP.FFmpeg", Assert.Single(native.Elements("package")).Attribute("pattern")!.Value);
        Assert.Equal("*", Assert.Single(mappings.Single(source => source.Attribute("key")!.Value == "nuget.org").Elements("package")).Attribute("pattern")!.Value);
        Assert.Contains(config.Descendants("add"), source => source.Attribute("key")?.Value == "bale-native"
            && source.Attribute("value")?.Value == ".cache/ffmpeg-native");
    }

    [Fact]
    public void WindowsBuildVerifiesTheReleaseBeforeRestoreAndDoesNotTrustCachedNativeBits()
    {
        var workflow = Read("ci.yml.txt");
        Assert.True(workflow.IndexOf("./scripts/restore-ffmpeg-native.ps1", StringComparison.Ordinal)
            < workflow.IndexOf("msbuild src/Bale.Xbox/Bale.Xbox.csproj /restore", StringComparison.Ordinal));
        Assert.Contains("scripts/ffmpeg-native.json", workflow);
        Assert.Contains("./scripts/test-ffmpeg-checks.ps1", workflow);
        var download = Read("restore-ffmpeg-native.ps1.txt");
        Assert.Contains("Get-FileHash $download -Algorithm SHA256", download);
        Assert.True(download.IndexOf("if ($actual -ne $pin.sha256)", StringComparison.Ordinal)
            < download.IndexOf("Move-Item $download", StringComparison.Ordinal));
        Assert.Contains("ffmpeginteropx.uwp.ffmpeg/$($pin.version)", download);
        Assert.Contains("Remove-Item $cached -Recurse -Force", download);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
