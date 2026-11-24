using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

/// <summary>
/// The debug exclusive-mode output test (docs/audio-passthrough.md, "Exclusive output"): its ABI structs against the
/// header's layout (native/bale-gpu/include/bale_audio_out.h, pinned in Rust too), request rules, tone, feeder and report,
/// plus source guards for the route. The output's own behaviour is tested in Rust; the device needs the console.
/// </summary>
public class DebugAudioOutTests
{
    [Fact]
    public void AbiStructsHaveTheHeaderLayout()
    {
        Assert.Equal(48, Unsafe.SizeOf<BaleAudioFormat>());
        Assert.Equal(32, Offset<BaleAudioFormat>(nameof(BaleAudioFormat.EncodedChannels)));
        Assert.Equal(80, Unsafe.SizeOf<BaleAudioOutOpenInfo>());
        Assert.Equal(28, Offset<BaleAudioOutOpenInfo>(nameof(BaleAudioOutOpenInfo.ActivateMs)));
        Assert.Equal(32, Offset<BaleAudioOutOpenInfo>(nameof(BaleAudioOutOpenInfo.DefaultPeriod100ns)));
        Assert.Equal(48, Offset<BaleAudioOutOpenInfo>(nameof(BaleAudioOutOpenInfo.Period100ns)));
        Assert.Equal(56, Offset<BaleAudioOutOpenInfo>(nameof(BaleAudioOutOpenInfo.BufferFrames)));
        Assert.Equal(68, Offset<BaleAudioOutOpenInfo>(nameof(BaleAudioOutOpenInfo.Realigned)));
        Assert.Equal(152, Unsafe.SizeOf<BaleAudioOutStats>());
        Assert.Equal(28, Offset<BaleAudioOutStats>(nameof(BaleAudioOutStats.Flushes)));
        Assert.Equal(32, Offset<BaleAudioOutStats>(nameof(BaleAudioOutStats.FramesWritten)));
        Assert.Equal(64, Offset<BaleAudioOutStats>(nameof(BaleAudioOutStats.DevicePosition100ns)));
        Assert.Equal(88, Offset<BaleAudioOutStats>(nameof(BaleAudioOutStats.SyncError100ns)));
        Assert.Equal(96, Offset<BaleAudioOutStats>(nameof(BaleAudioOutStats.Syncs)));
        Assert.Equal(112, Offset<BaleAudioOutStats>(nameof(BaleAudioOutStats.DroppedFrames)));
        Assert.Equal(128, Offset<BaleAudioOutStats>(nameof(BaleAudioOutStats.Reserved0)));
        // The header's constants.
        var header = Read("bale_audio_out.h");
        Assert.Contains("#define BALE_AUDIO_OUT_WRITE_CLOSED 0xFFFFFFFFu", header);
        Assert.Contains("#define BALE_AUDIO_PTS_NONE (-9223372036854775807LL - 1)", header);
        Assert.Equal(long.MinValue, BaleAudioOut.PtsNone);
        Assert.Contains("#define BALE_AUDIO_OUT_STATUS_DEVICE_LOST 3u", header);
        Assert.Equal(3u, BaleAudioOut.StatusDeviceLost);
    }

    [Fact]
    public void TheTestOpensTheConsolesAcceptedFormat()
    {
        var format = DebugAudioOutTone.Format;
        Assert.Equal((0u, 8u, 48_000u, 32u, 24u, 0x63Fu, 0u, 0u, 0u),
            (format.Kind, format.Channels, format.SampleRate, format.ContainerBits, format.ValidBits, format.ChannelMask,
                format.IecSubtype, format.EncodedRate, format.EncodedChannels));
        Assert.Equal(32, DebugAudioOutTone.BlockAlign);
    }

    [Theory]
    [InlineData(null, null, 5, 440)]
    [InlineData("1", null, 1, 440)]
    [InlineData("30", "20000", 30, 20_000)]
    [InlineData(null, "20", 5, 20)]
    public void OptionsDefaultAndAcceptTheirRanges(string? seconds, string? tone, int expectedSeconds, int expectedTone)
    {
        var query = new Dictionary<string, string>();
        if (seconds is not null) query["seconds"] = seconds;
        if (tone is not null) query["tone"] = tone;
        Assert.True(DebugAudioOutOptions.TryParse(query, out var options));
        Assert.Equal(new DebugAudioOutOptions(expectedSeconds, expectedTone), options);
    }

