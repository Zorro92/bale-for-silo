using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

[Collection("Player seek")]
public sealed class DebugDav1dPathSwitchTests
{
    private static PlaybackRouteDecision Route(string codec = "av1", bool software = true) =>
        new(true, software, codec, "aac", 0, 1920, 1080);

    [Fact]
    public void SwitchDefaultsToD3d11AndCapturesOnlyValidatedMemoryValues()
    {
        var previous = DebugDav1dPathSwitch.Mode;
        try
        {
            Assert.Equal("d3d11", previous);
            Assert.True(DebugDav1dPathSwitch.TrySet("zerocopy"));
            var captured = DebugDav1dPathSwitch.Mode;
            foreach (var invalid in new string?[] { null, "", "D3D11", "gpu", "d3d12", "zerocopy ", "d3d11&extra=1" })
            {
                Assert.False(DebugDav1dPathSwitch.TrySet(invalid));
                Assert.Equal(captured, DebugDav1dPathSwitch.Mode);
            }
            Assert.True(DebugDav1dPathSwitch.TrySet("d3d11"));
            Assert.Equal("zerocopy", captured);
        }
        finally { DebugDav1dPathSwitch.TrySet(previous); }
        var source = Read("DebugDav1dPathSwitch.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.DoesNotContain("Settings", source);
        Assert.DoesNotContain("Storage", source);
        var player = Read("PlayerPage.cs.txt");
        const string capture = "public string Dav1dPath { get; } = DebugDav1dPathSwitch.Mode;";
        Assert.Contains(capture, player);
        Assert.DoesNotContain("DebugDav1dPathSwitch", player.Replace(capture, "", StringComparison.Ordinal));
        Assert.Contains("FfmpegSource.Configuration(media.Route, attempt.FfmpegTuning, media.Rav1g, attempt.Dav1dPath,\n                    attempt.Dav1dGpuFilmGrain, attempt.SdrWhiteNits)",
            Read("PlayerPage.Media.cs.txt"));
        Assert.DoesNotContain("DebugDav1dPathSwitch", Read("FfmpegSource.cs.txt"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void D3d11NeedsNoRegistrationAndZeroCopyFailureKeepsCpuOutput(bool rav1g)
    {
        var previous = ZeroCopyRuntime.Available;
        var registrations = ZeroCopyRuntime.Registrations;
        try
        {
            ZeroCopyRuntime.Available = false;
            var decision = rav1g ? new Rav1gRouteDecision(true, 250, null, null, false, new(null, null, null, null, null)) : null;
            var cpu = FfmpegSource.Configuration(Route(), new(), decision);
            Assert.Equal(registrations, ZeroCopyRuntime.Registrations);
            var declined = FfmpegSource.Configuration(Route(), new(), decision, "zerocopy");
            Assert.Equal(registrations + 1, ZeroCopyRuntime.Registrations);
            foreach (var config in new[] { cpu, declined })
            {
                Assert.Equal("0", config.FFmpegOptions["bale_film_grain_mode"]);
                Assert.DoesNotContain("bale_d3d11_upload", config.FFmpegOptions.Keys);
                Assert.DoesNotContain("bale_zero_copy", config.FFmpegOptions.Keys);
                if (rav1g) Assert.Equal("1080", config.FFmpegOptions["bale_av1_fallback_max_height"]);
            }
            Assert.Contains("player: dav1d zero-copy unavailable; using d3d11", Log.Messages);
        }
        finally { ZeroCopyRuntime.Available = previous; }
    }

    [Theory]
    [InlineData(null, "0", false)]
    [InlineData("dav1d", "0", false)]
    [InlineData("off", "0", true)]
    public void D3d11GrainOverridesKeepCpuBuffers(string? grain, string mode, bool exportOnly)
    {
        Assert.True(DebugFfmpegPlaytest.TryParseGrain(grain, out var selection, out _));
        var tuning = new DebugFfmpegInputTuning(FilmGrain: selection);
        var config = FfmpegSource.Configuration(Route(), tuning);
        Assert.Equal(mode, config.FFmpegOptions["bale_film_grain_mode"]);
        Assert.Equal(exportOnly, config.FFmpegOptions.ContainsKey("bale_dav1d_export_film_grain"));
        if (exportOnly) Assert.Equal("1", config.FFmpegOptions["bale_dav1d_export_film_grain"]);
        Assert.DoesNotContain("bale_d3d11_upload", config.FFmpegOptions.Keys);
        Assert.DoesNotContain("bale_zero_copy", config.FFmpegOptions.Keys);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroCopyRestoresOldSettingsOnlyForSoftwareAv1(bool rav1g)
    {
        var decision = rav1g ? new Rav1gRouteDecision(true, 250, null, null, false, new(null, null, null, null, null)) : null;
        var config = FfmpegSource.Configuration(Route(), new(), decision, "zerocopy");
        Assert.Equal("6", config.FFmpegOptions["bale_d3d11_upload"]);
        // Wrapper bale.82 on stock FFmpeg 9.1 (no p1 picture-memory patch): the zero-copy key is gone.
        Assert.DoesNotContain("bale_zero_copy", config.FFmpegOptions.Keys);
        Assert.Equal("2", config.FFmpegOptions["bale_film_grain_mode"]);
        if (rav1g) Assert.Equal("1080", config.FFmpegOptions["bale_av1_fallback_max_height"]);
        foreach (var route in new[] { Route("mpeg4"), Route("hevc", false) })
        {
            var other = FfmpegSource.Configuration(route, new(), decision, "zerocopy");
            Assert.DoesNotContain("bale_zero_copy", other.FFmpegOptions.Keys);
            Assert.DoesNotContain("bale_d3d11_upload", other.FFmpegOptions.Keys);
        }
        var off = FfmpegSource.Configuration(Route(), new(FilmGrain: DebugFilmGrain.Off), decision, "zerocopy");
        Assert.Equal("2", off.FFmpegOptions["bale_film_grain_mode"]);
        Assert.Equal("1", off.FFmpegOptions["bale_dav1d_export_film_grain"]);
    }

    [Fact]
    public void ServerRejectsDuplicateAndUnexpectedKeysAndDocumentsTheSwitch()
    {
        var server = Read("DebugServer.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", server);
        Assert.Contains("\"/dav1d-path\"", server.Split('\n').Single(line => line.Contains("duplicateQueryKeys && path is", StringComparison.Ordinal)));
        var get = server[server.IndexOf("case (\"GET\", \"/dav1d-path\")", StringComparison.Ordinal)
            ..server.IndexOf("case (\"POST\", \"/dav1d-path\")", StringComparison.Ordinal)];
        Assert.Contains("query.Count != 0", get);
        Assert.DoesNotContain("TrySet", get);
        Assert.Contains("dav1d-path={DebugDav1dPathSwitch.Mode}\\n", get);
        var post = server[server.IndexOf("case (\"POST\", \"/dav1d-path\")", StringComparison.Ordinal)
            ..server.IndexOf("case (\"GET\", \"/av1-decoder\")", StringComparison.Ordinal)];
        Assert.Contains("query.Count != 1 || !DebugDav1dPathSwitch.TrySet(query.GetValueOrDefault(\"mode\"))", post);
        Assert.Contains("400 Bad Request", post);
        Assert.Contains("applies to the next playback attempt", post);
        Assert.Contains("///   GET  /dav1d-path", server);
        Assert.Contains("///   POST /dav1d-path?mode=zerocopy", server);
        Assert.Contains("GET /dav1d-path, POST /dav1d-path?mode={d3d11|zerocopy}", server);
        Assert.Contains("scripts/bale-debug.sh dav1d-path [d3d11|zerocopy]", Read("bale-debug.sh"));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
