namespace Bale.Core.Tests;

// Source-contract checks for 0019 (8-bit AV1 through the zero-copy direct write), not D3D/MF execution.
public class ZeroCopyEightBitPatchTests
{
    private static readonly string Patch = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "0019-zero-copy-eight-bit.patch"));
    private static readonly string Added = Lines('+');
    private static readonly string Removed = Lines('-');

    private static string Lines(char sign) => string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith(sign) && !line.StartsWith(new string(sign, 3), StringComparison.Ordinal))
        .Select(line => line[1..]));

    [Fact]
    public void PatchTouchesOnlyNativeSources()
    {
        var paths = Patch.Split('\n').Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray();
        Assert.Equal(new[]
        {
            "Source/BaleD3D11Upload.h",
            "Source/BaleFilmGrain.h",
            "Source/FFmpegMediaSource.cpp",
            "Source/FFmpegMediaSource.h",
            "Source/UncompressedVideoSampleProvider.cpp",
            "Source/UncompressedVideoSampleProvider.h",
        }, paths);
    }

    [Fact]
    public void EightBitOutputsP010OnlyWhenTheDirectWriteCanTakeIt()
    {
        Assert.Contains("GetProcAddress(module, \"bale_zc_repack_into_bits\")", Added);
        Assert.Contains("if (eightBitZeroCopy && input == AV_PIX_FMT_YUV420P) return AV_PIX_FMT_P010LE;", Added);
        // Variant 6 is the only upload whose pool textures the direct write can open.
        Assert.Contains("zeroCopy > 0 && uploadVariant == 6", Added);
        Assert.Contains("BaleZeroCopyExports::Get().repackIntoBits != nullptr", Added);
        Assert.Contains("config.Video().VideoOutputAllow10bit()", Added);
        // The pre-open decision feeds both the output format and the grain mode, then the provider.
        Assert.Contains("config.as<winrt::FFmpegInteropX::MediaSourceConfig>(), eightBitZeroCopy);", Added);
        Assert.Contains("uncompressed->ZeroCopyEightBit = eightBitZeroCopy && uncompressed->ZeroCopy > 0;", Added);
        Assert.Contains("uncompressed->FilmGrain.eightInP010 = uncompressed->ZeroCopyEightBit;", Added);
    }

    [Fact]
    public void EightBitPicturesUseByteStridesAndTheBitsExport()
    {
        Assert.Contains("bool eightBit = frame->format == AV_PIX_FMT_YUV420P && provider->ZeroCopyEightBit;", Added);
        Assert.Contains("size_t lumaStride = alignUp(width * sample);", Added);
        Assert.Contains("api.repackIntoBits(handle, picture, &frame, 8, directGrain, texture)", Added);
        // 10-bit frames keep the unchanged export and their landing fallback.
        Assert.Contains(": api.repackInto(handle, picture, &frame, directGrain, texture);", Added);
        Assert.DoesNotContain("api.repack(handle", Removed);
        Assert.DoesNotContain("CreateLandedSample", Removed);
    }

    [Fact]
    public void EightBitFramesNeverLand()
    {
        // bale_zc_repack reads 10-bit samples; an 8-bit picture handed to it would be misread.
        var guard = Added.IndexOf("if (eightBit && hr == S_FALSE) return false;", StringComparison.Ordinal);
        Assert.True(guard >= 0);
        Assert.Contains("eightBit ? \"using staging upload\" : \"landing frames\"", Added);
    }

    [Fact]
    public void EightBitGrainIsAppliedOnlyByTheDirectWrite()
    {
        // Staging, repack and CPU paths keep strict format matching: their grain would be wrong in P010.
        Assert.DoesNotContain("static bool Compatible", Removed);
        Assert.Contains("bool zeroCopyGrainOnly = hasGrain && !BaleFilmGrain::Compatible(", Added);
        Assert.Contains("params = nullptr;", Added);
        Assert.Contains("if (hasGrain && zeroCopyGrainOnly)", Added);
        Assert.Contains("reason = \"CPU grain cannot write 8-bit into P010; keeping dav1d\";", Added);
    }

    [Fact]
    public void StatsCountEightBitFrames()
    {
        Assert.Contains("if (eightBit) zeroCopyEightBitFrames++;", Added);
        Assert.Contains("direct=%u direct_grain=%u eight_bit=%u.", Added);
    }
}
