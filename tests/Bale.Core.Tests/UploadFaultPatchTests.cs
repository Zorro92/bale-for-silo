namespace Bale.Core.Tests;

// Source-contract checks for the 0011 debug upload fault, not D3D/MF execution.
public class UploadFaultPatchTests
{
    private static readonly string Patch = Read("0011-d3d11-upload-fault-injection.patch");
    private static readonly string Added = string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..]));

    [Fact]
    public void PatchTouchesOnlyNativeSourcesAndRemovesNothing()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(new[] { "Source/FFmpegMediaSource.cpp", "Source/FFmpegMediaSource.h", "Source/UncompressedVideoSampleProvider.cpp", "Source/UncompressedVideoSampleProvider.h" }, paths);
        Assert.DoesNotContain(Patch.Split('\n'), line => line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal));
    }

    [Fact]
    public void OptionIsBoundedAndOffByDefault()
    {
        Assert.Contains("else if (key == \"bale_d3d11_upload_fault_frames\") { target = &baleD3d11UploadFaultFrames; minimum = 0; maximum = 999; }", Added);
        Assert.Contains("int baleD3d11UploadFaultFrames = 0;", Added);
        Assert.Contains("int UploadFaultFrames = 0;", Added);
        Assert.Contains("uncompressed->UploadFaultFrames = baleD3d11UploadFaultFrames;", Added);
    }

    [Fact]
    public void FaultThrowsBeforeSampleCreationAndCountsOnlySuccessfulUploads()
    {
        var fault = Added.IndexOf("if (UploadFaultFrames > 0 && uploadedFrames >= UploadFaultFrames)", StringComparison.Ordinal);
        var thrown = Added.IndexOf("winrt::throw_hresult(E_OUTOFMEMORY);", StringComparison.Ordinal);
        var counted = Added.IndexOf("uploadedFrames++;", StringComparison.Ordinal);
        Assert.True(fault >= 0 && thrown > fault && counted > thrown);
        Assert.True(Patch.IndexOf("uploadSample = upload->CreateSample(", StringComparison.Ordinal) < Patch.IndexOf("+    uploadedFrames++;", StringComparison.Ordinal));
        Assert.Contains("\"Bale D3D11 upload fault injected after %d frames.\\n\", uploadedFrames", Added);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
