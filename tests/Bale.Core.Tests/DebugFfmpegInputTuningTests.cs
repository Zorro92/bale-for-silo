using System.Globalization;
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
        Assert.Contains("default restores the release setting", DebugFfmpegInputTuning.Usage);
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
    [InlineData("0", "1", "0")]
    [InlineData("64", "16", "64")]
    [InlineData("0008", "04", "001")]
    public void ForkOptionsUseInclusiveBoundsAndInvariantDescriptions(string ahead, string sws, string delay)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string>
            {
                ["delay"] = delay, ["ahead"] = ahead, ["sws"] = sws,
            }, out var tuning));
            var expected = new DebugFfmpegInputTuning(DecodeAheadFrames: int.Parse(ahead, CultureInfo.InvariantCulture),
                SwsThreads: int.Parse(sws, CultureInfo.InvariantCulture), Dav1dMaxFrameDelay: int.Parse(delay, CultureInfo.InvariantCulture));
            Assert.Equal(expected, tuning);
            Assert.Equal($"readahead=default readsize=default prefetch=default ahead={expected.DecodeAheadFrames} sws={expected.SwsThreads} delay={expected.Dav1dMaxFrameDelay}", tuning.Description);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void ForkOptionsAreCapturedAndClearedIndependentlyWithoutResettingOtherOverrides()
    {
        Set(new(true, 65536, 8, 1500, 8, 4, 1));
        var captured = DebugFfmpegInputTuning.Current;
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["ahead"] = "default" }, out var first));
        Assert.Equal(captured with { DecodeAheadFrames = null }, first);
        Assert.DoesNotContain(" ahead=", first.Description);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["sws"] = "default" }, out var second));
        Assert.Equal(first with { SwsThreads = null }, second);
        Assert.DoesNotContain("sws=", second.Description);
        Assert.True(DebugFfmpegInputTuning.TrySet(new Dictionary<string, string> { ["delay"] = "default" }, out var third));
        Assert.Equal(new DebugFfmpegInputTuning(true, 65536, 8, 1500), third);
        Assert.Equal("readahead=on readsize=65536 prefetch=8 buffer=1500", third.Description);
        Assert.Equal(new DebugFfmpegInputTuning(true, 65536, 8, 1500, 8, 4, 1), captured);
    }

    [Theory]
    [InlineData("ahead", 0, 64)]
    [InlineData("sws", 1, 16)]
    [InlineData("delay", 0, 64)]
    public void InvalidForkOptionUpdatesAreStrictAndAtomic(string key, int minimum, int maximum)
    {
        Set(new(false, 65536, 4, 1500, 8, 4, 1));
        var before = DebugFfmpegInputTuning.Current;
        foreach (var value in new[] { "-1", "+1", " 1", "1 ", "1.0", "1,0", "1e0", "0x1", "", "DEFAULT", "2147483648",
            (minimum - 1).ToString(CultureInfo.InvariantCulture), (maximum + 1).ToString(CultureInfo.InvariantCulture) })
        {
            var options = new Dictionary<string, string>
            {
                ["readahead"] = "on", ["buffer"] = "0", ["ahead"] = "64", ["sws"] = "16", ["delay"] = "64",
            };
            options[key] = value;
            Assert.False(DebugFfmpegInputTuning.TrySet(options, out var current), key + "=" + value);
            Assert.Same(before, current);
            Assert.Same(before, DebugFfmpegInputTuning.Current);
        }
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
        Assert.Contains("if (route.SoftwareVideo)\n            ApplyDecodeTuning(config, new(tuning.DecodeAheadFrames, tuning.SwsThreads, tuning.Dav1dMaxFrameDelay))", experiment);
        Assert.Contains("ApplyDecodeTuning(config, SoftwareDecodeTuning.ForVideo(route.SoftwareVideo, route.VideoWidth, route.VideoHeight))", configuration[..start]);
        foreach (var (property, variable, option) in new[]
        {
            ("DecodeAheadFrames", "ahead", "bale_decode_ahead_frames"), ("SwsThreads", "sws", "bale_sws_threads"),
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
        ["sws"] = tuning.SwsThreads?.ToString(CultureInfo.InvariantCulture) ?? "default",
        ["delay"] = tuning.Dav1dMaxFrameDelay?.ToString(CultureInfo.InvariantCulture) ?? "default",
    }, out _));

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
