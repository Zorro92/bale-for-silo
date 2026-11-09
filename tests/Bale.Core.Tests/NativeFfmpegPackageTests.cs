using System.Text.Json;
using System.Xml.Linq;

namespace Bale.Core.Tests;

public class NativeFfmpegPackageTests
{
    [Theory]
    [InlineData("ffmpeg-native.json", "FFmpegInteropX.UWP.FFmpeg", "8.1.2-bale.dav1d.1.5.4")]
    [InlineData("ffmpeginteropx-wrapper.json", "FFmpegInteropX.UWP.Lib", "2.1.0-bale.13")]
    public void ExactReferenceAndBothLockEntriesMatchTheReviewedAsset(string pinFile, string packageId, string expectedVersion)
    {
        using var pin = JsonDocument.Parse(Read(pinFile));
        var version = pin.RootElement.GetProperty("version").GetString();
        Assert.Equal(expectedVersion, version);
        Assert.Matches("^[0-9a-f]{64}$", pin.RootElement.GetProperty("sha256").GetString()!);
        var project = XDocument.Parse(Read("Bale.Xbox.csproj.txt"));
        var native = project.Descendants("PackageReference").Single(package => package.Attribute("Include")!.Value == packageId);
        Assert.Equal($"[{version}]", native.Attribute("Version")!.Value);
        Assert.Equal("true", native.Attribute("GeneratePathProperty")!.Value);
        using var packages = JsonDocument.Parse(Read("Bale.Xbox.packages.lock.json"));
        var entries = packages.RootElement.GetProperty("dependencies").EnumerateObject()
            .Select(framework => framework.Value.GetProperty(packageId)).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry =>
        {
            Assert.Equal(version, entry.GetProperty("resolved").GetString());
            Assert.Equal($"[{version}, {version}]", entry.GetProperty("requested").GetString());
            Assert.Equal(pin.RootElement.GetProperty("nuget_content_hash").GetString(), entry.GetProperty("contentHash").GetString());
        });
    }

    [Fact]
    public void PinsRetainTheReviewedDav1dAndWrapperReleaseProvenance()
    {
        using var native = JsonDocument.Parse(Read("ffmpeg-native.json"));
        Assert.Equal("1.5.4", native.RootElement.GetProperty("dav1d_version").GetString());
        Assert.Equal("54706fc6bc0cdecab7e9593974a4039cc038fca7", native.RootElement.GetProperty("dav1d_commit").GetString());
        using var wrapper = JsonDocument.Parse(Read("ffmpeginteropx-wrapper.json"));
        Assert.Equal("wrapper-ffmpeginteropx-2.1.0-bale.13", wrapper.RootElement.GetProperty("release_tag").GetString());
        Assert.Equal("8c3f653589bbb6f6faef0b4153ac82f46d6163ed1e662c1fea9002db2b9849d4", wrapper.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("khM1WpVCJkLdoWoIcd9G4gzRfCFgBBQQfLNCis8dLEh0s82gqlAEYDIafZfruf8Soqz04NZn3c1tiPy2K28NOg==", wrapper.RootElement.GetProperty("nuget_content_hash").GetString());
        Assert.Equal(native.RootElement.GetProperty("upstream_commit").GetString(), wrapper.RootElement.GetProperty("upstream_commit").GetString());
    }

    [Fact]
    public void BothForkIdsAreMappedOnlyToTheLocalFeed()
    {
        var config = XDocument.Parse(Read("NuGet.Config"));
        var mappings = config.Descendants("packageSource").ToArray();
        var native = mappings.Single(source => source.Attribute("key")!.Value == "bale-native");
        Assert.Equal(new[] { "FFmpegInteropX.UWP.FFmpeg", "FFmpegInteropX.UWP.Lib" },
            native.Elements("package").Select(package => package.Attribute("pattern")!.Value).ToArray());
        Assert.Equal("*", Assert.Single(mappings.Single(source => source.Attribute("key")!.Value == "nuget.org").Elements("package")).Attribute("pattern")!.Value);
        Assert.Equal(2, mappings.Length);
        Assert.Contains(config.Descendants("add"), source => source.Attribute("key")?.Value == "bale-native"
            && source.Attribute("value")?.Value == ".cache/ffmpeg-native");
    }

    [Fact]
    public void WindowsBuildVerifiesBothReleasesBeforeRestoreAndDoesNotTrustCachedNativeBits()
    {
        var workflow = Read("ci.yml.txt");
        Assert.True(workflow.IndexOf("./scripts/restore-ffmpeg-native.ps1", StringComparison.Ordinal)
            < workflow.IndexOf("msbuild src/Bale.Xbox/Bale.Xbox.csproj /restore", StringComparison.Ordinal));
        Assert.Contains("scripts/ffmpeg-native.json", workflow);
        Assert.Contains("scripts/ffmpeginteropx-wrapper.json", workflow);
        Assert.Contains("./scripts/test-ffmpeg-checks.ps1", workflow);
        var download = Read("restore-ffmpeg-native.ps1.txt");
        Assert.Contains("Get-FileHash $download -Algorithm SHA256", download);
        Assert.True(download.IndexOf("if ($actual -ne $pin.sha256)", StringComparison.Ordinal)
            < download.IndexOf("Move-Item $download", StringComparison.Ordinal));
        Assert.Contains("Id = 'FFmpegInteropX.UWP.FFmpeg'; Pin = $nativePin", download);
        Assert.Contains("Id = 'FFmpegInteropX.UWP.Lib'; Pin = $wrapperPin", download);
        Assert.Contains("$($package.Id.ToLowerInvariant())/$($pin.version)", download);
        Assert.Contains("Remove-Item $cached -Recurse -Force", download);
        Assert.Contains("if (-not $NativeOnly)", download);
        Assert.Contains("restore-ffmpeg-native.ps1\" -NativeOnly", Read("build-ffmpeginteropx-wrapper.ps1.txt"));
    }

    [Fact]
    public void MsixCheckerComparesTheWrapperAndProvenanceWithTheHashPinnedPackage()
    {
        var check = Read("check-ffmpeg-package.ps1.txt");
        Assert.Contains("Get-FileHash $WrapperPackagePath -Algorithm SHA256", check);
        Assert.Contains("if ($actual -ne $ExpectedWrapperPackageSha256)", check);
        Assert.Contains("'FFmpegInteropX.dll' = 'runtimes/win-x64/native/FFmpegInteropX.dll'", check);
        Assert.Contains("'Licenses/FFmpegInteropX/SOURCES.txt' = 'SOURCES.txt'", check);
        Assert.Contains("if ($shippedHash -ne $expectedHash)", check);
        Assert.Contains("check-dav1d-version.ps1", check);
        Assert.Contains("ActivatableClass", check);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
