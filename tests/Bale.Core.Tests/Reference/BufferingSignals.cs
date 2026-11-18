using Bale.Core.Playback;

// The C# originals, before the Rust port (docs/rust-port.md), for differential tests only.
namespace Bale.Core.Tests.Reference;

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
/// </summary>
internal sealed class BufferingSignals
{
    private BufferingSignal? _state;
    private bool _trouble;
    private bool _seekSincePlaying;
    private bool _startPending;
    private bool _planHasRun = true;
    private bool _mediaRequestFailed;
    private bool _mediaDataArrived;
    private bool _outageAwaitsData;

    /// <summary>The adopted plan has run since its adoption.</summary>
    public bool PlanHasRun => _planHasRun;

    /// <summary>A media request failed (a broken progressive body, a failed segment): an outage begins.</summary>
    public void NoteMediaRequestFailed() => _mediaRequestFailed = true;

    /// <summary>The plan's media received bytes: an outage ends once nothing else holds it.</summary>
    public void NoteMediaDataArrived() => _mediaDataArrived = true;

    /// <summary>Forgets the state last reported, so the next update reports it again.</summary>
    public void Invalidate() => _state = null;

    /// <summary>A plan was adopted: its start is timed until it first runs or pauses, and it starts with no outage.</summary>
    public void PlanAdopted()
    {
        _state = null;
        _startPending = true;
        _planHasRun = false;
        _seekSincePlaying = false;
        _outageAwaitsData = false;
        _mediaRequestFailed = false;
        _mediaDataArrived = false;
    }

    public IReadOnlyList<BufferingSignal> Update(BufferingSnapshot snapshot)
    {
        var calls = new List<BufferingSignal>(2);
        if (_planHasRun && snapshot.SeekInFlight) _seekSincePlaying = true;
        if (!snapshot.WantsToPlay) _startPending = false;
        var next = MediaState(snapshot);
        // Buffering after a seek stays the seek's until playback runs, even once the seek has landed.
        if (next == BufferingSignal.WaitingNetwork && _seekSincePlaying) next = BufferingSignal.WaitingSeek;
        if (next == BufferingSignal.Playing)
        {
            _seekSincePlaying = false;
            _startPending = false;
            _planHasRun = true;
        }
        var trouble = OutageGoesOn(snapshot, next);
        if (trouble != _trouble)
        {
            _trouble = trouble;
            calls.Add(trouble ? BufferingSignal.TroubleStarted : BufferingSignal.TroubleEnded);
        }
        if (next == _state) return calls;
        _state = next;
        calls.Add(next);
        return calls;
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

    /// <summary>
    /// An outage begins when a media request fails or playback waits while the origin has sent nothing for the
    /// silence limit. It lasts until the plan's media data arrives again or playback runs; no timer ends it.
    /// </summary>
    private bool OutageGoesOn(BufferingSnapshot snapshot, BufferingSignal next)
    {
        var dataArrived = _mediaDataArrived;
        _mediaDataArrived = false;
        var silentWait = snapshot.UpstreamSilent && next == BufferingSignal.WaitingNetwork;
        if (_mediaRequestFailed || silentWait)
        {
            _mediaRequestFailed = false;
            _outageAwaitsData = true;
            return true;
        }
        if (!_outageAwaitsData) return false;
        if (next == BufferingSignal.Playing || dataArrived)
        {
            _outageAwaitsData = false;
            return false;
        }
        return true;
    }

    /// <summary>A reload's wait, or before the plan first runs or pauses, the start's.</summary>
    private BufferingSignal ReloadWait => _startPending ? BufferingSignal.WaitingNetwork : BufferingSignal.WaitingReload;

    private BufferingSignal MediaState(BufferingSnapshot s)
    {
        if (!s.HasMount) return s.WantsToPlay ? ReloadWait : BufferingSignal.Paused;
        if (s.ReachedEnd || !s.WantsToPlay) return BufferingSignal.Paused;
        if (s.SeekInFlight) return _planHasRun ? BufferingSignal.WaitingSeek : BufferingSignal.WaitingNetwork;
        if (s.Playing) return BufferingSignal.Playing;
        // Opening, Buffering, or Playing with a clock that has not moved yet: the plan waits for media.
        return BufferingSignal.WaitingNetwork;
    }
}
