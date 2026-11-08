using System.Globalization;
using System.Text.RegularExpressions;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

// Source contracts only: Windows CI compiles the series; recycling and presentation need console testing.
public class D3d11UploadLifetimePatchTests
{
    private const string UploadPath = "Source/BaleD3D11Upload.h";
    private static readonly string Patch = Read("0008-d3d11-upload-processed-lifetime.patch");
    private static readonly string AddedSource = string.Join('\n', Patch.Split('\n')
        .Where(line => line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
        .Select(line => line[1..]));
    private static readonly string Upload = ApplyPatch(ApplyPatch(ApplyPatch("",
        Read("0006-d3d11-software-upload.patch")), Read("0007-d3d11-upload-variants-diagnostics.patch")), Patch);

    [Fact]
    public void LifetimePatchChangesOnlyUploadAndPoolHeaders()
    {
        Assert.Equal(new[] { UploadPath, "Source/TexturePool.h" }, Patch.Split('\n')
            .Where(line => line.StartsWith("+++ b/", StringComparison.Ordinal)).Select(line => line[6..]).ToArray());
        Assert.DoesNotContain("avcodec_open2", AddedSource);
        Assert.DoesNotContain("av_hwdevice_ctx_", AddedSource);
    }

    [Fact]
    public void TrackedSampleOwnsTheTextureAndDiagnosticPositionBeforeMfCanCloseIt()
    {
        Assert.Contains("winrt::event_token token;", Upload);
        Assert.Contains("winrt::com_ptr<ID3D11Texture2D> texture;\n        TimeSpan position;", Upload);
        Assert.Contains("std::map<winrt::Windows::Foundation::IUnknown, TrackedSample> trackedSamples;", Upload);
        Assert.Contains("trackedSamples.emplace(native, TrackedSample{ token, texture, position });", Upload);
        var processed = Upload[Upload.IndexOf("    void OnProcessed(", StringComparison.Ordinal)..];
        Assert.Contains("if (entry == trackedSamples.end()) return;", processed);
        Assert.Contains("auto tracked = std::move(entry->second);", processed);
        Assert.Contains("trackedSamples.erase(entry);", processed);
        Assert.Contains("DetachProcessed(native, tracked.token);", processed);
        Assert.Contains("texturePool.ReturnUploadTexture(std::move(tracked.texture));", processed);
        Assert.Contains("tracked.position.count()", processed);
        Assert.DoesNotContain("sender.Direct3D11Surface()", Upload);
        Assert.DoesNotContain("sender.Timestamp()", Upload);
        Assert.DoesNotContain("GetDXGISurface", processed);
    }

    [Fact]
    public void FlushDiscardsTrackedTexturesAndUnknownCallbacksCannotReturnThem()
    {
        var flush = Upload[Upload.IndexOf("    void Flush()", StringComparison.Ordinal)..Upload.IndexOf("private:", StringComparison.Ordinal)];
        Assert.Contains("DetachProcessed(entry.first, entry.second.token);", flush);
        Assert.Contains("trackedSamples.clear();", flush);
        Assert.Contains("texturePool.Clear();", flush);
        Assert.Contains("for (auto& texture : stagingTextures) texture = nullptr;", flush);
        Assert.DoesNotContain("ReturnUploadTexture", flush);
        Assert.DoesNotContain("issuedSamples =", flush);
        Assert.DoesNotContain("processedSamples =", flush);
        var processed = Upload[Upload.IndexOf("    void OnProcessed(", StringComparison.Ordinal)..];
        Assert.True(processed.IndexOf("if (entry == trackedSamples.end()) return;", StringComparison.Ordinal)
            < processed.IndexOf("ReturnUploadTexture", StringComparison.Ordinal));
        Assert.True(processed.IndexOf("trackedSamples.erase(entry);", StringComparison.Ordinal)
            < processed.IndexOf("DetachProcessed(native, tracked.token);", StringComparison.Ordinal));
    }

    [Fact]
    public void CleanupExceptionsAreLoggedOnceWithoutPoisoningUploadButD3dFailuresStillPropagate()
    {
        Assert.Contains("void LogProcessedFailure() noexcept", Upload);
        Assert.Contains("if (!processedFailureLogged.exchange(true))", Upload);
        Assert.Contains("std::atomic<bool> processedFailureLogged{ false };", Upload);
        Assert.Contains("void DetachProcessed(winrt::Windows::Foundation::IUnknown const& sample, winrt::event_token token) noexcept", Upload);
        Assert.Contains("catch (...)\n        {\n            LogProcessedFailure();\n        }", Upload);
        Assert.DoesNotContain("failure.load", Upload);
        Assert.DoesNotContain("failure.store", Upload);
        Assert.DoesNotContain("std::atomic<HRESULT>", Upload);
        Assert.Contains("winrt::check_hresult(device->GetDeviceRemovedReason());", Upload);
        Assert.Contains("auto hr = context->Map(staging.get(), 0, D3D11_MAP_WRITE, 0, &target.mapped);", Upload);
        Assert.Contains("operation = \"CreateFromDirect3D11Surface\";", Upload);
        Assert.Contains("static_cast<unsigned>(winrt::to_hresult()));\n            throw;", Upload);
    }

    [Theory]
    [InlineData(1, "D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE", "0")]
    [InlineData(2, "D3D11_BIND_DECODER | D3D11_BIND_SHADER_RESOURCE", "0")]
    [InlineData(3, "D3D11_BIND_DECODER", "0")]
    [InlineData(4, "D3D11_BIND_SHADER_RESOURCE", "0")]
    [InlineData(5, "D3D11_BIND_SHADER_RESOURCE", "D3D11_RESOURCE_MISC_SHARED")]
    public void VariantsDefaultToRenderTargetAndKeepTheKnownFailingModesSelectable(int variant, string bind, string misc)
    {
        Assert.Contains("UINT bindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;", Upload);
        Assert.Contains("UINT miscFlags = 0;", Upload);
        if (variant == 1)
        {
            Assert.Equal(SoftwareDecodeTuning.DefaultD3d11Upload, variant);
            return;
        }
        var expected = $"case {variant}: bindFlags = {bind};";
        if (misc != "0") expected += $" miscFlags = {misc};";
        Assert.Contains(expected + " break;", Upload);
    }

    [Fact]
    public void ReturningUploadTexturesChecksTheWholeDescriptorWithoutChangingHardwareReturns()
    {
        Assert.Contains("void ReturnUploadTexture(winrt::com_ptr<ID3D11Texture2D> texture)", AddedSource);
        foreach (var field in new[] { "Width", "Height", "Format", "MipLevels", "ArraySize", "SampleDesc.Count", "SampleDesc.Quality",
            "Usage", "BindFlags", "CPUAccessFlags", "MiscFlags" })
            Assert.Contains($"desc.{field} == desc_shared.{field}", AddedSource);
        Assert.Contains("pool.push_back(std::move(texture));", AddedSource);
        Assert.DoesNotContain("-    void ReturnTexture", Patch);
    }

    [Fact]
    public void FirstThreeProcessedDiagnosticsAndOneSteadySnapshotRemainSafeAndBounded()
    {
        Assert.Contains("if (++processedSamples <= 3)", Upload);
        Assert.Contains("Processed index=%u position_ms=%lld", Upload);
        Assert.Contains("if (++issuedSamples == 240)", Upload);
        Assert.Contains("Bale D3D11 upload steady: issued=%u processed=%u pooled=%zu", Upload);
        Assert.Contains("issuedSamples, processedSamples, texturePool.PooledTextureCount()", Upload);
        Assert.Contains("size_t PooledTextureCount() const { return pool.size(); }", AddedSource);
        var formats = Regex.Matches(AddedSource, "av_log\\(nullptr, AV_LOG_(?:INFO|WARNING), \"([^\"]+)\"")
            .Select(match => match.Groups[1].Value).ToArray();
        Assert.NotEmpty(formats);
        Assert.All(formats, format =>
        {
            Assert.StartsWith("Bale D3D11 upload", format);
            Assert.DoesNotContain("://", format);
            Assert.DoesNotContain("%ls", format);
        });
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file)).Replace("\r\n", "\n", StringComparison.Ordinal);

