using Bale.Core.Api.Models;
using Bale.Core.Detail;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using System.Text.Json;
using System.Xml.Linq;

namespace Bale.Core.Tests;

/// <summary>
/// rav1g is the default AV1 decoder in release builds too (owner, 2026-11-26): rav1g, the fork's in-open dav1d fallback,
/// one dav1d remount, then the ordinary replan; AV1 claimed up to 2160p60 at the 40 Mbps soft ceiling with a usable
/// rav1g.dll, dav1d's 1080p60 without. This project compiles with DEBUG_SERVER, so release builds are checked two ways:
/// the decisions themselves (Rust, through Core), and the Xbox sources as a BaleDebugServer=false build compiles them
/// (their DEBUG_SERVER regions removed).
/// </summary>
public sealed class Rav1gReleaseTests
{
    private static readonly HardwareDecodeLimits Series = new(H264: new(1920, 1080), HevcMain: new(7680, 4320),
        HevcMain10: new(7680, 4320), Vp9Profile0: new(3840, 2160), Vp9Profile2: new(3840, 2160), Vc1: new(1920, 1080),
        Mpeg2: new(1920, 1080));

    private static PlaybackPlan Plan(long height) => new()
    {
        PlanId = "plan", Delivery = "original_http",
        Source = new PlaybackSource
        {
            MediaFileId = "file", VideoCodec = "av1", AudioCodec = "opus", BitDepth = 10, Width = height * 16 / 9, Height = height,
        },
    };

    private static FileVersion Version(long height, long kbps, string rate = "60", string id = "file") => new()
    {
        FileId = id, CodecVideo = "av1", Container = "mkv",
        VideoTracks = [new() { Codec = "av1", Profile = "Main", BitDepth = 10, Width = height * 16 / 9, Height = height,
            FrameRate = rate, Bitrate = kbps, ColorPrimaries = "bt709", ColorTransfer = "bt709", ColorSpace = "bt709" }],
    };

    [Fact]
    public void SoftwareAv1TakesRav1gWithAUsableLibrary()
    {
        // As release builds route: the decoder is always rav1g, the library check decides.
        var plan = Plan(2160);
        var route = PlaybackRoutePolicy.Decide(plan, false, sdrOutput: true, versions: [Version(2160, 30_000)]);
        Assert.True(route.UseFfmpeg && route.SoftwareVideo);
        var rav1g = Rav1gPolicy.Route("rav1g", route, plan, [Version(2160, 30_000)], (1920, 1080), sdrDisplay: true,
            withdrawn: false, libraryUsable: true);
        Assert.True(rav1g.Rav1g);
        Assert.Equal(250, rav1g.BufferMs);
        Assert.Equal((1920, 1080), (rav1g.OutputWidth, rav1g.OutputHeight));
        // The release configuration of that mount (FfmpegSource's rav1g keys are outside DEBUG_SERVER, see below).
        var config = FfmpegSource.Configuration(route, new DebugFfmpegInputTuning(), rav1g);
        Assert.Equal("1", config.FFmpegOptions["bale_av1_decoder"]);
        Assert.Equal("1080", config.FFmpegOptions["bale_av1_fallback_max_height"]);
        Assert.Equal("1", config.FFmpegOptions["bale_rav1g_display"]);
        Assert.Equal("8", config.FFmpegOptions["bale_decode_ahead_frames"]);
        // Once a rav1g mount failed, the attempt's remount is dav1d's.
        Assert.False(Rav1gPolicy.Route("rav1g", route, plan, [Version(2160, 30_000)], (1920, 1080), true, withdrawn: true,
            libraryUsable: true).Rav1g);
        // The chain after a failure: one dav1d remount at 1080p and below, the replan above.
        Assert.True(Rav1gPolicy.RemountOnDav1d(true, true, false, false, false, false, 1080));
        Assert.False(Rav1gPolicy.RemountOnDav1d(true, true, false, false, false, false, route.VideoHeight));
    }

