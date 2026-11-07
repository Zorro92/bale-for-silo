namespace Bale.Core.Tests;

// Source/ABI guards only: native platform support still needs verification on the console.
public class DebugPlatformCapabilitiesTests
{
    [Fact]
    public void PlatformSectionIsGuardedAndRunsBetweenDisplayAndTrailingAudio()
    {
        var source = Read();
        Assert.StartsWith("#if DEBUG_SERVER", source);
        var display = source.IndexOf("if (Run(\"display\"))", StringComparison.Ordinal);
        var platform = source.IndexOf("if (Run(\"platform\")) await AppendPlatformCapabilitiesAsync(builder);", StringComparison.Ordinal);
        var audio = source.IndexOf("builder.Append(\"Audio:\\n\");", StringComparison.Ordinal);
        Assert.True(display >= 0 && platform > display && audio > platform);
        Assert.Contains("Log.Info($\"Debug caps: {section}\");", source);
        Assert.Contains("video|gpu|audio|display|platform|device|graph", source);
        var server = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.cs.txt"));
        Assert.Contains("video|gpu|audio|display|platform|device|graph", server);
        var script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "bale-debug.sh"));
        Assert.Contains("video, gpu, audio, display, platform, device or graph", script);
    }

    [Theory]
    [InlineData("SSE4.1", 37, "PF_SSE4_1_INSTRUCTIONS_AVAILABLE")]
    [InlineData("SSE4.2", 38, "PF_SSE4_2_INSTRUCTIONS_AVAILABLE")]
    [InlineData("AVX", 39, "PF_AVX_INSTRUCTIONS_AVAILABLE")]
    [InlineData("AVX2", 40, "PF_AVX2_INSTRUCTIONS_AVAILABLE")]
    [InlineData("AVX512F", 41, "PF_AVX512F_INSTRUCTIONS_AVAILABLE")]
    public void ProcessorFeaturesUseWinntHeaderValues(string name, uint value, string constant)
    {
        var probe = PlatformProbe(Read());
        Assert.Contains($"(\"{name}\", {value}), // {constant}", probe);
        Assert.Contains("[LibraryImport(\"kernel32.dll\")]", probe);
        Assert.Contains("partial int IsProcessorFeaturePresent(uint processorFeature)", probe);
        Assert.Contains("ReadPlatformCapability(() => IsProcessorFeaturePresent(feature) != 0", probe);
        Assert.Contains("ReadPlatformCapability(() => Avx2.IsSupported", probe);
        Assert.Contains("ReadPlatformCapability(() => Avx512F.IsSupported", probe);
    }

    [Theory]
    [InlineData("AppMemoryUsage")]
    [InlineData("AppMemoryUsageLimit")]
    [InlineData("ExpectedAppMemoryUsageLimit")]
    public void EachMemoryManagerPropertyIsIndependentlyGuardedAndReportedInMiB(string property)
    {
        Assert.Contains("ReadPlatformCapability(() => $\"{MemoryManager." + property + " / (1024 * 1024)} MiB\")", Read());
    }

    [Fact]
    public void ExtendedMemoryRetainsDocumentedLayoutFactoryInterfaceAndSlots()
    {
        var probe = PlatformProbe(Read());
        Assert.Contains("[StructLayout(LayoutKind.Sequential)]\n    private struct ExtendedMemoryInfo\n    {\n"
            + "        public ulong AppMemoryUsage;\n        public ulong AppMemoryLimit;\n"
            + "        public ulong ExtendedMemoryUsage;\n        public ulong ExtendedMemoryLimit;\n    }", probe);
        Assert.Contains("Windows.Xbox.ApplicationModel.ApplicationResources", probe);
        Assert.Contains("af86e2e0-b12d-4c6a-9c5a-d7aa65101e90", probe);
        Assert.Contains("dd36a017-b640-45f7-a023-1615cf098923", probe);
        Assert.Contains("[LibraryImport(\"combase.dll\")]", probe);
        Assert.Contains("WindowsCreateString(char* sourceString, uint length, nint* hstring)", probe);
        Assert.Contains("RoGetActivationFactory(nint classId, Guid* iid, nint* factory)", probe);
        Assert.Contains("unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)factory)[0]", probe);
        Assert.Contains("unmanaged[Stdcall]<nint, ExtendedMemoryInfo*, int>)(*(void***)resources)[6]", probe);
        Assert.Contains("Marshal.ThrowExceptionForHR(getExtendedMemoryInfo(resources, &memory))", probe);
        Assert.Contains("finally\n        {\n            ReleasePlatformCom(builder, resources);\n            ReleasePlatformCom(builder, factory);", probe);
        Assert.Contains("Marshal.ThrowExceptionForHR(WindowsDeleteString(classId))", probe);
        Assert.Contains("unmanaged[Stdcall]<nint, uint>)(*(void***)instance)[2]", probe);
        Assert.Contains("Extended memory: app=", probe);
        Assert.Contains("graphics={memory.ExtendedMemoryUsage", probe);
    }

    [Theory]
    [InlineData("HEVC 4K decode", "hvc1", "decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=30,decode-bpc=10")]
    [InlineData("HEVC 4K decode+display", "hvc1", "decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=30,decode-bpc=10,display-res-x=3840,display-res-y=2160,display-bpc=8")]
    [InlineData("HEVC 4K HDR", "hvc1", "decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=30,decode-bpc=10,display-res-x=3840,display-res-y=2160,display-bpc=10,hdr=1")]
    [InlineData("HEVC 4K60 HDR", "hvc1", "decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=60,decode-bpc=10,display-res-x=3840,display-res-y=2160,display-bpc=10,hdr=1")]
    [InlineData("AV1 4K HDR", "av01", "decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=30,decode-bpc=10,display-res-x=3840,display-res-y=2160,display-bpc=10,hdr=1")]
    [InlineData("VP9 4K HDR", "vp09", "decode-res-x=3840,decode-res-y=2160,decode-bitrate=20000,decode-fps=30,decode-bpc=10,display-res-x=3840,display-res-y=2160,display-bpc=10,hdr=1")]
    [InlineData("HDCP 2.2", "hvc1", "hdcp=2")]
    public void ProtectionTypesKeepExactCodecsFeaturesAndInnerQuotes(string name, string codec, string features)
    {
        var type = $"video/mp4;codecs=\\\"{codec},mp4a\\\";features=\\\"{features}\\\"";
        Assert.Contains($"(\"{name}\", \"{type}\")", Read());
    }

    [Fact]
    public void ProtectionReusesOneInstanceRetriesOnlyMaybeAndRunsHdcpLast()
    {
        var probe = PlatformProbe(Read());
        Assert.Single(probe.Split("new ProtectionCapabilities()", StringSplitOptions.None).Skip(1));
        Assert.True(probe.IndexOf("new ProtectionCapabilities()", StringComparison.Ordinal)
            < probe.IndexOf("foreach (var (name, type) in PlatformProtectionTypes)", StringComparison.Ordinal));
        Assert.Contains("protection.IsTypeSupported(type, \"com.microsoft.playready.hardware\")", probe);
        Assert.Contains("while (attempts < 10)", probe);
        Assert.Contains("if (result != ProtectionCapabilityResult.Maybe || attempts == 10) break;\n                    await Task.Delay(100);", probe);
        Assert.Contains("if (attempts > 1) builder.Append($\" ({attempts} attempts)\");", probe);
        Assert.Contains("(\"HDCP 2.2\", \"video/mp4;codecs=\\\"hvc1,mp4a\\\";features=\\\"hdcp=2\\\"\"),\n    ];", probe);
    }

    [Fact]
    public void PlatformFailuresStayBoundedAndNeverIncludeExceptionMessages()
    {
        var probe = PlatformProbe(Read());
        Assert.Contains("catch (Exception ex) { return PlatformUnavailable(ex); }", probe);
        Assert.Contains("DebugScreenText.Compact($\"unavailable (0x{unchecked((uint)ex.HResult):X8})\", 120)", probe);
        Assert.DoesNotContain("ex.Message", probe);
        Assert.DoesNotContain("Log.Error", probe);
        Assert.DoesNotContain("DllImport", probe);
    }

    private static string Read() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.Capabilities.cs.txt"));

    private static string PlatformProbe(string source) => source[
        source.IndexOf("    // Windows SDK winnt.h", StringComparison.Ordinal)..
        source.IndexOf("    // GUIDs, descriptor layout and ID3D11VideoDevice", StringComparison.Ordinal)];
}
