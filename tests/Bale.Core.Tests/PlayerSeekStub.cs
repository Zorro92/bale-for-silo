using Bale.Core;
using Bale.Core.Api.Models;
using Bale.Core.Playback;
using Bale.Xbox.Services;
using Windows.Media.Playback;
using Windows.System;

// Exercise PlayerPage.Seek.cs and PlayerPage.Debug.cs themselves on Linux. This models mount/session callbacks, not UWP's
// HLS parser, decoder, or live-edge behavior. An unproven native seek fails instead of inventing
// a successful landing; only the server reanchor can make such a target available.
namespace Bale.Xbox
{
    public sealed partial class PlayerPage : IDisposable
    {
        private readonly SemaphoreSlim _lifecycle = new(1, 1);
        private readonly bool _active = true;
        private readonly bool _leaving = false, _suspended = false;
        private readonly Attempt _attempt;
        private readonly double? _duration;
        private double _sourcePosition;
        private string _state = "playing";

        private sealed class Attempt
        {
            public required SiloClient Client { get; init; }
            public required PlaybackStartRequestV3 Start { get; init; }
            public required PlaybackPlan Plan { get; set; }
            public IReadOnlyList<string> ServerFeatures { get; set; } = [PlaybackSeekReanchor.Feature];
            public IReadOnlyList<string> AttemptedPlanKeys { get; } = [];
            public string PlanAttemptId { get; } = "mount-0123456789";
            public string SessionId { get; } = "session";
            public bool Attached { get; } = true;
            public bool Replanned { get; } = false;
            public double Position { get; set; }
            public CancellationTokenSource Cancellation { get; } = new();
            public required SeekServices Services { get; init; }
            public Mount? Media { get; set; }
        }

        private sealed record SeekServices(IPlaybackInstallationId Installation, PlaybackProgressReporter Reporter);
        private sealed class Mount
        {
            public Mount(PlaybackMedia transport, MediaPlaybackSession session)
            {
                Transport = transport;
                Player = new(session);
                Token = Cancellation.Token;
            }
            public PlaybackMedia Transport { get; }
            public MediaPlayer Player { get; }
            public bool Opened { get; set; } = true;
            public bool Started { get; set; } = true;
            public bool FailureQueued { get; } = false;
            public CancellationTokenSource Cancellation { get; } = new();
            public CancellationToken Token { get; }
            public TaskCompletionSource<bool> Opening { get; } = Ready();
            public SeekFrameDelivery? FrameDelivery => null;
            private static TaskCompletionSource<bool> Ready()
            {
                var ready = new TaskCompletionSource<bool>();
                ready.SetResult(true);
                return ready;
            }
        }
        private sealed class SeekFrameDelivery { public void SetSeeking(bool seeking) { } }

        internal PlayerPage(SiloClient client, PlaybackStartRequestV3 start, PlaybackPlan plan,
            IPlaybackInstallationId installation, PlaybackProgressReporter reporter, MediaPlaybackSession session)
        {
            _duration = plan.Source?.DurationSeconds;
            _attempt = new()
            {
                Client = client, Start = start, Plan = plan, Services = new(installation, reporter),
                Media = new(client.Playback.OpenMedia(plan), session),
            };
        }

        internal List<(PlaybackMedia Transport, double PlayerStart, bool Paused)> Mounts { get; } = [];
        internal Func<PlaybackPlan, double, MediaPlaybackSession> OpenSession { get; set; } =
            (_, start) => new(start) { CanSeek = true, Ranges = [(0, 20)] };
        internal MediaPlaybackSession Session => _attempt.Media!.Player.PlaybackSession;
        internal double Source => SourcePosition(_attempt);
        internal bool IsPaused => Session.PlaybackState == MediaPlaybackState.Paused;
        internal string State => _state;
        internal bool IsSeeking => _seeking;
        internal SemaphoreSlim Lifecycle => _lifecycle;
        internal int StateUpdates { get; private set; }
        internal void SetMountReadiness(bool opened, bool started)
        {
            _attempt.Media!.Opened = opened;
            _attempt.Media.Started = started;
        }
        internal void Unmount() => DisposeMount(_attempt);
        internal async Task<string> SeekSourceAsync(double target)
        {
            var result = await SeekAsync(new(target));
            return result.Outcome.ToString();
        }

