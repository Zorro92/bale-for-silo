using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>What the player page knows about playback at one moment (<see cref="BufferingSignals.Update"/>).</summary>
public readonly record struct BufferingSnapshot
{
    /// <summary>A mount is open (its source loaded in the backend).</summary>
    public bool HasMount { get; init; }
    /// <summary>The viewer's transport intent: play, not pause. During a remount, the intent the remount opens with.</summary>
    public bool WantsToPlay { get; init; }
    /// <summary>The backend reports Playing and the clock has moved since the mount opened.</summary>
    public bool Playing { get; init; }
    /// <summary>A seek, skip, scrub commit or seek reanchor has not landed yet.</summary>
    public bool SeekInFlight { get; init; }
    /// <summary>The media ended.</summary>
    public bool ReachedEnd { get; init; }
    /// <summary>No media bytes arrived for the plan's silence limit (<see cref="MediaSilence"/>).</summary>
    public bool UpstreamSilent { get; init; }
}

/// <summary>One controller call the snapshot asks for.</summary>
public enum BufferingSignal { TroubleStarted, TroubleEnded, Playing, WaitingNetwork, WaitingSeek, WaitingReload, Paused }

/// <summary>
/// Turns the page's playback facts into <see cref="BufferingQualityController"/>'s media events, so only the network's
/// own buffering can count as a stall (the spec's "Stall" term and its exclusions). A port of silo-apple's
/// BufferingSignalMapper.swift for Bale's backends (IMFMediaEngine and MediaPlayer report Playing / Buffering /
/// Paused / Opening; both raise BufferingStarted/Ended with the same meaning). A repeated state returns nothing, and a
/// change of transport trouble comes before the media call it accompanies.
/// <para>
/// Not ported: renderer waits. Neither backend reports how much media is buffered past the playhead, so a wait with
/// ample media buffered (a decoder behind, not the network) is still a network wait; the spec's AVPlayer route has the
/// same gap. Offline detection is left to the silence rule.
/// </para>
/// <para>
/// The mapping lives in Rust (native/bale-core/src/buffering.rs). The page updates it every UI tick, so it crosses the
/// handle ABI as bits and packed calls, without JSON, and an update with nothing to report allocates nothing.
/// </para>
/// </summary>
public sealed class BufferingSignals
{
    private readonly SignalsHandle _handle;

    public BufferingSignals()
    {
        BaleCoreNative.EnsureLoaded();
        _handle = BaleCoreBuffering.bale_core_signals_create();
    }

    /// <summary>The adopted plan has run since its adoption.</summary>
    public bool PlanHasRun => BaleCoreBuffering.bale_core_signals_plan_has_run(_handle) != 0;

    /// <summary>A media request failed (a broken progressive body, a failed segment): an outage begins.</summary>
    public void NoteMediaRequestFailed() => BaleCoreBuffering.bale_core_signals_note_request_failed(_handle);

    /// <summary>The plan's media received bytes: an outage ends once nothing else holds it.</summary>
    public void NoteMediaDataArrived() => BaleCoreBuffering.bale_core_signals_note_data_arrived(_handle);

    /// <summary>Forgets the state last reported, so the next update reports it again.</summary>
    public void Invalidate() => BaleCoreBuffering.bale_core_signals_invalidate(_handle);

    /// <summary>A plan was adopted: its start is timed until it first runs or pauses, and it starts with no outage.</summary>
    public void PlanAdopted() => BaleCoreBuffering.bale_core_signals_plan_adopted(_handle);

    public IReadOnlyList<BufferingSignal> Update(BufferingSnapshot snapshot)
    {
        var bits = (snapshot.HasMount ? 1u : 0) | (snapshot.WantsToPlay ? 2u : 0) | (snapshot.Playing ? 4u : 0)
            | (snapshot.SeekInFlight ? 8u : 0) | (snapshot.ReachedEnd ? 16u : 0) | (snapshot.UpstreamSilent ? 32u : 0);
        // One call per byte from the lowest, as its BufferingSignal value + 1; 0 ends the list.
        var packed = BaleCoreBuffering.bale_core_signals_update(_handle, bits);
        if (packed == 0) return [];
        var first = (BufferingSignal)((packed & 0xFF) - 1);
        var second = (packed >> 8) & 0xFF;
        return second == 0 ? [first] : [first, (BufferingSignal)(second - 1)];
    }

    /// <summary>Feeds <paramref name="calls"/> to <paramref name="controller"/>.</summary>
    public static void Apply(BufferingQualityController controller, IEnumerable<BufferingSignal> calls)
    {
        foreach (var call in calls)
        {
            switch (call)
            {
                case BufferingSignal.TroubleStarted: controller.TransportTroubleChanged(true); break;
                case BufferingSignal.TroubleEnded: controller.TransportTroubleChanged(false); break;
                case BufferingSignal.Playing: controller.Playing(); break;
                case BufferingSignal.WaitingNetwork: controller.Waiting(BufferingWaitCause.Network); break;
                case BufferingSignal.WaitingSeek: controller.Waiting(BufferingWaitCause.Seek); break;
                case BufferingSignal.WaitingReload: controller.Waiting(BufferingWaitCause.Reload); break;
                case BufferingSignal.Paused: controller.Paused(); break;
            }
        }
    }
}

/// <summary>
/// When the plan's media last received bytes, for the outage rule: OUTAGE_NO_DATA_MS = 4000 on progressive and
/// direct delivery, OUTAGE_NO_DATA_SEGMENTED_MS = 15000 on HLS, where the server holds a segment request silent while
/// it produces the segment.
/// </summary>
public sealed class MediaSilence
{
    public const long OutageNoDataMs = 4_000;
    public const long OutageNoDataSegmentedMs = 15_000;

    private object? _source;
    private long _bytes;
    private long? _lastDataAt;

    /// <summary>A new plan or mount: silence counts from <paramref name="nowMs"/>.</summary>
    public void Reset(long nowMs)
    {
        _source = null;
        _bytes = 0;
        _lastDataAt = nowMs;
    }

    /// <summary>Reads <paramref name="source"/>'s cumulative byte count; true when bytes arrived since the last read.</summary>
    public bool Observe(object source, long downloadedBytes, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ReferenceEquals(source, _source))
        {
            _source = source;
            _bytes = 0;
        }
        if (downloadedBytes == _bytes) return false;
        var arrived = downloadedBytes > _bytes;
        _bytes = downloadedBytes;
        if (arrived) _lastDataAt = nowMs;
        return arrived;
    }

    public bool Silent(long nowMs, bool segmented) =>
        _lastDataAt is { } last && nowMs - last >= (segmented ? OutageNoDataSegmentedMs : OutageNoDataMs);
}
