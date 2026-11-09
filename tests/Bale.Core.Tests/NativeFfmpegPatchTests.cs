using System.Text.RegularExpressions;

namespace Bale.Core.Tests;

// Source-contract checks for the reviewed FFmpeg source patches applied by build-ffmpeg-native.ps1.
public class NativeFfmpegPatchTests
{
    private static readonly string PatchDirectory = Path.Combine(AppContext.BaseDirectory, "ffmpeg-native-patches");

    [Fact]
    public void PatchesTouchOnlyTheAllowedFfmpegFile()
    {
        var patches = Directory.GetFiles(PatchDirectory, "*.patch");
        Assert.NotEmpty(patches);
        foreach (var patch in patches)
        {
            var text = File.ReadAllText(patch);
            var targets = Regex.Matches(text, @"^\+\+\+ b/(\S+)$", RegexOptions.Multiline).Select(m => m.Groups[1].Value).Distinct();
            Assert.Equal(new[] { "libavcodec/libdav1d.c" }, targets);
            Assert.DoesNotContain("new file mode", text);
            Assert.DoesNotContain("deleted file mode", text);
        }
    }

    [Fact]
    public void LibDav1dPatchKeepsThePoolFallbackAndChecksTheAllocatorContract()
    {
        var patch = File.ReadAllText(Path.Combine(PatchDirectory, "0001-libdav1d-user-picture-buffers.patch"));
        Assert.Contains("+    if (c->get_buffer2 == avcodec_default_get_buffer2 ||", patch);
        Assert.Contains("+    if (libdav1d_user_picture(c, p, format, w, h) >= 0)", patch);
        Assert.Contains("+        frame->linesize[1] != frame->linesize[2])", patch);
        Assert.Contains("+            plane_size[i] + DAV1D_PICTURE_ALIGNMENT > (size_t)(end - frame->data[i]))", patch);
        Assert.Contains("+    s.allocator.cookie = c;", patch);
    }

    [Fact]
    public void BuildAppliesPatchesBeforeUpstreamBuildAndRecordsThem()
    {
        var build = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "build-ffmpeg-native.ps1.txt"));
        Assert.Contains("$allowedPatchFiles = @('libavcodec/libdav1d.c')", build);
        Assert.Contains("throw 'PatchRevision must be set exactly when FFmpeg source patches exist'", build);
        Assert.True(build.IndexOf("Invoke-Git $ffmpeg @('apply', $patch.FullName)", StringComparison.Ordinal)
            < build.IndexOf("-File ./Build-FFmpeg.ps1", StringComparison.Ordinal));
        Assert.Contains("FFmpeg source patch set:", build);
        Assert.Contains("Copy-Item $patch.FullName (Join-Path $output $patch.Name)", build);
        var workflow = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ffmpeg-native.yml.txt"));
        Assert.Contains("-PatchRevision $env:PATCH_REVISION", workflow);
        var restore = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "restore-ffmpeg-native.ps1.txt"));
        Assert.Contains("$patchRevision -cnotmatch '^(p[1-9][0-9]*)?$'", restore);
    }
}
