namespace Bale.Core.Tests;

// Source/interop guards only; the Windows build compiles the projection and the Rust tests check ABI layout.
public class NativeFrameDeliveryTests
{
    [Fact]
    public void NativeBackendHasNoManagedSampleEventsOrForcedCollections()
    {
        var source = Read("NativeFrameDelivery.cs.txt");
        Assert.StartsWith("#if DEBUG_SERVER", source);
        Assert.Contains("partial class NativeFrameDelivery : IDebugFrameDelivery", source);
        Assert.Contains("partial class DebugFrameDelivery : IDebugFrameDelivery", Read("DebugFrameDelivery.cs.txt"));
        Assert.StartsWith("#if DEBUG_SERVER", Read("IDebugFrameDelivery.cs.txt"));
        Assert.Contains("private const uint ExpectedAbi = 5;", source);
        Assert.Contains("bale_gpu_abi_version() != ExpectedAbi", source);
        Assert.Contains("[StructLayout(LayoutKind.Sequential)]", source);
        foreach (var field in new[] { "uint Kind", "uint Flags", "double NowMilliseconds", "long Timestamp", "long Duration", "long Position" })
            Assert.Contains("public " + field + ";", source);
        Assert.Contains("((WinRT.IWinRTObject)_source).NativeObject.ThisPtr", source);
        Assert.Contains("((WinRT.IWinRTObject)_session).NativeObject.ThisPtr", source);
        Assert.DoesNotContain(".SampleRequested", source);
        Assert.DoesNotContain("GC.Collect(", source);
        Assert.DoesNotContain("Environment.TickCount", source);
        Assert.DoesNotContain("ex.Message", source);
    }

    [Fact]
    public void DrainThreadUsesPreallocatedBuffersAndTheQpcClockForRead()
    {
        var source = Read("NativeFrameDelivery.cs.txt");
        Assert.Contains("FrameRecord[] _records = new FrameRecord[DrainCapacity]", source);
        Assert.Contains("IsBackground = true, Name = \"bale-frame-drain\"", source);
        Assert.Contains("_stop.WaitOne(25)", source);
        Assert.Contains("snapshot.NowMilliseconds - lastDrain < 250", source);
        Assert.Contains("fixed (FrameRecord* records = _records)", source);
        Assert.Contains("while (count == DrainCapacity)", source);
        Assert.Contains("_processor.Read(_session.Position, (long)NowMilliseconds)", source);
        Assert.Contains("Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)", source);
        Assert.Contains("seeking ? 3u : 4u", source);
        Assert.Contains("bale_frame_observer_status(_observer)", source);
        Assert.Contains("if (dropped != 0) { Unavailable(FailureHResult); return; }", source);
        Assert.Contains("Interlocked.Exchange(ref _unavailable, 1)", source);
        Assert.Contains("native frame delivery unavailable hr=0x", source);
    }

    [Fact]
    public void DescriptorIsProjectedOnceAndTheWorkerOwnsBoundedShutdownCleanup()
    {
        var source = Read("NativeFrameDelivery.cs.txt");
        Assert.Contains("record.Kind == 1 && !_descriptorRead", source);
        Assert.Contains("_descriptorRead = true;", source);
        Assert.Contains("bale_frame_observer_video_descriptor(_observer)", source);
        Assert.Contains("WinRT.MarshalInspectable<object>.FromAbi(pointer)", source);
        Assert.Contains("finally { if (pointer != 0) Marshal.Release(pointer); }", source);
        Assert.Contains("Interlocked.Exchange(ref _disposed, 1)", source);
        Assert.Contains("_stop.Set()", source);
        Assert.Contains("_thread.Join(1000)", source);
        Assert.Contains("bale_frame_observer_destroy(_observer)", source);
        Assert.Contains("GC.KeepAlive(_source)", source);
        Assert.Contains("GC.KeepAlive(_session)", source);
    }

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));
}