        internal async Task SkipAsync(bool forward)
        {
            HandleSkipKey(forward ? VirtualKey.GamepadRightTrigger : VirtualKey.GamepadLeftTrigger, firstDown: true);
            await WaitForSkipAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        private async Task WaitForSkipAsync() { while (_skipProcessing) await Task.Yield(); }

        private bool IsCurrent(Attempt attempt, Mount media) => ReferenceEquals(_attempt, attempt) && ReferenceEquals(attempt.Media, media);
        private void Pause(Attempt attempt) => attempt.Media!.Player.Pause();
        private void DisposeMount(Attempt attempt)
        {
            ClearPgsForTest();
            attempt.Media!.Cancellation.Cancel();
            attempt.Media.Cancellation.Dispose();
            attempt.Media = null;
        }
        private Task MountPlanAsync(Attempt attempt, double start, bool paused)
        {
            var transport = attempt.Client.Playback.OpenMedia(attempt.Plan);
            var session = OpenSession(attempt.Plan, start);
            if (paused) session.PlaybackState = MediaPlaybackState.Paused;
            attempt.Media = new(transport, session);
            Mounts.Add((transport, start, paused));
            if (attempt.Client.Playback.ResolveSubtitles(attempt.Plan) is { IsBitmap: true } source)
                StartPgsSubtitles(attempt, attempt.Media, source);
            return Task.CompletedTask;
        }
        private void ShowSkipFeedback(int delta, double target) { }
        private void HideTransport() { }
        private void SetLoading(bool loading) { }
        private void UpdatePlaybackState(Attempt attempt, Mount media)
        {
            StateUpdates++;
            _state = media.Player.PlaybackSession.PlaybackState.ToString().ToLowerInvariant();
        }
        private void RefreshStats(Attempt attempt) { }
        private void PushSubtitleClock(bool force)
        {
            var source = SourcePosition(_attempt);
            RefreshPgsSubtitles(source);
            SubtitleClocks.Add(SubtitleClockPush.Milliseconds(source - _subtitleTimingOrigin));
        }
        private static void LogFailure(string operation, Exception error) => Log.Info($"{operation}: {error.GetType().Name}");
        public void Dispose()
        {
            if (_attempt.Media is not null) DisposeMount(_attempt);
            _attempt.Cancellation.Dispose();
            _lifecycle.Dispose();
        }
    }
}

namespace Windows.System
{
    internal enum VirtualKey { GamepadLeftTrigger, GamepadRightTrigger, J, L }
}

namespace Windows.Media.Playback
{
    internal enum MediaPlaybackState { None, Opening, Buffering, Playing, Paused }
    internal sealed class MediaPlayer(MediaPlaybackSession session)
    {
        public MediaPlaybackSession PlaybackSession { get; } = session;
        public void Pause() => PlaybackSession.RequestState(MediaPlaybackState.Paused);
        public void Play() => PlaybackSession.RequestState(MediaPlaybackState.Playing);
    }
    internal sealed class MediaPlaybackSession(double position)
    {
        private TimeSpan _position = TimeSpan.FromSeconds(position);
        public bool CanSeek { get; init; }
        public TimeSpan NaturalDuration { get; init; }
        internal IReadOnlyList<(double Start, double End)> Ranges { get; init; } = [];
        // A complete VOD plan can authorize seeks without native ranges; growing HLS cannot.
        internal bool CompleteVod { get; init; }
        internal int UnprovenSeeks { get; private set; }
        internal int NativeSeeks { get; private set; }
        // Reproduce the console: a nominally in-range EVENT seek lands at the produced edge.
        internal double? LiveEdge { get; init; }
        private MediaPlaybackState _state = MediaPlaybackState.Playing;
        public MediaPlaybackState PlaybackState
        {
            get => _state;
            set { _state = value; PlaybackStateChanged?.Invoke(this, new object()); }
        }
        public event Action<MediaPlaybackSession, object>? PlaybackStateChanged;
        internal int StateSubscribers => PlaybackStateChanged?.GetInvocationList().Length ?? 0;
        internal Action<MediaPlaybackState>? OnStateRequested { get; set; }
        internal int StateRequests { get; private set; }
        internal void RequestState(MediaPlaybackState state)
        {
            StateRequests++;
            if (OnStateRequested is { } handler) handler(state);
            else PlaybackState = state;
        }
        public event Action<MediaPlaybackSession, object>? SeekCompleted;
        public TimeSpan Position
        {
            get => _position;
            set
            {
                NativeSeeks++;
                if (!CanSeek || !CompleteVod && !Ranges.Any(range => range.Start <= value.TotalSeconds && value.TotalSeconds <= range.End))
                {
                    UnprovenSeeks++;
                    throw new InvalidOperationException("Native seek outside the proven window.");
                }
                _position = LiveEdge is { } edge ? TimeSpan.FromSeconds(edge) : value;
                SeekCompleted?.Invoke(this, new object());
            }
        }
        public IReadOnlyList<MediaTimeRange> GetSeekableRanges() => Ranges
            .Select(range => new MediaTimeRange(TimeSpan.FromSeconds(range.Start), TimeSpan.FromSeconds(range.End))).ToArray();
    }
    internal sealed record MediaTimeRange(TimeSpan Start, TimeSpan End);
}