    [Theory]
    [InlineData("seconds", "0")]
    [InlineData("seconds", "31")]
    [InlineData("seconds", "-1")]
    [InlineData("seconds", "5.0")]
    [InlineData("seconds", " 5")]
    [InlineData("tone", "19")]
    [InlineData("tone", "20001")]
    [InlineData("tone", "000440")]
    [InlineData("tone", "")]
    [InlineData("freq", "440")]
    public void BadOptionsAreRefused(string key, string value)
    {
        Assert.False(DebugAudioOutOptions.TryParse(new Dictionary<string, string> { [key] = value }, out _));
        Assert.StartsWith("usage: GET /audio-out-test?seconds=1..30&tone=20..20000", DebugAudioOutOptions.Usage);
    }

    [Fact]
    public void TheToneIsOnFrontLeftAndRightOnlyIn24BitsAtMinus18Dbfs()
    {
        var samples = new int[48_000 * DebugAudioOutTone.Channels];
        DebugAudioOutTone.Fill(samples, 0, 1_000);
        var peak = 0;
        for (var frame = 0; frame < 48_000; frame++)
        {
            var left = samples[frame * 8];
            Assert.Equal(left, samples[frame * 8 + 1]);
            for (var channel = 2; channel < 8; channel++) Assert.Equal(0, samples[frame * 8 + channel]);
            // 24 valid bits in the top three bytes: the low byte stays zero.
            Assert.Equal(0, left & 0xFF);
            peak = Math.Max(peak, Math.Abs(left >> 8));
        }
        Assert.Equal((int)Math.Round(0.125 * 8_388_607), peak);
        // 1 kHz at 48 kHz: a quarter period in, the sine peaks.
        Assert.Equal(peak, samples[12 * 8] >> 8);
        Assert.Equal(0, samples[0]);
    }

    [Fact]
    public void ChunksJoinWithoutAClickAndPtsCountFrames()
    {
        var whole = new int[960 * 8];
        DebugAudioOutTone.Fill(whole, 47_520, 440);
        var first = new int[480 * 8];
        var second = new int[480 * 8];
        DebugAudioOutTone.Fill(first, 47_520, 440);
        DebugAudioOutTone.Fill(second, 48_000, 440);
        Assert.Equal(whole, first.Concat(second).ToArray());
        Assert.Equal(0, DebugAudioOutTone.Pts(0));
        Assert.Equal(100_000, DebugAudioOutTone.Pts(480));
        Assert.Equal(10_000_000, DebugAudioOutTone.Pts(48_000));
    }

    [Fact]
    public void TheFeederStopsWhenTheRingIsFullAndResendsWhatWasNotTaken()
    {
        var feeder = new DebugAudioOutFeeder(440);
        var calls = new List<(int Bytes, long Pts)>();
        // The ring takes 1000 frames, then is full.
        var room = 1_000;
        uint Ring(ReadOnlySpan<byte> data, long pts)
        {
            calls.Add((data.Length, pts));
            var frames = Math.Min(room, data.Length / DebugAudioOutTone.BlockAlign);
            room -= frames;
            return (uint)(frames * DebugAudioOutTone.BlockAlign);
        }
        Assert.Equal(1_000, feeder.Feed(Ring));
        Assert.Equal([(480 * 32, 0L), (480 * 32, 100_000L), (480 * 32, 200_000L), (440 * 32, 208_333L)], calls);
        Assert.Equal(1, feeder.FullAnswers);
        // The rest of the third chunk goes again from frame 1000, with that frame's pts.
        calls.Clear();
        room = 440;
        Assert.Equal(440, feeder.Feed(Ring));
        Assert.Equal([(440 * 32, 208_333L), (480 * 32, 300_000L)], calls);
        Assert.Equal(1_440, feeder.FramesTaken);
        Assert.False(feeder.Closed);
    }

    [Fact]
    public void TheFeederStopsForGoodWhenTheOutputCloses()
    {
        var feeder = new DebugAudioOutFeeder(440);
        var calls = 0;
        uint Closed(ReadOnlySpan<byte> data, long pts)
        {
            calls++;
            return calls == 1 ? (uint)data.Length : BaleAudioOut.WriteClosed;
        }
        Assert.Equal(480, feeder.Feed(Closed));
        Assert.True(feeder.Closed);
        Assert.Equal(0, feeder.Feed(Closed));
        Assert.Equal(2, calls);
        // An answer shorter than a frame counts as full instead of spinning.
        var odd = new DebugAudioOutFeeder(440);
        Assert.Equal(0, odd.Feed((data, pts) => 31));
        Assert.Equal(1, odd.FullAnswers);
    }