    [Fact]
    public void AMissingOrUnusableLibraryKeepsDav1dAt1080p60()
    {
        foreach (var status in new[]
        {
            Rav1gPolicy.Library(present: false, loaded: false, null, null),
            Rav1gPolicy.Library(true, loaded: false, null, null),
            Rav1gPolicy.Library(true, true, "rav1g_decoder_decode", Rav1gPolicy.LibraryAbiVersion),
            Rav1gPolicy.Library(true, true, null, Rav1gPolicy.LibraryAbiVersion + 1),
        })
        {
            Assert.False(status.Usable);
            var plan = Plan(1080);
            var route = PlaybackRoutePolicy.Decide(plan, false);
            var decision = Rav1gPolicy.Route("rav1g", route, plan, [Version(1080, 8_000)], (1920, 1080), true, false, status.Usable);
            Assert.False(decision.Rav1g);
            // dav1d exactly as before rav1g: no fork key, the software defaults (16 ahead at 1080p), CPU grain.
            var config = FfmpegSource.Configuration(route, new DebugFfmpegInputTuning(), decision);
            Assert.DoesNotContain("bale_av1_decoder", config.FFmpegOptions.Keys);
            Assert.DoesNotContain("bale_rav1g_display", config.FFmpegOptions.Keys);
            Assert.Equal("16", config.FFmpegOptions["bale_decode_ahead_frames"]);
            Assert.Equal("0", config.FFmpegOptions["bale_film_grain_mode"]);
            Assert.Equal(1000, SoftwareDecodeTuning.BufferTimeMs(true, decision.BufferMs, route.VideoWidth, route.VideoHeight));
            // The claim stays dav1d's 1080p60 (PlaybackClaims passes the library's verdict).
            var claims = ConsoleEvidence.ForHardware(Series, status.Usable);
            var av1 = Assert.Single(claims.VideoDecode, decode => decode.Codec == "av1");
            Assert.Equal(("dav1d", 1920, 1080, 60.0, (int?)null),
                (av1.DecoderName!, av1.MaxWidth ?? 0, av1.MaxHeight ?? 0, av1.MaxFrameRate ?? 0, av1.MaxBitrateKbps));
        }
    }