    // Reconstruct the upload header from its introducing patch and subsequent hunks, not just added lines.
    private static string ApplyPatch(string source, string patch)
    {
        var section = patch.Split("diff --git a/" + UploadPath + " b/" + UploadPath + "\n", StringSplitOptions.None)[1]
            .Split("diff --git ", StringSplitOptions.None)[0];
        var lines = source.Length == 0 ? [] : source.TrimEnd('\n').Split('\n');
        var result = new List<string>();
        var cursor = 0;
        var inHunk = false;
        foreach (var line in section.Split('\n'))
        {
            var hunk = Regex.Match(line, @"^@@ -(\d+)(?:,\d+)? \+\d+(?:,\d+)? @@");
            if (hunk.Success)
            {
                var start = Math.Max(0, int.Parse(hunk.Groups[1].Value, CultureInfo.InvariantCulture) - 1);
                while (cursor < start) result.Add(lines[cursor++]);
                inHunk = true;
                continue;
            }
            if (!inHunk || line.Length == 0) continue;
            if (line[0] is ' ' or '-') Assert.Equal(line[1..], lines[cursor++]);
            if (line[0] is ' ' or '+') result.Add(line[1..]);
        }
        result.AddRange(lines[cursor..]);
        return string.Join('\n', result) + "\n";
    }
}