    [Fact]
    public void ReportNamesHresultsStepsAndDrift()
    {
        Assert.Equal("S_OK", DebugAudioOutReport.Hr(0));
        Assert.Equal("AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED (0x8889000E)", DebugAudioOutReport.Hr(unchecked((int)0x8889000E)));
        Assert.Equal("AUDCLNT_E_DEVICE_IN_USE (0x8889000A)", DebugAudioOutReport.Hr(unchecked((int)0x8889000A)));
        Assert.Equal("AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED (0x88890019)", DebugAudioOutReport.Hr(unchecked((int)0x88890019)));
        Assert.Equal("E_HANDLE (0x80070006)", DebugAudioOutReport.Hr(unchecked((int)0x80070006)));
        Assert.Equal("0x888900FF", DebugAudioOutReport.Hr(unchecked((int)0x888900FF)));
        Assert.Equal("IsFormatSupported", DebugAudioOutReport.Step(6));
        Assert.Equal("device lost", DebugAudioOutReport.Status(BaleAudioOut.StatusDeviceLost));
        var refused = new BaleAudioOutOpenInfo
        {
            Step = 6,
            Hr = unchecked((int)0x8889000E),
            ActivateMs = 6,
            FormatHr = unchecked((int)0x8889000E),
        };
        Assert.Equal(
            "Create: AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED (0x8889000E) at IsFormatSupported; activation S_OK in 6 ms, Movie category S_OK, "
            + "IsFormatSupported AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED (0x8889000E)\n",
            DebugAudioOutReport.Open(refused.Hr, 0, refused));
        var badArguments = new BaleAudioOutOpenInfo { Step = 1, Hr = unchecked((int)0x80070057) };
        Assert.Equal("Create: E_INVALIDARG (0x80070057) at arguments\n", DebugAudioOutReport.Open(badArguments.Hr, 0, badArguments));
        var inUse = new BaleAudioOutOpenInfo { Step = 7, Hr = unchecked((int)0x8889000A), InitializeHr = unchecked((int)0x8889000A) };
        Assert.EndsWith(", IsFormatSupported S_OK, Initialize AUDCLNT_E_DEVICE_IN_USE (0x8889000A)\n",
            DebugAudioOutReport.Open(inUse.Hr, 0, inUse));
        var opened = new BaleAudioOutOpenInfo
        {
            DefaultPeriod100ns = 100_000,
            MinimumPeriod100ns = 30_000,
            Period100ns = 100_000,
            BufferFrames = 480,
            RingFrames = 24_000,
            Mmcss = 1,
        };
        Assert.Equal(
            "Create: S_OK id=1; activation S_OK in 0 ms, Movie category S_OK, IsFormatSupported S_OK, Initialize S_OK\n"
            + "Device: period default 10.000 ms, minimum 3.000 ms; initialized 10.000 ms, buffer 480 frames; ring 24000 frames (500 ms); "
            + "render thread MMCSS Pro Audio\n",
            DebugAudioOutReport.Open(0, 1, opened));
        // 1 s of QPC time over which the device clock advanced 1 s + 50 us: +50 ppm.
        var first = new BaleAudioOutStats { DevicePosition100ns = 10_000_000, DeviceQpc100ns = 500_000_000 };
        var last = new BaleAudioOutStats { DevicePosition100ns = 20_000_500, DeviceQpc100ns = 510_000_000 };
        Assert.Equal(50, DebugAudioOutReport.DriftPpm(first, last)!.Value, 6);
        Assert.Equal("+50.0 ppm", DebugAudioOutReport.Ppm(DebugAudioOutReport.DriftPpm(first, last)));
        Assert.Null(DebugAudioOutReport.DriftPpm(default, last));
        Assert.Null(DebugAudioOutReport.DriftPpm(first, first));
        Assert.Null(DebugAudioOutReport.DriftPpm(first, last with { Flushes = 1 }));
        Assert.Equal("unavailable", DebugAudioOutReport.Ppm(null));
        var played = DebugAudioOutReport.Summary(last with { Status = 1, Underruns = 2, SyncError100ns = -12_340 }, -3.04);
        Assert.Equal("Summary: played; status running, underruns 2, drift -3.0 ppm, sync error -1.234 ms, drops 0, pads 0\n", played);
        Assert.Equal("Summary: not played (E_HANDLE (0x80070006) at arguments)\n",
            DebugAudioOutReport.NotPlayed($"{DebugAudioOutReport.Hr(unchecked((int)0x80070006))} at {DebugAudioOutReport.Step(1)}"));
    }

