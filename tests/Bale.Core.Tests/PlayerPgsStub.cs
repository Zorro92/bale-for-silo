using System.Collections.Concurrent;
using Bale.Core.Playback;
using Bale.Xbox.Services;

// Model only the native handle/overlay boundary. Production window fetching, cancellation,
// mounted-clock selection, seek and reanchor code is compiled into these tests unchanged.
namespace Bale.Xbox
{
    public sealed partial class PlayerPage
    {
        private const uint MaxSubtitleSinkToken = 999999999;
        private const int SubtitleFetchSeconds = 30;
        private uint _subtitleSinkToken;
        private int _subtitleGeneration;
        private double _subtitleTimingOrigin;
        private SubtitleOverlay? _subtitles;
        private readonly SemaphoreSlim _subtitleLoads = new(1, 1);
        private SubtitleOverlay EnsureSubtitleOverlay() => _subtitles ??= new();
        private void UpdateSubtitleLayout() { }
        internal List<long> SubtitleClocks { get; } = [];
        internal IReadOnlyList<PgsSubtitleRange> PgsCommits => _subtitles?.Commits ?? [];
        internal int PgsClears => _subtitles?.Clears ?? 0;
        internal ulong PgsBoundToken => _subtitles?.Token ?? 0;
        internal PgsSubtitleRange? PgsCoverage => _pgsRange;
        internal void BeginPgs() => StartPgsSubtitles(_attempt, _attempt.Media!, _attempt.Client.Playback.ResolveSubtitles(_attempt.Plan)!);
        internal void TickPgs() => PushSubtitleClock(true);
        internal async Task WaitPgsAsync()
        {
            async Task Settle() { while (_pgsFetch is not null) await Task.Yield(); }
            await Settle().WaitAsync(TimeSpan.FromSeconds(10));
        }
        internal void ClearPgsForTest()
        {
            _subtitleGeneration++;
            StopPgsSubtitles();
            _subtitles?.BindBitmaps(0);
        }
    }
}

namespace Bale.Xbox.Services
{
    internal sealed class SubtitleOverlay
    {
        internal static ConcurrentDictionary<ulong, SubtitleOverlay> Sinks { get; } = new();
        internal ulong Token { get; private set; }
        internal int Clears { get; private set; }
        internal List<PgsSubtitleRange> Commits { get; } = [];
        internal int BindBitmaps(ulong token)
        {
            if (Token != 0) Sinks.TryRemove(Token, out _);
            Token = token;
            if (token == 0) Clears++;
            if (token != 0) Sinks[token] = this;
            return 0;
        }
    }
    internal sealed class PgsSubtitleWindow(PgsSubtitleRange range) : IDisposable
    {
        internal static Action<PgsSubtitleRange>? BeforeDecode { get; set; }
        internal static Action<PgsSubtitleRange>? Disposed { get; set; }
        internal static PgsSubtitleWindow Decode(byte[] bytes, PgsSubtitleRange range, uint width, uint height)
        {
            BeforeDecode?.Invoke(range);
            return new(range);
        }
        internal int Bind(ulong token, long sourceMs)
        {
            if (!SubtitleOverlay.Sinks.TryGetValue(token, out var overlay)) return unchecked((int)0x80070490);
            overlay.Commits.Add(range);
            return 0;
        }
        public void Dispose() => Disposed?.Invoke(range);
    }
}
