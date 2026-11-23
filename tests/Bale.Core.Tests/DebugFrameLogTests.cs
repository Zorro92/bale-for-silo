using System.Globalization;
using Bale.Core.Playback;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public sealed class DebugFrameLogTests
{
    [Fact]
    public void SwitchDefaultsToOffAndAcceptsOnlyOnOrOffInMemory()
    {
        var previous = DebugFrameLogSwitch.Mode;
        try
        {
            Assert.Equal("off", previous);
            Assert.False(DebugFrameLogSwitch.Enabled);
            Assert.True(DebugFrameLogSwitch.TrySet("on"));
            Assert.True(DebugFrameLogSwitch.Enabled);
            Assert.Equal("on", DebugFrameLogSwitch.Mode);
            foreach (var invalid in new string?[] { null, "", "ON", "On", "true", "1", " on", "off ", "native", "on&extra=1" })
            {
                Assert.False(DebugFrameLogSwitch.TrySet(invalid));
                Assert.Equal("on", DebugFrameLogSwitch.Mode);
            }
            Assert.True(DebugFrameLogSwitch.TrySet("off"));
            Assert.False(DebugFrameLogSwitch.Enabled);
        }
        finally { DebugFrameLogSwitch.TrySet(previous); }
        var source = Read("DebugFrameLogSwitch.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("private static string _mode = \"off\";", source);
        Assert.DoesNotContain("Settings", source);
        Assert.DoesNotContain("Storage", source);
    }

    [Fact]
    public void SampleLinesCarryQpcMillisecondsTicksAndLeadInMilliseconds()
    {
        // A 59.94 fps sample delivered one frame ahead of the player clock.
        Assert.Equal("frame: t=123456.789 pts=41708333 dur=166833 pos=41541500 lead=16.7 kind=1 flags=1",
            DebugFrameLog.Frame(new PlaybackFrameRecord(1, 1, 123456.78949, 41708333, 166833, 41541500)));
        // Late (pts behind the clock) and discontinuous.
        Assert.Equal("frame: t=1.000 pts=10000000 dur=166833 pos=10250000 lead=-25.0 kind=1 flags=3",
            DebugFrameLog.Frame(new PlaybackFrameRecord(1, 3, 1.0, 10000000, 166833, 10250000)));
        // Engine preroll before the clock was attached: no position, so no lead.
        Assert.Equal("frame: t=2.500 pts=417083 dur=166833 pos=- lead=- kind=1 flags=4",
            DebugFrameLog.Frame(new PlaybackFrameRecord(1, DebugFrameLog.NoClock, 2.5, 417083, 166833)));
        Assert.Equal("frame: t=2.500 pts=0 dur=0 pos=0 lead=0.0 kind=1 flags=0",
            DebugFrameLog.Frame(new PlaybackFrameRecord(1, 0, 2.5)));
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    public void MarkersHaveNoSampleValues(uint kind)
    {
        Assert.Equal($"frame: t=3.250 pts=- dur=- pos=- lead=- kind={kind} flags=0",
            DebugFrameLog.Frame(new PlaybackFrameRecord(kind, 0, 3.25)));
    }

    [Theory]
    [InlineData(0L, "0.0")]
    [InlineData(10000L, "1.0")]
    [InlineData(166833L, "16.7")]
    [InlineData(1499L, "0.1")]
    [InlineData(1500L, "0.2")]
    [InlineData(-1500L, "-0.2")]
    [InlineData(-499L, "0.0")]
    [InlineData(-500L, "-0.1")]
    [InlineData(-400L, "0.0")]
    [InlineData(long.MaxValue, "922337203685477.6")]
    [InlineData(long.MinValue, "-922337203685477.6")]
    public void LeadRoundsTicksHalfAwayFromZeroToTenthsOfAMillisecond(long ticks, string expected) =>
        Assert.Equal(expected, DebugFrameLog.Milliseconds(ticks));

    [Fact]
    public void EventLinesNameTheEngineEventAndItsPositionInTicks()
    {
        Assert.Equal("frame-event: t=5000.124 kind=waiting pos=12500000", DebugFrameLog.Event(5000.1236, 12, 1.25));
        Assert.Equal("frame-event: t=5000.124 kind=playing pos=41708333", DebugFrameLog.Event(5000.1236, 13, 4.1708333));
        // Before the session's frame log was on (the open's own events), bale_gpu sends NaN.
        Assert.Equal("frame-event: t=7.000 kind=pause pos=-", DebugFrameLog.Event(7, 9, double.NaN));
        Assert.Equal("frame-event: t=- kind=99 pos=-", DebugFrameLog.Event(double.NaN, 99, double.PositiveInfinity));
        var names = new Dictionary<uint, string>
        {
            [8] = "play", [9] = "pause", [12] = "waiting", [13] = "playing", [16] = "seeking", [17] = "seeked",
            [19] = "ended", [1005] = "bufferingstarted", [1006] = "bufferingended",
        };
        foreach (var (kind, name) in names) Assert.Equal(name, DebugFrameLog.EventName(kind));
    }

    [Fact]
    public void LinesIgnoreTheCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.Equal("frame: t=1234567.891 pts=-20000 dur=166833 pos=1234567 lead=-125.5 kind=1 flags=1",
                DebugFrameLog.Frame(new PlaybackFrameRecord(1, 1, 1234567.8909, -20000, 166833, 1234567)));
            Assert.Equal("frame-event: t=0.500 kind=seeked pos=-15000000", DebugFrameLog.Event(0.5, 17, -1.5));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void AttemptsCaptureTheSwitchAndBothBackendsWriteItsLines()
    {
        var player = Read("PlayerPage.cs.txt");
        const string capture = "public bool FrameLog { get; } = DebugFrameLogSwitch.Enabled;";
        var at = player.IndexOf(capture, StringComparison.Ordinal);
        Assert.True(at >= 0);
        Assert.True(player.LastIndexOf("#if DEBUG_SERVER", at, StringComparison.Ordinal)
            > player.LastIndexOf("#endif", at, StringComparison.Ordinal));
        Assert.DoesNotContain("DebugFrameLogSwitch", player.Replace(capture, "", StringComparison.Ordinal));
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.DoesNotContain("DebugFrameLogSwitch", media);
        Assert.Contains(": new NativeFrameDelivery(softwareSource, player.PlaybackSession, stream: media.Stream, frameLog: attempt.FrameLog);", media);
        // Engine: the observer subscribes before the open (preroll) and takes the engine clock after it.
        var open = media.IndexOf("var opened = await engine.OpenAsync(", StringComparison.Ordinal);
        var subscribe = media.IndexOf("AttachEngineFrameDelivery(attempt, media, engine);", StringComparison.Ordinal);
        var flag = media.IndexOf("engine.FrameLog = attempt.FrameLog;", StringComparison.Ordinal);
        var attach = media.IndexOf("frames.AttachEngine(engine);", StringComparison.Ordinal);
        Assert.True(flag >= 0 && subscribe > flag && open > subscribe && attach > open);
        foreach (var index in new[] { flag, subscribe, attach })
            Assert.True(media.LastIndexOf("#if DEBUG_SERVER", index, StringComparison.Ordinal)
                > media.LastIndexOf("#endif", index, StringComparison.Ordinal));
        Assert.Contains("new NativeFrameDelivery(softwareSource, engine.PlaybackSession, media.Stream, attempt.FrameLog)", media);
        Assert.Contains("if (opened && ReferenceEquals(attempt.Media, media) && media.FrameDelivery is NativeFrameDelivery frames)", media);
        Assert.Contains("frame delivery managed needs MediaPlayer's session", media);
        Assert.Contains("t = QPC ms, pts/dur/pos = 100 ns, lead = ms", media);
        foreach (var file in new[] { "NativeFrameDelivery.cs.txt", "EngineBackend.cs.txt" })
            Assert.DoesNotContain("DebugFrameLogSwitch", Read(file));
    }

    [Fact]
    public void SampleLinesAreWrittenInBatchesByTheDrainThreadOutsideTheGate()
    {
        var source = Read("NativeFrameDelivery.cs.txt");
        Assert.Equal(1, Count(source, "Log.Lines("));
        var drainCore = Slice(source, "private void DrainCore()", "private void WriteFrameLog()");
        Assert.DoesNotContain("Log.Lines(", drainCore);
        Assert.DoesNotContain("Log.Info(", drainCore);
        Assert.Contains("if (_frameLog.Count < FrameLogBacklog) _frameLog.Add(processed);", drainCore);
        Assert.Contains("else _frameLogDropped++;", drainCore);
        var write = Slice(source, "private void WriteFrameLog()", "private void ReadOutputDescriptor()");
        // Formatted under the gate, written after it.
        Assert.True(write.IndexOf("lock (_gate)", StringComparison.Ordinal) < write.IndexOf("DebugFrameLog.Frame(record)", StringComparison.Ordinal));
        Assert.True(write.LastIndexOf('}', write.IndexOf("Log.Lines(lines);", StringComparison.Ordinal)) > write.IndexOf("_frameLog.Clear();", StringComparison.Ordinal));
        Assert.Contains("frame-log: {_frameLogDropped} records dropped (the log writer fell behind)", write);
        var loop = Slice(source, "private void DrainLoop()", "private void DrainCore()");
        Assert.Contains("lastDrain = snapshot.NowMilliseconds;\n                }\n                WriteFrameLog();", loop);
        // The last deliveries (up to one drain interval) are drained and written before the observer goes.
        var final = loop.IndexOf("if (_frameLog is not null && Volatile.Read(ref _unavailable) == 0)", StringComparison.Ordinal);
        Assert.True(final >= 0 && final < loop.IndexOf("bale_frame_observer_destroy(_observer);", StringComparison.Ordinal));
        Assert.True(loop.LastIndexOf("WriteFrameLog();", StringComparison.Ordinal) > loop.IndexOf("bale_frame_observer_destroy(_observer);", StringComparison.Ordinal));
        Assert.Contains("private const int FrameLogBacklog = 16384;", source);
    }

    [Fact]
    public void EngineEventsCarryQpcTimeAndBecomeLinesAfterTheSessionLock()
    {
        var engine = Read("EngineBackend.cs.txt");
        Assert.Contains("if (FrameLog) (_frameEvents ??= []).Add(DebugFrameLog.Event(events[i].QpcMilliseconds, events[i].Kind, events[i].Position));", engine);
        var write = Slice(engine, "private void WriteFrameEvents()", "private void PollState()");
        Assert.True(write.IndexOf("_frameEvents = null;", StringComparison.Ordinal) < write.IndexOf("if (lines is { Count: > 0 }) Log.Lines(lines);", StringComparison.Ordinal));
        Assert.Contains("        PollState();\n#if DEBUG_SERVER\n        WriteFrameEvents();\n#endif", engine);
        Assert.Contains("var frameLogHr = Call(handle => bale_gpu_engine_session_frame_log(handle, 1));", engine);
        var import = engine.IndexOf("private static extern int bale_gpu_engine_session_frame_log(nint session, uint on);", StringComparison.Ordinal);
        Assert.True(import >= 0 && engine.LastIndexOf("#if DEBUG_SERVER", import, StringComparison.Ordinal)
            > engine.LastIndexOf("#endif", import, StringComparison.Ordinal));
        // engine_events::PlaybackEvent, 32 bytes (bale_gpu ABI 16).
        Assert.Contains("    public double Seconds;\n    public uint Kind;\n    public uint Reserved;\n", engine);
        Assert.True(engine.IndexOf("public double QpcMilliseconds;", StringComparison.Ordinal)
            < engine.IndexOf("public double Position;\n}", StringComparison.Ordinal));
        Assert.Contains("internal int WithSessionHandle(Func<nint, int> call) => Call(call);", engine);
    }

    [Fact]
    public void DebugLogsMoveToBale1InsteadOfDroppingLinesAndXboxLogsReadsIt()
    {
        var log = Read("Log.cs.txt");
        Assert.Contains("public static void Lines(IReadOnlyList<string> messages)", log);
        var rotate = log.IndexOf("File.Move(FilePath, PreviousPath, overwrite: true);", StringComparison.Ordinal);
        var delete = log.IndexOf("File.Delete(FilePath);", StringComparison.Ordinal);
        Assert.True(rotate >= 0 && delete > rotate);
        Assert.True(log.LastIndexOf("#if DEBUG_SERVER", rotate, StringComparison.Ordinal) > log.LastIndexOf("#endif", rotate, StringComparison.Ordinal));
        Assert.True(log.LastIndexOf("#else", delete, StringComparison.Ordinal) > rotate);
        Assert.Contains("log: bale.log passed {MaxBytes / 1024} KiB; earlier lines are in bale.1.log", log);
        Assert.Contains("A file in use (Device Portal reading it): append past the limit", log);
        var script = Read("xbox-logs.sh");
        Assert.Contains("[[ \"$FILE\" == bale.log || \"$FILE\" == bale.1.log ]]", script);
        Assert.Contains("--data-urlencode \"filename=$FILE\"", script);
    }

    [Fact]
    public void RoutesAndScriptExposeTheSwitch()
    {
        var routes = Read("DebugServer.cs.txt");
        var duplicateGuard = routes.Split('\n').Single(line => line.Contains("duplicateQueryKeys && path is", StringComparison.Ordinal));
        Assert.Contains("\"/frame-log\"", duplicateGuard);
        var get = Slice(routes, "case (\"GET\", \"/frame-log\")", "case (\"POST\", \"/frame-log\")");
        Assert.Contains("if (query.Count != 0)", get);
        Assert.Contains("frame-log={DebugFrameLogSwitch.Mode}\\n", get);
        Assert.DoesNotContain("TrySet", get);
        var post = Slice(routes, "case (\"POST\", \"/frame-log\")", "case (\"GET\", \"/start-budget\")");
        Assert.Contains("query.Count != 1 || !DebugFrameLogSwitch.TrySet(query.GetValueOrDefault(\"mode\"))", post);
        Assert.Contains("\"400 Bad Request\"", post);
        Assert.Contains("applies to the next playback attempt", post);
        Assert.Contains("///   GET  /frame-log", routes);
        Assert.Contains("///   POST /frame-log?mode=on", routes);
        var notFound = routes.Split('\n').Single(line => line.Contains("\"routes: ", StringComparison.Ordinal));
        Assert.Contains("GET /frame-log, POST /frame-log?mode={on|off}", notFound);
        var script = Read("bale-debug.sh");
        Assert.Contains("#   scripts/bale-debug.sh frame-log [on|off]", script);
        var handler = Slice(script, "  frame-log)\n", "  start-budget)\n");
        Assert.Contains("! \"$2\" =~ ^(on|off)$", handler);
        Assert.Contains("curl --fail-with-body -sS --max-time 5 \"$BASE/frame-log\"", handler);
        Assert.Contains("-X POST -G \"$BASE/frame-log\" --data-urlencode \"mode=$2\"", handler);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Slice(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, end);
        return text[from..to];
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
