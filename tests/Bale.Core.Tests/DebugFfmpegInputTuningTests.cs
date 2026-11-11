using System.Globalization;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public sealed class DebugFfmpegInputTuningTests : IDisposable
{
    private readonly DebugFfmpegInputTuning _previous = DebugFfmpegInputTuning.Current;

    public DebugFfmpegInputTuningTests() => Set(new());
    public void Dispose() => Set(_previous);

    [Fact]
    public void DefaultsKeepReleaseSettingsAndDescribeOnlyOverrides()
    {
        Assert.Equal(new DebugFfmpegInputTuning(), DebugFfmpegInputTuning.Current);
        Assert.Equal("readahead=default readsize=default prefetch=default", DebugFfmpegInputTuning.Current.Description);
        Assert.Contains("default restores the release setting (buffer=1000 for software video;", DebugFfmpegInputTuning.Usage);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string>(), out var current));
        Assert.Same(DebugFfmpegInputTuning.Current, current);
    }

    [Fact]
    public void BufferTimeIsBoundedMillisecondsAndDescribedOnlyWhenSet()
    {
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["buffer"] = "1500" }, out var set));
        Assert.Equal(1500, set.BufferMs);
        Assert.Equal("readahead=default readsize=default prefetch=default buffer=1500", set.Description);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["buffer"] = "0" }, out var zero));
        Assert.Equal(0, zero.BufferMs);
        Assert.False(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["buffer"] = "5001" }, out _));
        Assert.False(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["buffer"] = "-1" }, out _));
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["buffer"] = "default" }, out var cleared));
        Assert.Null(cleared.BufferMs);
        Assert.Equal("readahead=default readsize=default prefetch=default", cleared.Description);
    }

    [Fact]
    public void BufferTimeIsAppliedAfterItemCreationWithOnlyTheOverrideDebugGuarded()
    {
        var player = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("int? bufferOverrideMs = null;\n#if DEBUG_SERVER\n                bufferOverrideMs = attempt.FfmpegTuning.BufferMs;\n#endif\n                if (SoftwareDecodeTuning.BufferTimeMs(media.Route.SoftwareVideo, bufferOverrideMs,\n                        media.Route.VideoWidth, media.Route.VideoHeight) is { } bufferMs)", player);
        var item = player.IndexOf("media.Item = media.FfmpegSource.CreateMediaPlaybackItem();", StringComparison.Ordinal);
        var buffering = player.IndexOf("if (SoftwareDecodeTuning.BufferTimeMs(", StringComparison.Ordinal);
        Assert.True(item >= 0 && buffering > item);
        Assert.Contains("var mss = media.FfmpegSource.GetMediaStreamSource();\n                    if (mss is null)", player);
        Assert.Contains("mss.BufferTime = TimeSpan.FromMilliseconds(bufferMs);", player);
        Assert.Contains("Log.Info($\"player: FFmpeg MediaStreamSource BufferTime={bufferMs} ms\");", player);
    }

    [Fact]
    public void UpdatesPublishImmutableSnapshotsAndOmittedKeysKeepTheirOverrides()
    {
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string>
        {
            ["prefetch"] = "8", ["readahead"] = "on", ["readsize"] = "65536",
        }, out var captured));
        Assert.Equal(new DebugFfmpegInputTuning(true, 65536, 8), captured);
        Assert.Equal("readahead=on readsize=65536 prefetch=8", captured.Description);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["readsize"] = "1048576", ["readahead"] = "off" }, out var next));
        Assert.Equal(new DebugFfmpegInputTuning(false, 1048576, 8), next);
        Assert.Equal(new DebugFfmpegInputTuning(true, 65536, 8), captured);
        Assert.NotSame(captured, next);
        Assert.Same(next, DebugFfmpegInputTuning.Current);
    }

    [Fact]
    public void EachKeyCanBeClearedWithoutResettingTheOtherKeys()
    {
        Set(new(true, 65536, 8));
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["readahead"] = "default" }, out var first));
        Assert.Equal(new DebugFfmpegInputTuning(null, 65536, 8), first);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["readsize"] = "default", ["prefetch"] = "default" }, out var cleared));
        Assert.Equal(new DebugFfmpegInputTuning(), cleared);
    }

    [Theory]
    [InlineData("16384", "2")]
    [InlineData("1048576", "16")]
    [InlineData("065536", "08")]
    public void NumericBoundsAreInclusiveAndParsedAsInvariantDecimal(string size, string prefetch)
    {
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["readsize"] = size, ["prefetch"] = prefetch }, out var current));
        Assert.Equal(int.Parse(size, CultureInfo.InvariantCulture), current.ReadSize);
        Assert.Equal(int.Parse(prefetch, CultureInfo.InvariantCulture), current.Prefetch);
    }

    [Theory]
    [InlineData("readahead", "true")]
    [InlineData("readahead", "ON")]
    [InlineData("readahead", "")]
    [InlineData("readsize", "16383")]
    [InlineData("readsize", "1048577")]
    [InlineData("readsize", "-16384")]
    [InlineData("readsize", "+16384")]
    [InlineData("readsize", "16KB")]
    [InlineData("readsize", "16,384")]
    [InlineData("readsize", " 16384")]
    [InlineData("readsize", "18446744073709551616")]
    [InlineData("readsize", "")]
    [InlineData("prefetch", "1")]
    [InlineData("prefetch", "17")]
    [InlineData("prefetch", "2.0")]
    [InlineData("unknown", "1")]
    public void InvalidUpdatesAreAtomicAndNeverChangeTheCurrentOverride(string key, string value)
    {
        Set(new(false, 65536, 4));
        var before = DebugFfmpegInputTuning.Current;
        var options = new Dictionary<string, string> { ["readahead"] = "on", ["readsize"] = "1048576", ["prefetch"] = "16" };
        options[key] = value;
        Assert.False(DebugFfmpegInputTuning.TrySet(options, out var current));
        Assert.Same(before, current);
        Assert.Same(before, DebugFfmpegInputTuning.Current);
    }

    [Theory]
    [InlineData("0", "0", "0", "0", "0")]
    [InlineData("64", "64", "2", "1", "16")]
    [InlineData("0008", "001", "001", "001", "003")]
    public void ForkOptionsUseInclusiveBoundsAndInvariantDescriptions(string ahead, string delay, string prio, string maxres, string dpb)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string>
            {
                ["delay"] = delay, ["prio"] = prio, ["ahead"] = ahead, ["dpb"] = dpb, ["maxres"] = maxres,
            }, out var tuning));
            var expected = new DebugFfmpegInputTuning(DecodeAheadFrames: int.Parse(ahead, CultureInfo.InvariantCulture),
                Dav1dMaxFrameDelay: int.Parse(delay, CultureInfo.InvariantCulture),
                DecodeThreadPriority: int.Parse(prio, CultureInfo.InvariantCulture), DecoderUseMaxResolution: int.Parse(maxres, CultureInfo.InvariantCulture),
                DecoderMaxDpbCount: int.Parse(dpb, CultureInfo.InvariantCulture));
            Assert.Equal(expected, tuning);
            Assert.Equal($"readahead=default readsize=default prefetch=default ahead={expected.DecodeAheadFrames} delay={expected.Dav1dMaxFrameDelay} prio={expected.DecodeThreadPriority} maxres={expected.DecoderUseMaxResolution} dpb={expected.DecoderMaxDpbCount}", tuning.Description);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ForkOptionsAreCapturedAndClearedIndependentlyWithoutResettingOtherOverrides()
    {
        Set(new(true, 65536, 8, 1500, 8, 1, 2));
        var captured = DebugFfmpegInputTuning.Current;
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["ahead"] = "default" }, out var first));
        Assert.Equal(captured with { DecodeAheadFrames = null }, first);
        Assert.DoesNotContain(" ahead=", first.Description);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["delay"] = "default" }, out var third));
        Assert.Equal(new DebugFfmpegInputTuning(true, 65536, 8, 1500, DecodeThreadPriority: 2), third);
        Assert.Equal("readahead=on readsize=65536 prefetch=8 buffer=1500 prio=2", third.Description);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["prio"] = "default" }, out var fourth));
        Assert.Equal(new DebugFfmpegInputTuning(true, 65536, 8, 1500), fourth);
        Assert.DoesNotContain("prio=", fourth.Description);
        Assert.Equal(new DebugFfmpegInputTuning(true, 65536, 8, 1500, 8, 1, 2), captured);
    }

    [Theory]
    [InlineData("ahead", 0, 64)]
    [InlineData("delay", 0, 64)]
    [InlineData("prio", 0, 2)]
    [InlineData("maxres", 0, 1)]
    [InlineData("dpb", 0, 16)]
    public void InvalidForkOptionUpdatesAreStrictAndAtomic(string key, int minimum, int maximum)
    {
        Set(new(false, 65536, 4, 1500, 8, 1, 1));
        var before = DebugFfmpegInputTuning.Current;
        foreach (var value in new[] { "-1", "+1", " 1", "1 ", "1.0", "1,0", "1e0", "0x1", "", "DEFAULT", "2147483648",
            (minimum - 1).ToString(CultureInfo.InvariantCulture), (maximum + 1).ToString(CultureInfo.InvariantCulture) })
        {
            var options = new Dictionary<string, string>
            {
                ["readahead"] = "on", ["buffer"] = "0", ["ahead"] = "64", ["delay"] = "64", ["prio"] = "2",
                ["maxres"] = "1", ["dpb"] = "16",
            };
            options[key] = value;
            Assert.False(DebugFfmpegInputTuning.TrySet(options, out var current), key + "=" + value);
            Assert.Same(before, current);
            Assert.Same(before, DebugFfmpegInputTuning.Current);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    public void PriorityUpdatesAndClearingPreserveOtherOverridesAndCapturedAttempts(string prio)
    {
        Set(new(true, 65536, 8, 1500, 8, 1));
        var captured = DebugFfmpegInputTuning.Current;
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["prio"] = prio }, out var set));
        Assert.Equal(captured with { DecodeThreadPriority = int.Parse(prio, CultureInfo.InvariantCulture) }, set);
        Assert.Contains(" prio=" + prio, set.Description);
        Assert.Null(captured.DecodeThreadPriority);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["prio"] = "default" }, out var cleared));
        Assert.Equal(captured, cleared);
        Assert.DoesNotContain("prio=", cleared.Description);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "3")]
    [InlineData("1", "16")]
    public void DecoderHintsAreCapturedAndClearedIndependentlyWithoutChangingOtherOverrides(string maxres, string dpb)
    {
        Set(new(true, 65536, 8, 1500, 8, 1, 2));
        var captured = DebugFfmpegInputTuning.Current;
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["maxres"] = maxres, ["dpb"] = dpb }, out var set));
        Assert.Equal(captured with
        {
            DecoderUseMaxResolution = int.Parse(maxres, CultureInfo.InvariantCulture),
            DecoderMaxDpbCount = int.Parse(dpb, CultureInfo.InvariantCulture),
        }, set);
        Assert.Contains(" maxres=" + maxres + " dpb=" + dpb, set.Description);
        Assert.Null(captured.DecoderUseMaxResolution);
        Assert.Null(captured.DecoderMaxDpbCount);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["maxres"] = "default" }, out var first));
        Assert.Equal(set with { DecoderUseMaxResolution = null }, first);
        Assert.DoesNotContain("maxres=", first.Description);
        Assert.Contains(" dpb=" + dpb, first.Description);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["dpb"] = "default" }, out var cleared));
        Assert.Equal(captured, cleared);
        Assert.DoesNotContain("dpb=", cleared.Description);
        Assert.Equal(int.Parse(maxres, CultureInfo.InvariantCulture), set.DecoderUseMaxResolution);
        Assert.Equal(int.Parse(dpb, CultureInfo.InvariantCulture), set.DecoderMaxDpbCount);
    }

    [Fact]
    public void AppliedValuesHaveCompactInvariantTextAndExplicitReadAheadCaps()
    {
        Assert.Equal(64L * 1024 * 1024, DebugFfmpegInputTuning.ReadAheadCapBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), DebugFfmpegInputTuning.ReadAheadCapDuration);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("on · 64 MiB / 30 s", DebugFfmpegInputTuning.ReadAheadText(true,
                DebugFfmpegInputTuning.ReadAheadCapBytes, DebugFfmpegInputTuning.ReadAheadCapDuration));
            Assert.Equal("off · 64 MiB / 30.5 s", DebugFfmpegInputTuning.ReadAheadText(false,
                DebugFfmpegInputTuning.ReadAheadCapBytes, TimeSpan.FromSeconds(30.5)));
            Assert.Equal("65536 B · prefetch=8", DebugFfmpegInputTuning.StreamReadsText(65536, 8));
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
    }

    [Fact]
    public void OnlyDebugSessionsApplyTheCapturedOverrideAndLogEffectiveValuesOnce()
    {
        var tuning = Read("DebugFfmpegInputTuning.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", tuning);
        Assert.DoesNotContain("Settings", tuning);
        Assert.DoesNotContain("Storage", tuning);
        var configuration = Read("FfmpegSource.cs.txt");
        var start = configuration.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal);
        var end = configuration.IndexOf("#endif", start, StringComparison.Ordinal);
        Assert.DoesNotContain("config.General.", configuration[..start]);
        var experiment = configuration[start..end];
        Assert.Contains("Configuration(PlaybackRouteDecision route, DebugFfmpegInputTuning tuning)", experiment);
        Assert.Contains("if (tuning.ReadAhead is { } enabled)", experiment);
        Assert.Contains("config.General.ReadAheadBufferEnabled = enabled", experiment);
        Assert.Contains("if (enabled)", experiment);
        Assert.Contains("config.General.ReadAheadBufferSize = DebugFfmpegInputTuning.ReadAheadCapBytes", experiment);
        Assert.Contains("config.General.ReadAheadBufferDuration = DebugFfmpegInputTuning.ReadAheadCapDuration", experiment);
        Assert.Contains("if (tuning.ReadSize is { } readSize) config.General.FileStreamReadSize = checked((uint)readSize)", experiment);
        Assert.Contains("var config = Configuration(route)", experiment);
        Assert.DoesNotContain("bale_decode_thread_priority", configuration[..start]);
        Assert.Contains("if (route.SoftwareVideo)\n        {\n            ApplyDecodeTuning(config, new(tuning.DecodeAheadFrames, tuning.Dav1dMaxFrameDelay));", experiment);
        Assert.Contains("if (tuning.DecodeThreadPriority is { } priority) config.FFmpegOptions[\"bale_decode_thread_priority\"] = priority.ToString(CultureInfo.InvariantCulture);", experiment);
        Assert.Contains("if (DebugFfmpegPlaytest.GrainOption(tuning.FilmGrain) is { } grain) config.FFmpegOptions[grain.Key] = grain.Value;", experiment);
        Assert.True(experiment.IndexOf("var config = Configuration(route)", StringComparison.Ordinal)
            < experiment.IndexOf("if (route.SoftwareVideo)", StringComparison.Ordinal));
        Assert.Contains("ApplyDecodeTuning(config, SoftwareDecodeTuning.ForVideo(route.SoftwareVideo, route.VideoWidth, route.VideoHeight))", configuration[..start]);
        foreach (var (property, variable, option) in new[]
        {
            ("DecodeAheadFrames", "ahead", "bale_decode_ahead_frames"),
            ("Dav1dMaxFrameDelay", "delay", "bale_dav1d_max_frame_delay"),
        })
        {
            Assert.Contains($"if (tuning.{property} is {{ }} {variable}) config.FFmpegOptions[\"{option}\"] = {variable}.ToString(CultureInfo.InvariantCulture)", configuration[..start]);
            Assert.DoesNotContain(option, experiment);
        }
        Assert.Contains("public DebugFfmpegInputTuning FfmpegTuning { get; } = DebugFfmpegInputTuning.Current", Read("PlayerPage.cs.txt"));
        var player = Read("PlayerPage.Media.cs.txt");
        Assert.Contains("media.Route.UseFfmpeg ? attempt.FfmpegTuning.Prefetch ?? HttpRangeStream.DefaultPrefetchBlocks", player);
        Assert.Contains("FfmpegSource.Configuration(media.Route, attempt.FfmpegTuning)", player);
        Assert.Contains("media.Stream.PrefetchBlocks", player);
        Assert.Contains("if (!attempt.FfmpegTuningLogged)", player);
        Assert.Contains("attempt.FfmpegTuningLogged = true", player);
        Assert.DoesNotContain("DebugFfmpegInputTuning.Current", player);
        Assert.DoesNotContain("ex.Message", player);
    }

    [Fact]
    public void DecoderHintsAreDebugOnlyAndAppliedOnlyToTheSystemVideoRoute()
    {
        var configuration = Read("FfmpegSource.cs.txt");
        var start = configuration.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal);
        var end = configuration.IndexOf("#endif", start, StringComparison.Ordinal);
        var experiment = configuration[start..end];
        foreach (var option in new[] { "bale_decoder_use_max_resolution", "bale_decoder_max_dpb_count" })
            Assert.DoesNotContain(option, configuration[..start]);
        Assert.Contains("else\n        {\n            if (tuning.DecoderUseMaxResolution is { } maxres) config.FFmpegOptions[\"bale_decoder_use_max_resolution\"] = maxres.ToString(CultureInfo.InvariantCulture);\n            if (tuning.DecoderMaxDpbCount is { } dpb) config.FFmpegOptions[\"bale_decoder_max_dpb_count\"] = dpb.ToString(CultureInfo.InvariantCulture);\n        }", experiment);
        Assert.Contains("maxres=0|1|default", DebugFfmpegInputTuning.Usage);
        Assert.Contains("dpb=0..16|default", DebugFfmpegInputTuning.Usage);
    }

    [Theory]
    [InlineData("on", "Dav1d", " grain=dav1d")]
    [InlineData("dav1d", "Dav1d", " grain=dav1d")]
    [InlineData("gpu", "Gpu", " grain=gpu")]
    [InlineData("off", "Off", " grain=off")]
    public void FilmGrainOverrideIsCapturedAndClearedWithoutChangingOtherOverrides(string value, string expected, string description)
    {
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["prio"] = "2" }, out _));
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["grain"] = value }, out var set));
        Assert.Equal(expected, set.FilmGrain?.ToString());
        Assert.Equal(2, set.DecodeThreadPriority);
        Assert.EndsWith(" prio=2" + description, set.Description);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["grain"] = "default" }, out var cleared));
        Assert.Null(cleared.FilmGrain);
        Assert.DoesNotContain("grain=", cleared.Description);
        Assert.Equal(2, SoftwareDecodeTuning.FilmGrainMode);
        Assert.Null(DebugFfmpegPlaytest.GrainOption(cleared.FilmGrain));
        foreach (var invalid in new[] { "OFF", "1", "", "true", "cpu" })
            Assert.False(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["grain"] = invalid }, out _));
        Assert.Null(DebugFfmpegInputTuning.Current.FilmGrain);
    }

    [Fact]
    public void FilmGrainExportIsDebugOnlyAndAppliedOnlyToSoftwareVideo()
    {
        var configuration = Read("FfmpegSource.cs.txt");
        var start = configuration.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal);
        var end = configuration.IndexOf("#endif", start, StringComparison.Ordinal);
        Assert.DoesNotContain("bale_dav1d_export_film_grain", configuration[..start]);
        var experiment = configuration[start..end];
        var software = experiment.IndexOf("if (route.SoftwareVideo)", StringComparison.Ordinal);
        var system = experiment.IndexOf("else\n", software, StringComparison.Ordinal);
        var key = experiment.IndexOf("if (DebugFfmpegPlaytest.GrainOption(tuning.FilmGrain) is { } grain) config.FFmpegOptions[grain.Key] = grain.Value;", StringComparison.Ordinal);
        Assert.True(software >= 0 && key > software && key < system);
        Assert.Contains("grain=dav1d|gpu|off|on|default", DebugFfmpegInputTuning.Usage);
        Assert.Contains("config.FFmpegOptions[\"bale_film_grain_mode\"] = SoftwareDecodeTuning.FilmGrainMode.ToString(CultureInfo.InvariantCulture);", configuration[..start]);
        Assert.Equal(("bale_film_grain_mode", "0"), DebugFfmpegPlaytest.GrainOption(DebugFilmGrain.Dav1d));
        Assert.Contains("default=gpu (production), on/dav1d=dav1d", DebugFfmpegInputTuning.Usage);
        Assert.Contains("production default=gpu, on/dav1d=dav1d", Read("DebugServer.cs.txt"));
        Assert.Contains("grain=default: gpu (production default); on/dav1d: dav1d", Read("bale-debug.sh"));
        Assert.Contains("software mode uses the production path (zero-copy, upload variant 6, GPU grain)", Read("bale-debug.sh"));
    }

    [Fact]
    public void SoftwareVideoAlwaysTakesTheFixedZeroCopyPathWithNoDebugOverride()
    {
        var configuration = Read("FfmpegSource.cs.txt");
        var start = configuration.IndexOf("#if DEBUG_SERVER", StringComparison.Ordinal);
        var end = configuration.IndexOf("#endif", start, StringComparison.Ordinal);
        var production = configuration[..start];
        Assert.Contains("if (route.SoftwareVideo) ApplySoftwareVideo(config);", production);
        var apply = production[production.IndexOf("internal static void ApplySoftwareVideo(MediaSourceConfig config)", StringComparison.Ordinal)..];
        var register = apply.IndexOf("if (!ZeroCopyRuntime.Register()) throw new InvalidDataException(", StringComparison.Ordinal);
        Assert.True(register >= 0);
        foreach (var line in new[]
        {
            "config.FFmpegOptions[\"bale_d3d11_upload\"] = SoftwareDecodeTuning.DefaultD3d11Upload.ToString(CultureInfo.InvariantCulture);",
            "config.FFmpegOptions[\"bale_film_grain_mode\"] = SoftwareDecodeTuning.FilmGrainMode.ToString(CultureInfo.InvariantCulture);",
            "config.FFmpegOptions[\"bale_zero_copy\"] = \"1\";",
        })
            Assert.True(apply.IndexOf(line, StringComparison.Ordinal) > register, line);
        foreach (var option in new[] { "\"bale_d3d11_upload\"", "\"bale_zero_copy\"", "\"bale_film_grain_mode\"" })
            Assert.Equal(1, configuration.Split(option).Length - 1);
        var experiment = configuration[start..end];
        foreach (var removed in new[] { "bale_d3d11_upload", "bale_zero_copy", "bale_d3d11_repack", "bale_d3d11_upload_fault_frames", "bale_sws_threads" })
            Assert.DoesNotContain(removed, experiment);
        foreach (var removed in new[] { "bale_sws_threads", "bale_d3d11_repack", "bale_d3d11_upload_fault_frames" })
            Assert.DoesNotContain(removed, configuration);
        // The probe takes the same path in software mode, before any grain override.
        var probe = Read("DebugFfmpegSource.cs.txt");
        var shared = probe.IndexOf("if (mode == \"software\") FfmpegSource.ApplySoftwareVideo(config);", StringComparison.Ordinal);
        Assert.True(shared >= 0 && shared < probe.IndexOf("DebugFfmpegPlaytest.GrainOption(tuning.FilmGrain)", StringComparison.Ordinal));
        Assert.DoesNotContain("bale_d3d11_upload", probe);
        Assert.Contains("software AV1 is fixed to zero-copy direct write (upload variant 6", DebugFfmpegInputTuning.Usage);
        var server = Read("DebugServer.cs.txt");
        Assert.Contains("FFmpeg tuning: {DebugFfmpegInputTuning.Current.Description}", server);
        Assert.Contains("FFmpeg tuning: {tuning.Description}", server);
    }

    [Theory]
    [InlineData("sws", "4")]
    [InlineData("upload", "6")]
    [InlineData("upload", "default")]
    [InlineData("uploadfault", "5")]
    [InlineData("repack", "on")]
    [InlineData("zerocopy", "off")]
    [InlineData("grain", "cpu")]
    public void RemovedSoftwareFallbackKnobsAreRejectedAtomically(string key, string value)
    {
        Set(new(DecodeAheadFrames: 8, FilmGrain: DebugFilmGrain.Off));
        var captured = DebugFfmpegInputTuning.Current;
        Assert.False(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["prio"] = "1", [key] = value }, out var rejected));
        Assert.Same(captured, rejected);
        Assert.Same(captured, DebugFfmpegInputTuning.Current);
        foreach (var text in new[] { "sws=", "upload=", "uploadfault=", "repack=", "zerocopy=", "cpu" })
            Assert.DoesNotContain(text, DebugFfmpegInputTuning.Usage);
    }

    [Fact]
    public void PlayerClaimsSoftwareVideoOnlyWhenZeroCopyRegisters()
    {
        // The gate lives in the claims helper the player shares with Auto's display matching.
        var claims = Read("PlaybackClaims.cs.txt");
        Assert.Contains("private static bool SoftwareVideo() => DeviceIdentity.SoftwareVideoSupported && ZeroCopyRuntime.Register();", claims);
        Assert.Contains("softwareVideo ? capabilities : SoftwareDecodePolicy.WithoutSoftwareVideo(capabilities)", claims);
        Assert.DoesNotContain("#if", claims);
        var player = Read("PlayerPage.cs.txt");
        var detect = player.IndexOf("var (capabilities, softwareVideo) = await PlaybackClaims.DetectAsync();", StringComparison.Ordinal);
        var build = player.IndexOf("PlaybackStartBuilder.Build(request.Detail", StringComparison.Ordinal);
        Assert.True(detect >= 0 && build > detect);
    }

    [Fact]
    public void StatsAndWhereShareTheAppliedValuesAndRangeWaitCounters()
    {
        var stats = Read("PlayerPage.Stats.cs.txt");
        foreach (var text in new[] { "(\"Stream waits\",", "SupportsRanges: true", "stream.DemandWaits", "stream.MaxDemandWaitTime.TotalMilliseconds",
            "stream.DemandWaitTime.TotalMilliseconds", "(\"Read-ahead\", media?.FfmpegReadAhead", "(\"Stream reads\", media?.FfmpegStreamReads" })
            Assert.Contains(text, stats);
        Assert.True(stats.IndexOf("(\"Late frames\",", StringComparison.Ordinal) < stats.IndexOf("(\"Stream waits\",", StringComparison.Ordinal));
        var server = Read("DebugServer.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", server);
        Assert.Contains("case (\"GET\", \"/ffmpeg-tune\"):", server);
        Assert.Contains("case (\"POST\", \"/ffmpeg-tune\"):", server);
        Assert.Contains("DebugFfmpegInputTuning.TrySet(query, out var tuning)", server);
        Assert.Contains("foreach (var (label, value) in player.DebugStatsRows)", server);
    }

    private static void Set(DebugFfmpegInputTuning tuning) => Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string>
    {
        ["readahead"] = tuning.ReadAhead is { } enabled ? enabled ? "on" : "off" : "default",
        ["readsize"] = tuning.ReadSize?.ToString(CultureInfo.InvariantCulture) ?? "default",
        ["prefetch"] = tuning.Prefetch?.ToString(CultureInfo.InvariantCulture) ?? "default",
        ["buffer"] = tuning.BufferMs?.ToString(CultureInfo.InvariantCulture) ?? "default",
        ["ahead"] = tuning.DecodeAheadFrames?.ToString(CultureInfo.InvariantCulture) ?? "default",
        ["delay"] = tuning.Dav1dMaxFrameDelay?.ToString(CultureInfo.InvariantCulture) ?? "default",
        ["prio"] = tuning.DecodeThreadPriority?.ToString(CultureInfo.InvariantCulture) ?? "default",
        ["maxres"] = tuning.DecoderUseMaxResolution?.ToString(CultureInfo.InvariantCulture) ?? "default",
        ["dpb"] = tuning.DecoderMaxDpbCount?.ToString(CultureInfo.InvariantCulture) ?? "default",
        ["grain"] = DebugFfmpegPlaytest.GrainName(tuning.FilmGrain),
    }, out _));

    [Fact]
    public void ZeroCopyRegistrationIsCachedAndLogsNoExceptionText()
    {
        var probe = Read("ZeroCopyRuntime.cs.txt");
        Assert.DoesNotContain("#if", probe);
        var start = probe.IndexOf("internal static bool Register()", StringComparison.Ordinal);
        var end = probe.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        var body = probe[start..end];
        Assert.Contains("[DllImport(\"bale_gpu.dll\", ExactSpelling = true)] private static extern int bale_zc_register_d3d12(nint d3d12Module);", probe);
        Assert.Contains("if (state != 0) return state > 0;", body);
        Assert.Contains("NativeLibrary.TryLoad(\"d3d12.dll\", out var module)", body);
        Assert.Contains("Volatile.Write(ref _registered, registered ? 1 : -1);", body);
        Assert.Contains("{ex.GetType().Name} 0x{unchecked((uint)ex.HResult):X8}", body);
        Assert.DoesNotContain("ex.Message", body);
        Assert.DoesNotContain("Log.Error", body);
        Assert.DoesNotContain("NativeLibrary.Free", probe);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
