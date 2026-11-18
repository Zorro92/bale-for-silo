using Bale.Xbox.Services;
using FFmpegInteropX;
using Windows.Media.Core;

namespace Bale.Core.Tests;

/// <summary>
/// The UI thread never calls FFmpegInteropX members that take the source lock during playback (the fork's bale/README.md,
/// "rav1g prewarm and threading notes"; rav1g froze the UI thread in CurrentVideoStream on 2026-11-18).
/// </summary>
[Collection("Player seek")]
public sealed class FfmpegUiThreadTests
{
    private static readonly string[] LockingMembers =
    [
        ".CurrentVideoStream", ".CurrentAudioStream", ".MetadataTags", ".PlaybackItem", ".GetMediaStreamSource(",
        ".CreateMediaPlaybackItem(", ".StartBuffering(", ".ExtractThumbnail(",
    ];

    private static string Read(string file) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, file));

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        var to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, end);
        return text[from..to];
    }

    private static FFmpegMediaSource Source()
    {
        var source = new FFmpegMediaSource();
        source.VideoStreams.Add(new()
        {
            StreamIndex = 0, CodecName = "av1", DecoderEngine = DecoderEngine.FFmpegD3D11HardwareDecoder,
            HardwareDecoderStatus = HardwareDecoderStatus.Available, PixelWidth = 3840, PixelHeight = 2160, FramesPerSecond = 23.976,
        });
        source.AudioStreams.Add(new() { StreamIndex = 1, CodecName = "eac3", DecoderEngine = DecoderEngine.SystemDecoder, Channels = 6, SampleRate = 48000 });
        return source;
    }

    [Theory]
    [InlineData("PlayerPage.HudStats.cs.txt")]
    [InlineData("PlayerPage.Stats.cs.txt")]
    [InlineData("PlayerPage.Tracks.cs.txt")]
    [InlineData("PlayerPage.Options.cs.txt")]
    [InlineData("PlayerPage.Subtitles.cs.txt")]
    [InlineData("PlayerPage.StartBudget.cs.txt")]
    [InlineData("PlayerPage.BufferingQuality.cs.txt")]
    [InlineData("PlayerPage.cs.txt")]
    public void UiThreadPlayerCodeNeverCallsTheWrappersLockingMembers(string file)
    {
        var text = Read(file);
        foreach (var member in LockingMembers) Assert.DoesNotContain(member, text);
    }

    [Fact]
    public void MediaTypeDiagnosticTakesItsStreamFromTheSnapshot()
    {
        // Its constructor's GetMediaStreamSource runs at open; Refresh runs on the UI tick and must not read the wrapper.
        var text = Read("DebugVideoMediaType.cs.txt");
        Assert.DoesNotContain(".CurrentVideoStream", text);
        Assert.DoesNotContain(".CurrentAudioStream", text);
        Assert.Contains("var video = _video();", text);
    }

    [Fact]
    public void TheMountReadsStreamsOnlyAtOpenAndHandsMediaPlayersSessionOverBeforeSource()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        Assert.DoesNotContain(".CurrentVideoStream", media);
        Assert.DoesNotContain(".CurrentAudioStream", media);
        // The open-time read: after creation, before the MediaStreamSource (and so any sample thread) exists.
        var snapshot = media.IndexOf("media.FfmpegStreams = FfmpegStreamSnapshot.Open(media.FfmpegSource);", StringComparison.Ordinal);
        Assert.True(snapshot > 0);
        Assert.True(snapshot < media.IndexOf("media.Item = media.FfmpegSource.CreateMediaPlaybackItem();", StringComparison.Ordinal));
        Assert.Contains("media.Rav1gActive = media.FfmpegStreams?.Value?.Video?.DecoderEngine", media);
        // PlaybackSession's setter takes the source lock: before Source, never in MediaOpened.
        var session = media.IndexOf("sessionSource.PlaybackSession = player.PlaybackSession;", StringComparison.Ordinal);
        Assert.True(session > 0 && session < media.IndexOf("player.Source = media.Item;", StringComparison.Ordinal));
        var opened = Between(media, "private void OnMediaOpened(", "private void SelectOriginalAudioTrack(");
        Assert.DoesNotContain(".PlaybackSession = ", opened.Replace("sender.PlaybackSession.Position = ", "", StringComparison.Ordinal));
        foreach (var member in LockingMembers) Assert.DoesNotContain(member, opened);
        Assert.Contains("media.FfmpegStreams?.RequestRefresh();", opened);
    }

    [Fact]
    public void MountTeardownClosesNativeSourcesOnThePoolAndTheNextOpenWaitsWithoutBlocking()
    {
        var media = Read("PlayerPage.Media.cs.txt");
        var dispose = Between(media, "private void DisposeMount(Attempt attempt)", "private Task _mountClose");
        var close = Between(dispose, "_mountClose = _mountClose.ContinueWith(_ =>", "TaskScheduler.Default);");
        // Same order as before, now off the UI thread.
        string[] order = ["itemSource?.Dispose()", "source?.Dispose()", "ffmpeg?.Dispose()", "random?.Dispose()",
            "forward?.Dispose()", "stream?.Dispose()", "progressive?.Dispose()", "PlaybackResources.ReleaseMediaDevice(rendererDevice);",
            "cancellation.Dispose"];
        var at = 0;
        foreach (var step in order)
        {
            var next = close.IndexOf(step, at, StringComparison.Ordinal);
            Assert.True(next >= at, step);
            at = next;
        }
        var outside = dispose.Replace(close, "", StringComparison.Ordinal);
        Assert.DoesNotContain("FfmpegSource?.Dispose()", outside);
        Assert.DoesNotContain("media.Cancellation.Dispose()", outside);
        Assert.Contains("media.FfmpegStreams?.Close();", outside);
        Assert.Contains("await WaitForMountCloseAsync(\"mount\");", Between(media, "private async Task MountPlanAsync(", "ConsoleToneMapPolicy.RequiresToneMap("));
        Assert.Contains("await Task.WhenAny(close, Task.Delay(MountCloseWaitMs))", media);
        var shutdown = Between(Read("PlayerPage.cs.txt"), "private async Task ShutdownCoreAsync()", "private async void OnSuspending(");
        Assert.True(shutdown.IndexOf("await WaitForMountCloseAsync(\"shutdown\");", StringComparison.Ordinal)
            < shutdown.IndexOf("PlaybackResources.ReleaseIdle(\"teardown\")", StringComparison.Ordinal));
    }

    [Fact]
    public void SnapshotReadsTheWrapperOnceAtOpenAndLaterOnlyOnTheScheduledRead()
    {
        var source = Source();
        var queued = new List<Action>();
        var streams = FfmpegStreamSnapshot.Open(source, queued.Add);
        Assert.Equal(1, source.CurrentStreamReads);
        Assert.Equal(new FfmpegVideoSnapshot(0, "av1", DecoderEngine.FFmpegD3D11HardwareDecoder, HardwareDecoderStatus.Available,
            3840, 2160, 23.976), streams.Value!.Video);
        Assert.Equal(new FfmpegAudioSnapshot(1, "eac3", DecoderEngine.SystemDecoder, 6, 48000), streams.Value.Audio);
        // The UI tick asks; the wrapper is untouched until the pool runs the read.
        source.VideoStreams[0].DecoderEngine = DecoderEngine.FFmpegSoftwareDecoder;
        streams.RequestRefresh();
        Assert.Equal(1, source.CurrentStreamReads);
        Assert.Equal(DecoderEngine.FFmpegD3D11HardwareDecoder, streams.Value.Video!.DecoderEngine);
        Assert.Single(queued)();
        Assert.Equal(2, source.CurrentStreamReads);
        Assert.Equal(DecoderEngine.FFmpegSoftwareDecoder, streams.Value!.Video!.DecoderEngine);
        streams.Close();
        streams.RequestRefresh();
        Assert.Single(queued);
    }

    [Fact]
    public void MediaTypeDiagnosticRefreshesFromTheSnapshotNotTheWrapper()
    {
        var source = Source();
        var queued = new List<Action>();
        var streams = FfmpegStreamSnapshot.Open(source, queued.Add);
        using var observer = new DebugVideoMediaType(source, VideoDecoderMode.ForceFFmpegSoftwareDecoder, () => streams.Value?.Video);
        source.Source.Request(new VideoStreamDescriptor { EncodingProperties = new() { Subtype = "P010", Width = 3840, Height = 2160 } });
        observer.Refresh(playbackStarted: true);
        Assert.Contains("decoder=ffmpeg-d3d11", observer.Description);
        Assert.Equal(1, source.CurrentStreamReads);
    }
}
