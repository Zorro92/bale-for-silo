using System.Text.Json;
using System.Xml.Linq;

namespace Bale.Core.Tests;

public class NativeFfmpegPackageTests
{
    [Theory]
    [InlineData("ffmpeg-native.json", "FFmpegInteropX.UWP.FFmpeg", "9.1.0-bale.dav1d.1.5.5")]
    [InlineData("ffmpeginteropx-wrapper.json", "FFmpegInteropX.UWP.Lib", "2.1.0-bale.84")]
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
        Assert.Equal("1.5.5", native.RootElement.GetProperty("dav1d_version").GetString());
        Assert.Equal("6c7bee685677fd997a431302be4fea8ea4017606", native.RootElement.GetProperty("dav1d_commit").GetString());
        using var wrapper = JsonDocument.Parse(Read("ffmpeginteropx-wrapper.json"));
        Assert.Equal("wrapper-ffmpeginteropx-2.1.0-bale.84", wrapper.RootElement.GetProperty("release_tag").GetString());
        Assert.Equal("2991da8f770e4dbe2039ba363ab6b4cddea58b6d7b557125f995f60b1c079885", wrapper.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("ixk2qP2q60tqyg2uuJdc1koBZFfXNARcfQrEoTDIkYtVPaEJyMv6TN+VBj0F6NQUNhcHakWsXvq2m/wBTj+4HA==", wrapper.RootElement.GetProperty("nuget_content_hash").GetString());
        Assert.Equal("Zorro92/bale-ffmpeginteropx", wrapper.RootElement.GetProperty("release_repository").GetString());
        Assert.Equal("9e34046460c0e8595292d5dc6725b8c7e3412850", wrapper.RootElement.GetProperty("source_commit").GetString());
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
        Assert.Contains("Repository = 'Zorro92/bale-for-silo'", download);
        Assert.Contains("Repository = $wrapperPin.release_repository", download);
        Assert.Contains("https://github.com/$($package.Repository)/releases/download/", download);
        Assert.Contains("Get-WrapperPin", download);
        Assert.Contains("./scripts/test-ffmpeginteropx-wrapper.ps1", workflow);
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

    [Fact]
    public void AppCiInspectsTheLiveExportsOfTheActualPackagedDll()
    {
        var workflow = Read("ci.yml.txt");
        var start = workflow.IndexOf("$entry = $zip.GetEntry('bale_gpu.dll')", StringComparison.Ordinal);
        var end = workflow.IndexOf("exports present, $($absent.Count) absent\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var inspection = workflow[start..end];
        Assert.Contains("ExtractToFile($entry, $packagedDll)", inspection);
        Assert.Contains("$exports = & $dumpbin.FullName /exports $packagedDll", inspection);
        Assert.Contains("if ($LASTEXITCODE -ne 0)", inspection);
        var common = inspection.IndexOf("$common = @(", StringComparison.Ordinal);
        var debug = inspection.IndexOf("$debugOnly = @(", StringComparison.Ordinal);
        var removed = inspection.IndexOf("$removed = @(", StringComparison.Ordinal);
        Assert.True(common >= 0 && debug > common && removed > debug);
        // Grain ABI 2: the GPU exports the wrapper loads stay required, and so does the ledger's accounting the fork calls;
        // CPU grain must be gone. The ledger's readers are debug-server exports (Cargo feature debug-probes).
        foreach (var symbol in new[] { "bale_grain_abi_version", "bale_grain_validate", "bale_grain_gpu_create",
            "bale_grain_gpu_apply", "bale_grain_gpu_destroy", "bale_mem_abi_version", "bale_mem_account" })
            Assert.Contains($"'{symbol}'", inspection[common..debug]);
        foreach (var symbol in new[] { "bale_mem_snapshot", "bale_mem_reset_peaks", "bale_mem_underflows" })
        {
            Assert.Contains($"'{symbol}'", inspection[debug..removed]);
            Assert.DoesNotContain($"'{symbol}'", inspection[common..debug]);
        }
        Assert.Contains("'bale_grain_cpu_apply'", inspection[removed..]);
        Assert.DoesNotContain("'bale_grain_cpu_apply'", inspection[common..removed]);
        // Debug-server DLLs need the debug list too; production DLLs must not export it.
        Assert.Contains("if ('${{ matrix.debug_server }}' -eq 'true') { $required = $common + $debugOnly; $absent = $removed }", inspection);
        Assert.Contains("else { $required = $common; $absent = $removed + $debugOnly }", inspection);
        Assert.Contains("throw \"Packaged bale_gpu.dll is missing export $symbol\"", inspection);
        Assert.Contains("throw \"Packaged bale_gpu.dll (debug_server=${{ matrix.debug_server }}) still exports $symbol\"", inspection);
        // The debug-server DLL is built with both features, production with neither.
        Assert.Contains("if ('${{ matrix.debug_server }}' -eq 'true') { $debugFeatures = @('--features', 'stage-timing,debug-probes') }", workflow);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
