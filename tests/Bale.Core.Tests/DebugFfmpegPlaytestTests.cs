using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public class DebugFfmpegPlaytestTests
{
    [Theory]
    [InlineData(null, null, 3, "auto")]
    [InlineData("1", "auto", 1, "auto")]
    [InlineData("30", "software", 30, "software")]
    [InlineData("60", "system", 60, "system")]
    public void ParsesBoundedDurationAndExplicitModes(string? seconds, string? mode, int expectedSeconds, string expectedMode)
    {
        Assert.True(DebugFfmpegPlaytest.TryParse("https://example.org/clip", seconds, mode,
            out _, out var duration, out var decoderMode, out var error));
        Assert.Equal(expectedSeconds, duration);
        Assert.Equal(expectedMode, decoderMode);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("0", "auto")]
    [InlineData("61", "auto")]
    [InlineData("-1", "auto")]
    [InlineData("1.5", "auto")]
    [InlineData(" 3", "auto")]
    [InlineData("3", "hardware")]
    [InlineData("3", "Software")]
    [InlineData("3", "")]
    public void RejectsInvalidInputs(string seconds, string mode) => Assert.False(
        DebugFfmpegPlaytest.TryParse("https://example.org/clip", seconds, mode, out _, out _, out _, out _));

    [Theory]
    [InlineData("file:///clip")]
    [InlineData("clip.mp4")]
    [InlineData("https://user:password@example.org/clip")]
    public void SharesTheBuiltInProbesUrlValidation(string url) => Assert.False(
        DebugFfmpegPlaytest.TryParse(url, "3", "auto", out _, out _, out _, out _));

    [Theory]
    [InlineData(null, null, null, null, null, 1.0)]
    [InlineData("0", "on", "0.5", 0, true, 0.5)]
    [InlineData("16", "off", "2.0", 16, false, 2.0)]
    [InlineData("4", null, "1.25", 4, null, 1.25)]
    [InlineData(null, "off", null, null, false, 1.0)]
    [InlineData(null, null, "0.75", null, null, 0.75)]
    public void ParsesOptionalTuningWithoutOverridingOmittedNativeSettings(string? threads, string? output10bit, string? rate,
        int? expectedThreads, bool? expected10bit, double expectedRate)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(DebugFfmpegPlaytest.TryParseTuning(threads, output10bit, rate, null, null, null, null, null, null, out var tuning, out var error));
            Assert.Equal(expectedThreads is { } count ? (uint?)count : null, tuning.Threads);
            Assert.Equal(expected10bit, tuning.Allow10bit);
            Assert.Equal(expectedRate, tuning.Rate);
            Assert.Empty(error);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("-1", null, null, "threads")]
    [InlineData("17", null, null, "threads")]
    [InlineData("4294967296", null, null, "threads")]
    [InlineData("1.5", null, null, "threads")]
    [InlineData(" 4", null, null, "threads")]
    [InlineData("", null, null, "threads")]
    [InlineData(null, "true", null, "10bit")]
    [InlineData(null, "OFF", null, "10bit")]
    [InlineData(null, "", null, "10bit")]
    [InlineData(null, null, "0.49", "rate")]
    [InlineData(null, null, "2.01", "rate")]
    [InlineData(null, null, "NaN", "rate")]
    [InlineData(null, null, "Infinity", "rate")]
    [InlineData(null, null, "1,5", "rate")]
    [InlineData(null, null, "1e0", "rate")]
    [InlineData(null, null, " 1", "rate")]
    [InlineData(null, null, "", "rate")]
    public void RejectsInvalidTuningWithFixedNonReflectingErrors(string? threads, string? output10bit, string? rate, string field)
    {
        Assert.False(DebugFfmpegPlaytest.TryParseTuning(threads, output10bit, rate, null, null, null, null, null, null, out var tuning, out var error));
        Assert.StartsWith(field + " must be", error);
        Assert.Equal(new DebugFfmpegTuning(), tuning);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("0", "1", "0", "0")]
    [InlineData("64", "16", "64", "2")]
    [InlineData("0008", "04", "001", "001")]
    public void ProbeForkOptionsAreOptionalBoundedInvariantDecimals(string? ahead, string? sws, string? delay, string? prio)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(DebugFfmpegPlaytest.TryParseTuning("4", "off", "0.75", ahead, sws, delay, prio, null, null, out var tuning, out var error));
            Assert.Equal(new DebugFfmpegTuning(4, false, 0.75,
                ahead is null ? null : int.Parse(ahead, CultureInfo.InvariantCulture),
                sws is null ? null : int.Parse(sws, CultureInfo.InvariantCulture),
                delay is null ? null : int.Parse(delay, CultureInfo.InvariantCulture),
                prio is null ? null : int.Parse(prio, CultureInfo.InvariantCulture)), tuning);
            Assert.Empty(error);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("ahead", 0, 64)]
    [InlineData("sws", 1, 16)]
    [InlineData("delay", 0, 64)]
    [InlineData("prio", 0, 2)]
    public void ProbeRejectsInvalidForkOptionsWithoutPublishingPartialTuning(string key, int minimum, int maximum)
    {
        foreach (var value in new[] { "-1", "+1", " 1", "1 ", "1.0", "1,0", "1e0", "0x1", "", "default", "2147483648",
            (minimum - 1).ToString(CultureInfo.InvariantCulture), (maximum + 1).ToString(CultureInfo.InvariantCulture) })
        {
            Assert.False(DebugFfmpegPlaytest.TryParseTuning("4", "off", "0.75", key == "ahead" ? value : "8",
                key == "sws" ? value : "4", key == "delay" ? value : "1", key == "prio" ? value : "2", null, null, out var tuning, out var error));
            Assert.StartsWith(key + " must be an integer from ", error);
            Assert.Equal(new DebugFfmpegTuning(), tuning);
        }
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("default", "default", null, null)]
    [InlineData("0", "0", 0, 0)]
    [InlineData("1", "3", 1, 3)]
    [InlineData("1", "16", 1, 16)]
    [InlineData("001", "003", 1, 3)]
    [InlineData("default", "3", null, 3)]
    [InlineData("1", "default", 1, null)]
    public void ProbeDecoderHintsAreOptInBoundedAndDoNotChangeOtherDefaults(string? maxres, string? dpb, int? expectedMaxres, int? expectedDpb)
    {
        Assert.True(DebugFfmpegPlaytest.TryParseTuning(null, null, null, null, null, null, null, maxres, dpb, out var tuning, out var error));
        Assert.Empty(error);
        Assert.Equal(new DebugFfmpegTuning(DecoderUseMaxResolution: expectedMaxres, DecoderMaxDpbCount: expectedDpb), tuning);
        Assert.Equal(SoftwareDecodeTuning.ForVideo(true), tuning.DecodeTuning("software"));
        Assert.Equal(SoftwareDecodeTuning.ForVideo(false), tuning.DecodeTuning("system"));
        var report = DebugFfmpegPlaytest.Settings(0, true, 8, tuning, "system");
        Assert.Contains($"Decoder use max resolution: {expectedMaxres ?? 0} (0=unset, 1=on; {(expectedMaxres is null ? "default" : "override")}; system video only)\n", report);
        Assert.Contains($"Decoder max DPB count: {expectedDpb ?? 0} (0=unset; {(expectedDpb is null ? "default" : "override")}; system video only)\n", report);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("default", null)]
    [InlineData("on", true)]
    [InlineData("off", false)]
    public void HdrProbeOverrideIsOptIn(string? hdr, bool? expected)
    {
        Assert.True(DebugFfmpegPlaytest.TryParseHdr(hdr, out var enabled, out var error));
        Assert.Equal(expected, enabled);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ON")]
    [InlineData("1")]
    [InlineData("pretend")]
    public void InvalidHdrProbeOverrideIsRejected(string hdr)
    {
        Assert.False(DebugFfmpegPlaytest.TryParseHdr(hdr, out _, out var error));
        Assert.Equal("hdr must be on, off or default", error);
    }

    [Fact]
    public void HdrProbeOverrideReachesWrapperConfigOnlyWhenSet()
    {
        var source = Read("DebugFfmpegSource.cs.txt");
        Assert.Contains("if (tuning.HdrSupport is { } hdr) config.Video.HdrSupport = hdr ? HdrSupport.Enabled : HdrSupport.Disabled;", source);
        Assert.Contains("query.GetValueOrDefault(\"hdr\")", Read("DebugServer.cs.txt"));
        Assert.Contains("tuning = tuning with { HdrSupport = hdrSupport, FilmGrain = filmGrain };", Read("DebugServer.Ffmpeg.cs.txt"));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("default", null)]
    [InlineData("on", "Dav1d")]
    [InlineData("dav1d", "Dav1d")]
    [InlineData("cpu", "Cpu")]
    [InlineData("gpu", "Gpu")]
    [InlineData("off", "Off")]
    public void GrainProbeOverrideIsOptIn(string? grain, string? expected)
    {
        Assert.True(DebugFfmpegPlaytest.TryParseGrain(grain, out var selection, out var error));
        Assert.Equal(expected, selection?.ToString());
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("OFF")]
    [InlineData("0")]
    [InlineData("export")]
    public void InvalidGrainProbeOverrideIsRejected(string grain)
    {
        Assert.False(DebugFfmpegPlaytest.TryParseGrain(grain, out _, out var error));
        Assert.Equal("grain must be dav1d, cpu, gpu, off, on or default", error);
    }

    [Fact]
    public void GrainExportKeyIsSentOnlyForGrainOff()
    {
        var source = Read("DebugFfmpegSource.cs.txt");
        Assert.Contains("if (DebugFfmpegPlaytest.GrainOption(tuning.FilmGrain) is { } grain) config.FFmpegOptions[grain.Key] = grain.Value;", source);
        Assert.Contains("query.GetValueOrDefault(\"grain\")", Read("DebugServer.cs.txt"));
        // The real player sets it only from the debug ffmpeg-tune override (DebugFfmpegInputTuningTests).
        var player = Read("FfmpegSource.cs.txt");
        Assert.DoesNotContain("bale_dav1d_export_film_grain", player[..player.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal)]);
    }

    [Theory]
    [InlineData(null, null, null, "dav1d (probe default; CPU buffers; production default is GPU)")]
    [InlineData("default", null, null, "dav1d (probe default; CPU buffers; production default is GPU)")]
    [InlineData("on", "bale_film_grain_mode", "0", "dav1d (override)")]
    [InlineData("dav1d", "bale_film_grain_mode", "0", "dav1d (override)")]
    [InlineData("cpu", "bale_film_grain_mode", "1", "Rust CPU (override)")]
    [InlineData("gpu", "bale_film_grain_mode", "2", "GPU (override; CPU fallback through 1080p, ungrained above 1080p)")]
    [InlineData("off", "bale_dav1d_export_film_grain", "1", "exported, not applied (override; libdav1d only)")]
    public void GrainOptionEmissionAndReportPreserveExportOnlyAndDav1dDefaults(string? input, string? key, string? value, string description)
    {
        Assert.True(DebugFfmpegPlaytest.TryParseGrain(input, out var grain, out _));
        var option = DebugFfmpegPlaytest.GrainOption(grain);
        Assert.Equal(key, option?.Key);
        Assert.Equal(value, option?.Value);
        var report = DebugFfmpegPlaytest.Settings(0, true, 8, new(FilmGrain: grain), "software");
        Assert.Contains("Film grain requested: " + description + "\n", report);
        Assert.Contains(input == "gpu" ? "1 (GPU grain; render target+shader resource)" : "0 (probe default; CPU buffers)", report);
    }

    [Theory]
    [InlineData("maxres", 1)]
    [InlineData("dpb", 16)]
    public void ProbeRejectsInvalidDecoderHintsWithoutPublishingPartialTuning(string key, int maximum)
    {
        foreach (var value in new[] { "-1", "+1", " 1", "1 ", "1.0", "1,0", "1e0", "0x1", "", "DEFAULT", "2147483648",
            (maximum + 1).ToString(CultureInfo.InvariantCulture) })
        {
            Assert.False(DebugFfmpegPlaytest.TryParseTuning("4", "off", "0.75", "8", "4", "1", "2",
                key == "maxres" ? value : "1", key == "dpb" ? value : "3", out var tuning, out var error));
            Assert.StartsWith(key + " must be an integer from ", error);
            Assert.Equal(new DebugFfmpegTuning(), tuning);
        }
    }

    [Fact]
    public void ProbeAppliesSharedSoftwareDefaultsAndReportsTheirOverrides()
    {
        var source = Read("DebugFfmpegSource.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("FfmpegSource.ApplyDecodeTuning(config, tuning.DecodeTuning(mode))", source);
        Assert.Contains("if (tuning.FilmGrain is DebugFilmGrain.Gpu) config.FFmpegOptions[\"bale_d3d11_upload\"] = \"1\";", source);
        Assert.Single(source.Split("bale_d3d11_upload").Skip(1));
        Assert.DoesNotContain("SoftwareDecodeTuning.FilmGrainMode", source);
        Assert.DoesNotContain("DefaultFilmGrainMode", source);
        Assert.Null(DebugFfmpegPlaytest.GrainOption(new DebugFfmpegTuning().FilmGrain));
        Assert.Contains("if (tuning.DecodeThreadPriority is { } priority) config.FFmpegOptions[\"bale_decode_thread_priority\"] = priority.ToString(CultureInfo.InvariantCulture)", source);
        Assert.Contains("if (tuning.DecoderUseMaxResolution is { } maxres) config.FFmpegOptions[\"bale_decoder_use_max_resolution\"] = maxres.ToString(CultureInfo.InvariantCulture)", source);
        Assert.Contains("if (tuning.DecoderMaxDpbCount is { } dpb) config.FFmpegOptions[\"bale_decoder_max_dpb_count\"] = dpb.ToString(CultureInfo.InvariantCulture)", source);
        var report = DebugFfmpegPlaytest.Settings(4, false, 8, new(4, false, 0.75, 8, 4, 1, 2), "software");
        Assert.Contains("Decode ahead: 8 (0=off; override)\n", report);
        Assert.Contains("SWS threads: 4 (1=upstream; override)\n", report);
        Assert.Contains("dav1d max frame delay: 1 (0=dav1d default; override)\n", report);
        Assert.Contains("Decode thread priority: 2 (0=off, 1=above normal, 2=highest; override)\n", report);
        var server = Read("DebugServer.cs.txt");
        foreach (var key in new[] { "ahead", "sws", "delay", "prio", "maxres", "dpb" }) Assert.Contains($"query.GetValueOrDefault(\"{key}\")", server);
        var duplicateGuard = server.Split('\n').Single(line => line.Contains("duplicateQueryKeys && path is", StringComparison.Ordinal));
        Assert.Contains("\"/playtest-ffmpeg\"", duplicateGuard);
        Assert.Contains("\"/ffmpeg-tune\"", duplicateGuard);
        Assert.Contains("if (values.ContainsKey(key)) duplicateKeys = true", server);
        Assert.Contains("TryParseTuning(threads, output10bit, rate, ahead, sws, delay, prio, maxres, dpb", Read("DebugServer.Ffmpeg.cs.txt"));
        Assert.DoesNotContain("DebugFfmpegInputTuning.Current", source);
    }

    [Theory]
    [InlineData("software", 16, 4)]
    [InlineData("auto", null, null)]
    [InlineData("system", null, null)]
    public void ProbeUsesReleaseSoftwareDefaultsWithUnknownDimensions(string mode, int? ahead, int? sws)
    {
        var tuning = new DebugFfmpegTuning();
        Assert.Equal(new SoftwareDecodeTuning(ahead, sws), tuning.DecodeTuning(mode));
        var report = DebugFfmpegPlaytest.Settings(0, true, 8, tuning, mode);
        Assert.Contains($"Decode ahead: {ahead ?? 0} (0=off; default)\n", report);
        Assert.Contains($"SWS threads: {sws ?? 1} (1=upstream; default)\n", report);
        Assert.Contains("dav1d max frame delay: 0 (0=dav1d default; default)\n", report);
        Assert.Null(tuning.DecodeThreadPriority);
        Assert.Contains("Decode thread priority: 0 (0=off, 1=above normal, 2=highest; default)\n", report);
        Assert.Null(tuning.DecoderUseMaxResolution);
        Assert.Null(tuning.DecoderMaxDpbCount);
        Assert.Contains("Decoder use max resolution: 0 (0=unset, 1=on; default; system video only)\n", report);
        Assert.Contains("Decoder max DPB count: 0 (0=unset; default; system video only)\n", report);
    }

    [Theory]
    [InlineData(0, 1, 0, 0)]
    [InlineData(8, 2, 1, 1)]
    [InlineData(64, 16, 64, 2)]
    public void ExplicitProbeValuesWinOverReleaseDefaultsIncludingZero(int ahead, int sws, int delay, int prio)
    {
        var tuning = new DebugFfmpegTuning(DecodeAheadFrames: ahead, SwsThreads: sws, Dav1dMaxFrameDelay: delay, DecodeThreadPriority: prio);
        Assert.Equal(new SoftwareDecodeTuning(ahead, sws, delay), tuning.DecodeTuning("software"));
        Assert.Contains($"Decode thread priority: {prio} (0=off, 1=above normal, 2=highest; override)\n",
            DebugFfmpegPlaytest.Settings(0, true, 8, tuning, "software"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void PriorityOnlyProbeOverridePreservesOtherReleaseDefaults(string prio)
    {
        Assert.True(DebugFfmpegPlaytest.TryParseTuning(null, null, null, null, null, null, prio, null, null, out var tuning, out var error));
        Assert.Empty(error);
        Assert.Equal(int.Parse(prio, CultureInfo.InvariantCulture), tuning.DecodeThreadPriority);
        Assert.Equal(SoftwareDecodeTuning.ForVideo(true), tuning.DecodeTuning("software"));
        Assert.Equal(SoftwareDecodeTuning.ForVideo(false), tuning.DecodeTuning("system"));
        Assert.Null(tuning.Threads);
        Assert.Null(tuning.Allow10bit);
        Assert.Equal(1, tuning.Rate);
    }

    [Theory]
    [InlineData(0, null, null, 0, 4, null)]
    [InlineData(null, 1, null, 16, 1, null)]
    [InlineData(null, null, 8, 16, 4, 8)]
    public void ProbeOverridesLeaveUnspecifiedReleaseDefaultsIntact(int? ahead, int? sws, int? delay,
        int expectedAhead, int expectedSws, int? expectedDelay)
    {
        var tuning = new DebugFfmpegTuning(DecodeAheadFrames: ahead, SwsThreads: sws, Dav1dMaxFrameDelay: delay);
        Assert.Equal(new SoftwareDecodeTuning(expectedAhead, expectedSws, expectedDelay), tuning.DecodeTuning("software"));
    }

    [Theory]
    [InlineData(0.5, 5, 1, "PLAYS - target rate")]
    [InlineData(0.5, 4, 0.8, "SLOW - below target rate")]
    [InlineData(1.5, 15, 1, "PLAYS - target rate")]
    [InlineData(2.0, 10, 0.5, "SLOW - below target rate")]
    [InlineData(2.0, 20, 1, "PLAYS - target rate")]
    [InlineData(2.0, 24, 1.2, "PLAYS - clock faster than target rate")]
    public void RatioAndVerdictUseWallTimeTimesTheAppliedRate(double rate, double advance, double expectedRatio, string verdict)
    {
        var start = TimeSpan.FromSeconds(20);
        var end = start + TimeSpan.FromSeconds(advance);
        var wall = TimeSpan.FromSeconds(10);
        Assert.Equal(expectedRatio, DebugFfmpegPlaytest.Ratio(start, end, wall, rate), precision: 6);
        Assert.StartsWith("Verdict: " + verdict, DebugFfmpegPlaytest.Verdict(true, start, end, wall, [], rate));
        var text = DebugFfmpegPlaytest.Measurements(start, end, wall, 0, null, null, end, "Playing", rate);
        Assert.Contains("expected=" + (10 * rate).ToString("0.###", CultureInfo.InvariantCulture) + "s", text);
        Assert.Contains("ratio=" + expectedRatio.ToString("0.###", CultureInfo.InvariantCulture), text);
    }

    [Fact]
    public void SettingsAndFrameSummaryAreInvariantAndDistinguishUnavailableFromZero()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            const string defaults = "Decode ahead: 0 (0=off; default)\nSWS threads: 1 (1=upstream; default)\ndav1d max frame delay: 0 (0=dav1d default; default)\nDecode thread priority: 0 (0=off, 1=above normal, 2=highest; default)\n"
                + "Decoder use max resolution: 0 (0=unset, 1=on; default; system video only)\nDecoder max DPB count: 0 (0=unset; default; system video only)\n"
                + "HDR support: automatic (default)\n"
                + "Film grain requested: dav1d (probe default; CPU buffers; production default is GPU)\n"
                + "D3D11 upload requested: 0 (probe default; CPU buffers)\n"
                + "Effective grain mode/fallback: see Bale film grain native diagnostics\n";
            Assert.Equal("Threads: 0 (0=logical CPU count; default)\n10-bit output: on (default)\nLogical CPUs: 8\nRate requested: 1\n" + defaults,
                DebugFfmpegPlaytest.Settings(0, true, 8, new(), "auto"));
            Assert.Equal("Threads: 4 (0=logical CPU count; override)\n10-bit output: off (override)\nLogical CPUs: 8\nRate requested: 0.75\n" + defaults,
                DebugFfmpegPlaytest.Settings(4, false, 8, new(4, false, 0.75), "auto"));
            Assert.Contains("Late frames total: unavailable", DebugFfmpegPlaytest.FrameDelivery(null));
            Assert.Contains("Total frames delivered: unavailable", DebugFfmpegPlaytest.FrameDelivery(null));
            Assert.Contains("min=unavailable median=unavailable", DebugFfmpegPlaytest.FrameDelivery(new(0, 0, 0, 1)));
            var summary = DebugFfmpegPlaytest.FrameDelivery(new PlaybackFrameDeliverySnapshot(0, 0, 0.5, 100, 0, 0.125));
            Assert.Contains("Late frames total: 0\nTotal frames delivered: 100\n", summary);
            Assert.Contains("min=0s median=0.125s", summary);
            Assert.Contains("not renderer queue depth", summary);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(0, "FAILS")]
    [InlineData(5, "SLOW")]
    [InlineData(9, "PLAYS - real time")]
    [InlineData(10, "PLAYS - real time")]
    [InlineData(11, "PLAYS - real time")]
    [InlineData(12, "PLAYS - clock faster")]
    public void VerdictMeasuresAdvancementAgainstWallTime(double advance, string verdict)
    {
        var start = TimeSpan.FromSeconds(20);
        Assert.StartsWith("Verdict: " + verdict, DebugFfmpegPlaytest.Verdict(true, start,
            start + TimeSpan.FromSeconds(advance), TimeSpan.FromSeconds(10), []));
    }

    [Fact]
    public void AnOpenOrMediaFailureOverridesAMovingClock()
    {
        var failure = DebugFfmpegPlaytest.Failure("MediaFailed", unchecked((int)0xC00D5212));
        Assert.Equal("MediaFailed: 0xC00D5212", failure);
        Assert.StartsWith("Verdict: FAILS", DebugFfmpegPlaytest.Verdict(false, TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), []));
        Assert.Equal("Verdict: FAILS - MediaFailed: 0xC00D5212\n", DebugFfmpegPlaytest.Verdict(true,
            TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), [failure]));
    }

    [Fact]
    public void MeasurementsUseCpuTimeDeltaInCoresAndInvariantDecimals()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("PlaybackState: Playing\nPosition: start=2s end=11.5s max=11.5s wall=10s expected=10s ratio=0.95\nBuffering entries: 3\nCPU: 2.500 cores\n",
                DebugFfmpegPlaytest.Measurements(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(11.5),
                    TimeSpan.FromSeconds(10), 3, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(125),
                    TimeSpan.FromSeconds(11.5), "Playing"));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void MissingCpuAndZeroWallTimeStayFinite()
    {
        Assert.Equal(0, DebugFfmpegPlaytest.Ratio(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero));
        Assert.Contains("CPU: unavailable", DebugFfmpegPlaytest.Measurements(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero,
            0, null, null, TimeSpan.Zero, "None"));
        Assert.Contains("CPU: unavailable", DebugFfmpegPlaytest.Measurements(TimeSpan.Zero, TimeSpan.Zero,
            TimeSpan.FromSeconds(1), 0, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.Zero, "Paused"));
        Assert.Equal("unavailable (process times did not advance)", DebugFfmpegPlaytest.CpuCores(
            TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void CpuUsesFileTimeTicksAndKeepsSmallLoadsVisible()
    {
        Assert.Equal(TimeSpan.FromSeconds(3.5), DebugFfmpegPlaytest.CpuTime(10_000_000, 25_000_000));
        Assert.Equal("0.004 cores", DebugFfmpegPlaytest.CpuCores(TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10.04), TimeSpan.FromSeconds(10)));
        var source = Read("DebugProcessCpu.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("GetProcessTimes(new IntPtr(-1)", source);
        Assert.Contains("[LibraryImport(\"kernel32.dll\", SetLastError = true)]", source);
        Assert.Contains("private static partial int GetProcessTimes", source);
        Assert.Contains("Marshal.GetHRForLastWin32Error()", source);
        Assert.Contains("ex.HResult", source);
        Assert.DoesNotContain("ex.Message", source);
        Assert.DoesNotContain("ProcessDiagnosticInfo", source);
    }

    [Theory]
    [InlineData(2.5, 6, "2.500 cores · 41.7% of 6 CPUs")]
    [InlineData(0.008, 8, "0.008 cores · 0.1% of 8 CPUs")]
    [InlineData(0, 6, "unavailable (process times did not advance) · 6 CPUs")]
    public void ProcessCpuPercentageUsesTheExplicitLogicalCpuDenominator(double cores, int processors, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal(expected, DebugFfmpegPlaytest.CpuUsage(TimeSpan.Zero, TimeSpan.FromSeconds(cores * 10),
                TimeSpan.FromSeconds(10), processors));
        }
        finally { CultureInfo.CurrentCulture = previous; }
        Assert.Equal("unavailable · 6 CPUs", DebugFfmpegPlaytest.CpuUsage(null, null, TimeSpan.Zero, 6));
    }

    [Fact]
    public void EndOfStreamResetKeepsTheMaximumObservedPositionAndState()
    {
        var maximum = TimeSpan.FromSeconds(9.5);
        var text = DebugFfmpegPlaytest.Measurements(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(10),
            0, null, null, maximum, "Paused");
        Assert.Contains("PlaybackState: Paused", text);
        Assert.Contains("start=0s end=0s max=9.5s wall=10s expected=10s ratio=0.95", text);
        Assert.StartsWith("Verdict: PLAYS - real time", DebugFfmpegPlaytest.Verdict(true,
            TimeSpan.Zero, maximum, TimeSpan.FromSeconds(10), []));
    }

    [Fact]
    public void WinRtProbeIsDebugOnlyMutedBoundedAndNeverFormatsMediaExceptions()
    {
        var source = Read("DebugServer.Ffmpeg.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        string[] required =
        [
            "Interlocked.CompareExchange(ref _playtestBusy, 1, 0)", "409 Conflict",
            "FfmpegLogging.Configure()", "FFmpegMediaSource.CreateFromUriAsync(uri.AbsoluteUri, config).AsTask()",
            "using var source = await creation", "var player = await OnFfmpegUiAsync(() => new MediaPlayer { IsMuted = true, AutoPlay = false })",
            "Attempt(\"dispose player\", player.Dispose)",
            "source.CreateMediaPlaybackItem()", "VideoDecoderMode.Automatic", "VideoDecoderMode.ForceFFmpegSoftwareDecoder",
            "VideoDecoderMode.ForceSystemDecoder", "source.CurrentVideoStream", "source.CurrentAudioStream",
            "video.DecoderEngine", "audio.DecoderEngine", "DebugProcessCpu.Read(out var failure)",
            "cpuDiagnostics.Add(failure)", "Stopwatch.StartNew()", "Task.Delay(DebugFfmpegPlaytest.CreateTimeoutMs)",
            "Task.Delay(DebugFfmpegPlaytest.OpenTimeoutMs)", "Task.Delay(TimeSpan.FromSeconds(duration))",
            "Interlocked.Increment(ref buffering)", "player.Pause()", "player.Source = null",
            "player.MediaOpened -= OnOpened", "player.MediaFailed -= OnFailed", "player.MediaEnded -= OnEnded",
            "player.BufferingStarted -= OnBuffering", "DisposeLateFfmpegSourceAsync(creation)",
            "Debug FFmpeg: before create", "before item/open", "Debug FFmpeg: before play", "Debug FFmpeg: done",
            "await OnFfmpegUiAsync(() => { renderTarget.Attach(player); return true; })",
            "CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync", "player.SetSurfaceSize(new Size(160, 90))",
            "player.GetSurface(compositor)", "CreateSurfaceBrush(_surface.CompositionSurface)", "CreateSpriteVisual()",
            "_visual.Size = new Vector2(160, 90)", "ElementCompositionPreview.SetElementChildVisual(_host, _container)",
            "ElementCompositionPreview.SetElementChildVisual(_host!, _previous)", "renderTarget.Close(Attempt)",
            "_surface?.Dispose()", "_brush?.Dispose()", "_visual?.Dispose()", "_container?.Dispose()",
            "player.PlaybackSession.PlaybackState.ToString()", "if (end > maximum) maximum = end", "Task.Delay(100)",
            "DebugFfmpegPlaytest.Verdict(didOpen, start, maximum, wall, reasons, playbackRate)",
            "TryParseTuning(threads, output10bit, rate", "config.Video.MaxDecoderThreads", "config.Video.VideoOutputAllow10bit",
            "player.PlaybackSession.PlaybackRate = tuning.Rate", "return player.PlaybackSession.PlaybackRate",
            "DebugFfmpegPlaytest.FrameDelivery(frames)", "Output pixel format:", "frameDelivery?.Read()",
            "frameDelivery?.OutputPixelFormat", "frameDelivery?.Dispose()", "new DebugFrameDelivery(source,",
            "source.CurrentVideoStream?.DecoderEngine == DecoderEngine.FFmpegSoftwareDecoder", "collectAheadStatistics: true",
            "if (tuning.Threads is { } threads) config.Video.MaxDecoderThreads = threads;",
            "if (tuning.Allow10bit is { } allow10bit) config.Video.VideoOutputAllow10bit = allow10bit;",
        ];
        var shared = Read("DebugFfmpegSource.cs.txt");
        Assert.Contains("DebugFfmpegSource.Configuration(decoderMode, tuning)", source);
        foreach (var text in required) Assert.Contains(text, source + shared);
        Assert.DoesNotContain("ex.Message", source + shared);
        Assert.DoesNotContain("args.ErrorMessage", source);
        Assert.DoesNotContain("Log.Error(", source);
        Assert.DoesNotContain("Session.Client", source);
        Assert.DoesNotContain("video.BitsPerSample", source);
        Assert.DoesNotContain(" bits=", source);
        Assert.True(source.IndexOf("renderTarget.Attach(player)", StringComparison.Ordinal)
            < source.IndexOf("player.Source = item", StringComparison.Ordinal));
        Assert.True(source.IndexOf("source.CreateMediaPlaybackItem()", StringComparison.Ordinal)
            < source.IndexOf("new DebugFrameDelivery(source,", StringComparison.Ordinal));
        Assert.True(source.IndexOf("new DebugFrameDelivery(source,", StringComparison.Ordinal)
            < source.IndexOf("player.Source = item", StringComparison.Ordinal));
        Assert.True(source.IndexOf("frameDelivery?.Dispose()", StringComparison.Ordinal)
            < source.IndexOf("player.Source = null", StringComparison.Ordinal));
        Assert.True(source.IndexOf("player.PlaybackSession.PlaybackRate = tuning.Rate", StringComparison.Ordinal)
            < source.IndexOf("player.Play()", StringComparison.Ordinal));
        Assert.True(source.IndexOf("playbackState = player.PlaybackSession.PlaybackState", StringComparison.Ordinal)
            < source.IndexOf("player.Pause()", StringComparison.Ordinal));
        Assert.Contains("case (\"GET\", \"/playtest-ffmpeg\"):", Read("DebugServer.cs.txt"));
    }

    [Fact]
    public void ManifestRegistersAllSixteenFactoriesAndPreservesTheStampedApp()
    {
        var original = XDocument.Parse(Read("Package.appxmanifest"));
        var transform = new XslCompiledTransform();
        using (var stylesheet = XmlReader.Create(Path.Combine(AppContext.BaseDirectory, "FFmpegManifest.xslt"))) transform.Load(stylesheet);
        using var output = new StringWriter();
        using (var input = original.CreateReader()) transform.Transform(input, null, output);
        var generated = XDocument.Parse(output.ToString());
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        Assert.True(XNode.DeepEquals(original.Root!.Element(ns + "Identity"), generated.Root!.Element(ns + "Identity")));
        Assert.True(XNode.DeepEquals(original.Root.Element(ns + "Dependencies"), generated.Root.Element(ns + "Dependencies")));
        Assert.True(XNode.DeepEquals(original.Root.Element(ns + "Capabilities"), generated.Root.Element(ns + "Capabilities")));
        XNamespace rescap = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
        Assert.Single(generated.Descendants(rescap + "Capability"), capability => capability.Attribute("Name")?.Value == "hevcPlayback");
        Assert.Empty(original.Descendants(ns + "ActivatableClass"));
        var extensions = generated.Root.Element(ns + "Extensions");
        Assert.NotNull(extensions);
        Assert.Empty(generated.Descendants(ns + "Application").Elements(ns + "Extensions"));
        var server = Assert.Single(extensions.Descendants(ns + "InProcessServer"));
        Assert.Equal("FFmpegInteropX.dll", server.Element(ns + "Path")!.Value);
        var classes = server.Elements(ns + "ActivatableClass").ToArray();
        Assert.Equal(16, classes.Length);
        Assert.All(classes, c => Assert.Equal("both", c.Attribute("ThreadingModel")!.Value));
        Assert.Contains(classes, c => c.Attribute("ActivatableClassId")!.Value == "FFmpegInteropX.MediaSourceConfig");
        Assert.Contains(classes, c => c.Attribute("ActivatableClassId")!.Value == "FFmpegInteropX.FFmpegMediaSource");
        Assert.Contains(classes, c => c.Attribute("ActivatableClassId")!.Value == "FFmpegInteropX.FFmpegInteropLogging");
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        Assert.True(XNode.DeepEquals(original.Descendants(uap + "VisualElements").Single(), generated.Descendants(uap + "VisualElements").Single()));
    }

    [Fact]
    public void PackagesActivationAndLicensesAreUnconditional()
    {
        var project = XDocument.Parse(Read("Bale.Xbox.csproj.txt"));
        var packages = project.Descendants("PackageReference").Where(p => p.Attribute("Include")!.Value.StartsWith("FFmpegInteropX.")).ToArray();
        Assert.Equal(2, packages.Length);
        Assert.All(packages, p => Assert.DoesNotContain(p.AncestorsAndSelf(), element => element.Attribute("Condition") is not null));
        Assert.Equal("[2.1.0-bale.19]", packages.Single(p => p.Attribute("Include")!.Value.EndsWith(".Lib")).Attribute("Version")!.Value);
        Assert.Equal("[8.1.2-bale.dav1d.1.5.4.p1]", packages.Single(p => p.Attribute("Include")!.Value.EndsWith(".FFmpeg")).Attribute("Version")!.Value);
        var target = project.Descendants("Target").Single(t => t.Attribute("Name")!.Value == "PrepareFFmpegManifest");
        Assert.Null(target.Attribute("Condition"));
        var licenses = project.Descendants("Content").Where(content => content.Attribute("Link")?.Value.StartsWith("Licenses") == true).ToArray();
        Assert.Equal(4, licenses.Length);
        Assert.Contains(licenses, license => license.Attribute("Link")!.Value == @"Licenses\FFmpeg\SOURCES.txt");
        Assert.Contains(licenses, license => license.Attribute("Link")!.Value == @"Licenses\FFmpegInteropX\SOURCES.txt");
        Assert.All(licenses, license => Assert.DoesNotContain(license.AncestorsAndSelf(), element => element.Attribute("Condition") is not null));
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
