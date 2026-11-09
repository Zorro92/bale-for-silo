namespace Bale.Core.Tests;

// Source-contract checks for the 0013 diagnostic timing: log-only, no new options, numbers only.
public class UploadTimingPatchTests
{
    private static readonly string Patch = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "0013-d3d11-upload-stage-timing.patch"));
    private static readonly string[] Added = Patch.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..])
        .ToArray();

    [Fact]
    public void PatchTouchesOnlyNativeSources()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(new[]
        {
            "Source/BaleD3D11Upload.h", "Source/FFmpegMediaSource.cpp", "Source/FFmpegMediaSource.h",
            "Source/UncompressedVideoSampleProvider.cpp",
        }, paths);
    }

    [Fact]
    public void PatchOnlyAddsLines()
    {
        Assert.DoesNotContain(Patch.Split('\n'), line => line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal));
    }

    [Fact]
    public void AddsNoOptions()
    {
        Assert.DoesNotContain(Added, line => line.Contains("key == \"bale_", StringComparison.Ordinal));
    }

    [Fact]
    public void LogLinesUseTheBalePrefixAndNumericFormatsOnly()
    {
        var formats = Added.Where(line => line.Contains("\"Bale ", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, formats.Length);
        Assert.Contains(formats, line => line.Contains("\"Bale D3D11 upload timing: ", StringComparison.Ordinal));
        Assert.Contains(formats, line => line.Contains("\"Bale decode-ahead timing: ", StringComparison.Ordinal));
        Assert.DoesNotContain(Added, line => line.Contains("%s", StringComparison.Ordinal));
    }
}