    [Fact]
    public void TheClaimReaches2160p60AtThe40MbpsSoftCeilingWithAUsableLibrary()
    {
        var claims = ConsoleEvidence.ForHardware(Series, rav1g: true).VideoDecode;
        Assert.Equal(new (string?, int, int, double, int?)[] { ("dav1d", 1920, 1080, 60.0, null), ("rav1g", 3840, 2160, 60.0, 40_000) },
            claims.Where(decode => decode.Codec == "av1").Select(decode =>
                (decode.DecoderName, decode.MaxWidth ?? 0, decode.MaxHeight ?? 0, decode.MaxFrameRate ?? 0, decode.MaxBitrateKbps)));
        // Auto's display matching reads the same bounds the server does (videoDecodeTakesSourceV3): 4K60 AV1 at or under
        // 40 Mbps is playable, faster is not; at 1080p dav1d's unbounded entry keeps admitting any bitrate.
        var versions = new[] { Version(2160, 50_000), Version(2160, 40_000, id: "40"), Version(1080, 70_000, id: "hd") };
        Assert.Equal("40", PlaybackFormatting.SelectVersion(versions, new DisplayVersionMatch(2160, claims))?.FileId);
        var dav1d = ConsoleEvidence.ForHardware(Series).VideoDecode;
        Assert.Equal("hd", PlaybackFormatting.SelectVersion(versions, new DisplayVersionMatch(2160, dav1d))?.FileId);
        // The start request carries the ceiling as max_bitrate_kbps.
        var start = PlaybackStartBuilder.Build("file", "profile", "attempt", null, PlaybackSelection.Auto,
            ConsoleEvidence.ForHardware(Series, rav1g: true), PlaybackTestSupport.Identity);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(start, PlaybackJson.Default.PlaybackStartRequestV3));
        var rav1g = json.RootElement.GetProperty("client_capabilities").GetProperty("video_decode").EnumerateArray()
            .Single(decode => decode.GetProperty("decoder_name").GetString() == "rav1g");
        Assert.Equal(40_000, rav1g.GetProperty("max_bitrate_kbps").GetInt32());
        Assert.Equal(60, rav1g.GetProperty("max_frame_rate").GetDouble());
        Assert.False(rav1g.GetProperty("hardware").GetBoolean());
        // Consoles without measured software video (Xbox One, no AVX2) claim no software AV1 at all, rav1g's included.
        Assert.DoesNotContain(SoftwareDecodePolicy.ForSoftwareVideo(ConsoleEvidence.ForHardware(Series, rav1g: true), false).VideoDecode,
            decode => decode.Codec == "av1");
    }

    [Fact]
    public void ReleaseClaimsFollowTheLibraryCheckAndPromiseRav1gsHdrAv1ToneMap()
    {
        var claims = Release("PlaybackClaims.cs.txt");
        Assert.Contains("internal static async Task<(DeviceCapabilities Capabilities, bool SoftwareVideo, bool Rav1g)> DetectAsync(bool rav1g = true)", claims);
        Assert.Contains("rav1g = rav1g && softwareVideo && (await Rav1gLibrary.Status).Usable;", claims);
        Assert.Contains("DisplayHdrOutput.DetectCapabilities(ConsoleEvidence.ForHardware(hardware, rav1g))", claims);
        Assert.Contains("Av1DynamicRangeMappingSupported = rav1g,", claims);
        // Auto's display matching counts what the player's claims admit, rav1g's entry included.
        Assert.Contains("SetVersionMatch(probe.Result, rav1g.Result.Usable);", claims);
        Assert.Contains("var claims = ConsoleEvidence.ForHardware(hardware, rav1g && softwareVideo);", claims);
        var player = Release("PlayerPage.cs.txt");
        Assert.Contains("var (capabilities, softwareVideo, rav1g) = await PlaybackClaims.DetectAsync();", player);
        Assert.Contains("public string Av1Decoder => \"rav1g\";", player);
        Assert.Contains("var capabilities = await DetectClaimsAsync(attempt);", player);
        // The library check runs in every build, never under DEBUG_SERVER, and frees what it loaded.
        var library = Read("Rav1gLibrary.cs.txt");
        Assert.DoesNotContain("#if", library);
        Assert.Contains("NativeLibrary.TryLoad(\"rav1g.dll\", out module)", library);
        Assert.Contains("Rav1gPolicy.LibraryExports.FirstOrDefault(name => !NativeLibrary.TryGetExport(module, name, out _))", library);
        Assert.Contains("finally\n        {\n            if (module != 0) NativeLibrary.Free(module);", library);
        Assert.Contains("private static readonly Lazy<Task<Rav1gLibraryStatus>> StatusValue = new(() => Task.Run(Check));", library);
    }

    [Fact]
    public void ReleaseMountsTakeTheRav1gRouteItsGuardBufferAndOneDav1dRemount()
    {
        var media = Release("PlayerPage.Media.cs.txt");
        Assert.Contains("var rav1g = media.Rav1g = Rav1gPolicy.Route(attempt.Av1Decoder, media.Route, attempt.Plan!,\n"
            + "                    attempt.Request.Detail.Versions, DisplayHdrOutput.CurrentRawSize(), sdrDisplay, attempt.Rav1gRemounted,\n"
            + "                    Rav1gLibrary.Usable);", media);
        Assert.Contains("var config = FfmpegSource.Configuration(media.Route, media.Rav1g, attempt.Dav1dGpuFilmGrain, attempt.SdrWhiteNits);", media);
        Assert.Contains("FfmpegSource.ValidateVideoDecoders(media.FfmpegSource, media.Route, rav1gRoute);", media);
        Assert.Contains("media.Rav1gActive = media.FfmpegStreams?.Value?.Video?.DecoderEngine == DecoderEngine.FFmpegD3D11HardwareDecoder;", media);
        Assert.Contains("media.Rav1gCheck = new Rav1gOutputCheck(rav1g.Source, sdrDisplay, !ConsoleToneMapRuntime.SupportsP010,", media);
        Assert.Contains("int? bufferOverrideMs = rav1gRoute ? rav1g.BufferMs : null;", media);
        Assert.DoesNotContain("DebugRav1gLog", media);
        Assert.DoesNotContain("DebugRav1gTuning", media);
        var player = Release("PlayerPage.cs.txt");
        Assert.Contains("public bool Rav1gRemounted { get; set; }", player);
        Assert.Contains("Rav1gPolicy.RemountOnDav1d(failedMount.Rav1gRoute, failedMount.Rav1gActive,", player);
        Assert.Contains("await RemountSamePlanAsync(attempt, position, \"player: rav1g failed; remounting the same plan on dav1d\");", player);
        // The remount comes before the single replan, after the console-downscale one.
        Assert.True(player.IndexOf("Rav1gPolicy.RemountOnDav1d(", StringComparison.Ordinal)
            > player.IndexOf("attempt.SdrDownscaleDeclined = true;", StringComparison.Ordinal));
        Assert.True(player.IndexOf("Rav1gPolicy.RemountOnDav1d(", StringComparison.Ordinal)
            < player.IndexOf("attempt.Replanned = true;", StringComparison.Ordinal));
        var guard = Release("FfmpegVideoOutputGuard.cs.txt");
        Assert.Contains("internal sealed class Rav1gOutputCheck(", guard);
        Assert.Contains("if (!_failure && _rav1g.Accept(video.EncodingProperties, current?.DecoderEngine)) return;", guard);
        // The HUD names rav1g's D3D11 engine.
        Assert.Contains("VideoDecoderName = FfmpegSource.DecoderName(video.DecoderEngine, media.Rav1gRoute),", Read("PlayerPage.HudStats.cs.txt"));
        Assert.Equal("rav1g", FfmpegSource.DecoderName(FFmpegInteropX.DecoderEngine.FFmpegD3D11HardwareDecoder, rav1g: true));
        Assert.Equal("FFmpeg software", FfmpegSource.DecoderName(FFmpegInteropX.DecoderEngine.FFmpegSoftwareDecoder, rav1g: true));
    }

    [Fact]
    public void ReleaseConfigurationAppliesTheRav1gKeysWithoutTheDebugKnobs()
    {
        var source = DebugFfmpegDecoderTests.ProductionSource();
        Assert.Contains("internal static MediaSourceConfig Configuration(PlaybackRouteDecision route, Rav1gRouteDecision? rav1g = null,", source);
        Assert.Contains("var rav1gStage = rav1g is { Rav1g: true } && route.SoftwareVideo && route.VideoCodec == \"av1\";", source);
        Assert.Contains("if (rav1g is { Rav1g: true } && softwareVideo && route.VideoCodec == \"av1\")", source);
        Assert.Contains("ApplyRav1g(config, rav1g, dav1dGpuFilmGrain);", source);
        var apply = source[source.IndexOf("internal static void ApplyRav1g(", StringComparison.Ordinal)..];
        apply = apply[..apply.IndexOf("\n    }\n", StringComparison.Ordinal)];
        foreach (var key in new[] { "bale_av1_decoder", "bale_av1_fallback_max_height", "bale_decode_ahead_frames",
            "bale_gpu_output_textures", "bale_tonemap", "bale_tonemap_output_width", "bale_rav1g_display" })
            Assert.Contains($"config.FFmpegOptions[\"{key}\"]", apply);
        Assert.Contains("ApplySoftwareVideo(config, dav1dGpuFilmGrain);", apply);
        // The provider knobs, perf logs and zero-copy path stay debug-only.
        Assert.DoesNotContain("DebugRav1gTuning", source);
        Assert.DoesNotContain("ApplyDav1dPath", source);
        Assert.DoesNotContain("bale_rav1g_perf", source);
        Assert.Contains("internal static void ValidateVideoDecoders(FFmpegMediaSource source, PlaybackRouteDecision route, bool rav1g = false)", source);
    }

    [Fact]
    public void TheDebugSwitchesStayDebugOnlyWithTheReleaseDefaults()
    {
        foreach (var file in new[] { "DebugAv1DecoderSwitch.cs.txt", "DebugAv1ClaimSwitch.cs.txt", "Rav1gPrewarm.cs.txt" })
        {
            var text = Read(file);
            Assert.StartsWith("#if DEBUG_SERVER", text);
            Assert.Equal("", Release(file).Trim());
        }
        Assert.Contains("private static string _mode = \"rav1g\";", Read("DebugAv1DecoderSwitch.cs.txt"));
        Assert.Contains("private static int _maxHeight = 2160;", Read("DebugAv1ClaimSwitch.cs.txt"));
        Assert.Equal("rav1g", DebugAv1DecoderSwitch.Mode);
        Assert.Equal(2160, DebugAv1ClaimSwitch.MaxHeight);
        // Neither the switches nor the prewarm reach the release player.
        foreach (var file in new[] { "PlayerPage.cs.txt", "PlayerPage.Media.cs.txt", "PlaybackClaims.cs.txt" })
        {
            var release = Release(file);
            Assert.DoesNotContain("DebugAv1DecoderSwitch", release);
            Assert.DoesNotContain("DebugAv1ClaimSwitch", release);
            Assert.DoesNotContain("Rav1gPrewarm", release);
        }
    }

    [Fact]
    public void EveryBuildPackagesThePinnedRav1gAndCiChecksEachPackage()
    {
        var project = XDocument.Parse(Read("Bale.Xbox.csproj.txt"));
        var rav1g = project.Descendants("Content").Where(content => content.Attribute("Include")!.Value.Contains(@".cache\rav1g\")).ToArray();
        Assert.Equal(2, rav1g.Length);
        Assert.All(rav1g, content =>
        {
            var condition = content.Parent!.Attribute("Condition")!.Value;
            Assert.Equal(@"Exists('$(MSBuildThisFileDirectory)..\..\.cache\rav1g\rav1g.dll')", condition);
            Assert.DoesNotContain("BaleDebugServer", condition);
        });
        Assert.Contains(rav1g, content => content.Attribute("Link")!.Value == "rav1g.dll");
        Assert.Contains(rav1g, content => content.Attribute("Link")!.Value == @"Licenses\rav1g\%(RecursiveDir)%(Filename)%(Extension)");
        // CI restores the pinned, hash-checked release for every matrix entry, production included, and checks each package.
        var ci = Read("ci.yml.txt");
        var restore = ci[ci.IndexOf("- name: Download and verify pinned rav1g.dll", StringComparison.Ordinal)..];
        restore = restore[..restore.IndexOf("- name:", 10, StringComparison.Ordinal)];
        Assert.Contains("run: ./scripts/restore-rav1g.ps1", restore);
        Assert.DoesNotContain("if:", restore);
        var check = ci[ci.IndexOf("- name: Check packaged rav1g.dll", StringComparison.Ordinal)..];
        check = check[..check.IndexOf("- name:", 10, StringComparison.Ordinal)];
        Assert.DoesNotContain("if:", check);
        Assert.Contains("./scripts/check-rav1g-package.ps1 -PackagePath $package.FullName -DebugServer '${{ matrix.debug_server }}' -Dumpbin $dumpbin.FullName", check);
        Assert.Contains("debug_server: ${{ fromJSON(inputs.production && '[\"true\", \"false\"]' || '[\"true\"]') }}", ci);
        var script = Read("check-rav1g-package.ps1.txt");
        Assert.DoesNotContain("$DebugServer -eq 'false') {", script);
        Assert.Contains("if (-not $pin) {", script);
        Assert.Contains("if ($actual -cne $pin.dll_sha256)", script);
        Assert.Contains("foreach ($debugServer in @('true', 'false'))", Read("test-rav1g-checks.ps1.txt"));
    }

    [Fact]
    public void TheLibraryCheckMatchesThePinAndTheForksExportList()
    {
        using var pin = JsonDocument.Parse(Read("rav1g.json"));
        Assert.Equal(Rav1gPolicy.LibraryAbiVersion, pin.RootElement.GetProperty("abi_version").GetUInt32());
        var common = Read("rav1g-common.ps1.txt");
        Assert.Contains($"$Rav1gWrapperAbiVersion = {Rav1gPolicy.LibraryAbiVersion}", common);
        var list = common[common.IndexOf("$Rav1gExports = @(", StringComparison.Ordinal)..];
        list = list[..list.IndexOf(')')];
        var exports = System.Text.RegularExpressions.Regex.Matches(list, "'([a-z0-9_]+)'").Select(match => match.Groups[1].Value);
        Assert.Equal(exports, Rav1gPolicy.LibraryExports);
    }

    /// <summary>The source as a release (BaleDebugServer=false) build compiles it: every DEBUG_SERVER region removed.</summary>
    internal static string Release(string file)
    {
        var debug = false;
        return string.Join('\n', Read(file).Split('\n').Where(line =>
        {
            if (line == "#if DEBUG_SERVER") { debug = true; return false; }
            if (line == "#else") { debug = !debug; return false; }
            if (line == "#endif") { debug = false; return false; }
            return !debug;
        }));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