    [Fact]
    public void RouteIsDebugOnlyGatedAndDocumented()
    {
        var server = Read("DebugServer.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", server);
        var start = server.IndexOf("case (\"GET\", \"/audio-out-test\"):", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var route = server[start..server.IndexOf("case (", start + 1, StringComparison.Ordinal)];
        Assert.Contains("return await AudioOutTestAsync(query);", route);
        Assert.Contains("///   GET  /audio-out-test?seconds=5&tone=440", server);
        var notFound = server.Split('\n').Single(line => line.Contains("\"routes: ", StringComparison.Ordinal));
        Assert.Contains("GET /audio-probe, GET /audio-out-test?seconds=5&tone=440,", notFound);
        Assert.Contains("or \"/audio-out-test\" or \"/webview-test\"))", server);

        var handler = Read("DebugServer.AudioOut.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", handler);
        var parse = handler.IndexOf("if (!DebugAudioOutOptions.TryParse(query, out var options))", StringComparison.Ordinal);
        var gate = handler.IndexOf("if (Interlocked.CompareExchange(ref _playtestBusy, 1, 0) != 0)", StringComparison.Ordinal);
        var player = handler.IndexOf("inPlayer = CurrentPage(out _) is PlayerPage;", StringComparison.Ordinal);
        var refuse = handler.IndexOf("if (inPlayer) return (\"409 Conflict\",", StringComparison.Ordinal);
        var run = handler.IndexOf("Task.Factory.StartNew(() => DebugAudioOutTest.Run(options),", StringComparison.Ordinal);
        Assert.True(parse >= 0 && gate > parse && player > gate && refuse > player && run > refuse);
        Assert.Contains("TaskCreationOptions.LongRunning", handler);
        Assert.Contains("finally { Interlocked.Exchange(ref _playtestBusy, 0); }", handler);
        Assert.Contains("Log.Info(\"Debug audio-out-test: \" + (summary is null ? \"no summary\" : summary[DebugAudioOutReport.SummaryPrefix.Length..]));", handler);
    }

    [Fact]
    public void TestLoadsTheModulesBreadcrumbsNativeCallsAndAlwaysDestroys()
    {
        var test = Read("DebugAudioOutTest.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", test);
        Assert.Contains("private static extern unsafe int bale_audio_out_create(nint mmdevapi, BaleAudioFormat* format, uint ringMs, uint* id, BaleAudioOutOpenInfo* info);", test);
        Assert.Contains("private static extern unsafe uint bale_audio_out_write(uint id, byte* data, uint bytes, long pts100ns);", test);
        Assert.Contains("private static extern int bale_audio_out_sync(uint id, long videoPosition100ns, long qpcTicks);", test);
        var load = test.IndexOf("NativeLibrary.TryLoad(\"mmdevapi.dll\", typeof(DebugAudioOutTest).Assembly, DllImportSearchPath.System32, out mmdevapi)", StringComparison.Ordinal);
        var avrt = test.IndexOf("NativeLibrary.TryLoad(\"avrt.dll\", typeof(DebugAudioOutTest).Assembly, DllImportSearchPath.System32, out avrt)", StringComparison.Ordinal);
        var breadcrumb = test.IndexOf("Log.Info(\"Debug audio-out-test: before bale_audio_out_create\");", StringComparison.Ordinal);
        var create = test.IndexOf("var hr = bale_audio_out_create(mmdevapi, &format, RingMs, &id, &info);", StringComparison.Ordinal);
        var destroyAt = test.IndexOf("finally", create, StringComparison.Ordinal);
        var destroy = test.IndexOf("bale_audio_out_destroy(id)", destroyAt, StringComparison.Ordinal);
        var free = test.IndexOf("if (mmdevapi != 0 && !borrowed) NativeLibrary.Free(mmdevapi);", StringComparison.Ordinal);
        Assert.True(load >= 0 && avrt > load && breadcrumb > avrt && create > breadcrumb && destroy > destroyAt && free > destroy);
        Assert.Contains("if (avrt != 0) NativeLibrary.Free(avrt);", test);
        // The tone plays off the UI thread, and exceptions are reported by type and HRESULT only.
        Assert.DoesNotContain("OnUiAsync", test);
        Assert.DoesNotContain("ex.Message", test);
        // Synced to the wall clock from Start: QPC ticks, as Stopwatch reads them.
        Assert.Contains("var sync = bale_audio_out_sync(id, elapsed * 10_000_000 / ticks, now);", test);
    }

    [Fact]
    public void ScriptAndPackagedDllChecksKnowTheOutput()
    {
        var script = Read("bale-debug.sh");
        Assert.Contains("#   scripts/bale-debug.sh audio-out-test [seconds] [freq] ", script);
        Assert.Contains("  audio-out-test)\n", script);
        var workflow = Read("ci.yml.txt");
        foreach (var export in new[] { "abi_version", "create", "write", "start", "pause", "flush", "sync", "stats", "format", "destroy" })
            Assert.Contains($"'bale_audio_out_{export}'", workflow);
        Assert.Contains("if ($dependents -match '(?i)avrt\\.dll') { throw 'bale_gpu.dll must not import avrt.dll' }", workflow);
    }

    private static int Offset<T>(string field) => Marshal.OffsetOf<T>(field).ToInt32();

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
