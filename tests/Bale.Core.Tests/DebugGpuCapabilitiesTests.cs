namespace Bale.Core.Tests;

// These are source/ABI guards, not evidence that the native APIs work on a console.
public class DebugGpuCapabilitiesTests
{
    [Fact]
    public void D3D12RunsAfterD3D11CleanupAndKeepsIndependentSubprobes()
    {
        var source = Read();
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.True(source.IndexOf("AppendD3D12GpuCapabilities(builder);", StringComparison.Ordinal)
            > source.IndexOf("ReleaseGpuCom(videoDevice);", StringComparison.Ordinal));
        var probe = D3D12Probe(source);
        Assert.Contains("AppendGpuGameMode(builder);", probe);
        Assert.Contains("AppendGpuConsoleModel(builder);", probe);
        Assert.Contains("AppendGpuAdapters(builder, adapters, out dxgiLibrary);", probe);
        Assert.Contains("AppendD3D12Devices(builder, adapters);", probe);
        Assert.Contains("AppendD3D12Adapter(builder, device, adapters);", probe);
        Assert.Contains("AppendD3D12FeatureLevels(builder, device);", probe);
        Assert.Contains("AppendD3D12ShaderModel(builder, device);", probe);
        Assert.Contains("AppendD3D12ComputeQueue(builder, device);", probe);
    }

    [Theory]
    [InlineData("api-ms-win-gaming-expandedresources-l1-1-0.dll", "HasExpandedResources")]
    [InlineData("api-ms-win-gaming-deviceinformation-l1-1-0.dll", "GetGamingDeviceModelInformation")]
    [InlineData("dxgi.dll", "CreateDXGIFactory2")]
    [InlineData("d3d12.dll", "D3D12CreateDevice")]
    public void OptionalNativeExportsAreResolvedWithoutStaticImports(string library, string export)
    {
        var probe = D3D12Probe(Read());
        Assert.Contains($"\"{library}\"", probe);
        Assert.Contains($"\"{export}\"", probe);
        Assert.Contains("NativeLibrary.TryLoad(libraryName, out library)", probe);
        Assert.Contains("NativeLibrary.TryGetExport(library, exportName, out export)", probe);
        Assert.DoesNotContain("DllImport", probe);
    }

    [Fact]
    public void RawComSignaturesRetainSdkSlotsAndTheMsvcHiddenLuidReturnBuffer()
    {
        var probe = D3D12Probe(Read());
        Assert.Contains("unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)factory)[12]", probe);
        Assert.Contains("unmanaged[Stdcall]<IntPtr, DxgiAdapterDesc1*, int>)(*(void***)adapter)[10]", probe);
        Assert.Contains("unmanaged[Stdcall]<IntPtr, GpuLuid*, GpuLuid*>)(*(void***)device)[43]", probe);
        Assert.Contains("unmanaged[Stdcall]<IntPtr, int, void*, uint, int>)(*(void***)device)[13]", probe);
        Assert.Contains("unmanaged[Stdcall]<IntPtr, D3D12CommandQueueDesc*, Guid*, IntPtr*, int>)(*(void***)device)[8]", probe);
        Assert.Contains("getLuid(device, &luid);", probe);
        Assert.Contains("candidate.Luid.LowPart != luid.LowPart || candidate.Luid.HighPart != luid.HighPart", probe);
        Assert.Contains("public bool IsSoftware => (Flags & 2) != 0;", Read());
        Assert.Contains("if (adapter.IsSoftware) continue;", probe);
    }

    [Fact]
    public void FeatureQueriesUseRequestedLevelsAndOnlyRetryDefinedShaderModelsForInvalidArgument()
    {
        var probe = D3D12Probe(Read());
        Assert.Contains("{ 0xB000, 0xB100, 0xC000, 0xC100, 0xC200 }", probe);
        Assert.Contains("checkFeature(device, 2, &data, (uint)sizeof(D3D12FeatureDataFeatureLevels))", probe);
        Assert.Contains("[0x66, 0x65, 0x64, 0x63, 0x62, 0x61, 0x60, 0x51]", probe);
        Assert.Contains("checkFeature(device, 7, &data, (uint)sizeof(D3D12FeatureDataShaderModel))", probe);
        Assert.Contains("if (hr == unchecked((int)0x80070057) && model != 0x51) continue;", probe);
    }

    [Fact]
    public void NativeProbeErrorsNeverIncludeExceptionMessages()
    {
        var probe = D3D12Probe(Read());
        Assert.Contains("catch (Exception ex)", probe);
        Assert.Contains("unchecked((uint)ex.HResult):X8", probe);
        Assert.DoesNotContain("ex.Message", probe);
        Assert.DoesNotContain("Log.Error", probe);
    }

    private static string Read() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "DebugServer.Capabilities.cs.txt"));

    private static string D3D12Probe(string source) => source[
        source.IndexOf("    private static void AppendD3D12GpuCapabilities", StringComparison.Ordinal)..
        source.IndexOf("    private static DebugCodecNames KnownCodecNames", StringComparison.Ordinal)];
}
