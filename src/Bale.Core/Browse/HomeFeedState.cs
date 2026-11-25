using Bale.Core.Settings;

namespace Bale.Core.Browse;

public sealed record HomeLoadRequest(int Generation, bool Quiet)
{
    /// <summary>How many retries of a transiently failed quiet read came before this one (0: a first read).</summary>
    public int Retry { get; init; }
}

/// <summary>A quiet read that failed transiently is read again <see cref="Delay"/> later, as retry <see cref="Retry"/>.</summary>
public sealed record HomeRetry(int Retry, TimeSpan Delay);

/// <summary>
/// TvHomeScreen.kt / HomeViewModel.kt: one initial load, quiet single-flight resume reads,
/// hidden reads deferred, and Detail-return refresh held until the shell restores focus.
/// </summary>
public sealed class HomeFeedState
{
    /// <summary>HomeViewModel.kt refreshFromRealtime (1b4732d90): <c>for (attempt in 0..3)</c>, waiting 1, 2 and 4 s.</summary>
    public const int MaxRetries = 3;

    private HomeLoadRequest? _request;
    private HomeRetry? _retry;
    private int _generation;
    private bool _everShown;
    private bool _hasResult;
    private bool _restoringFocus;
    private bool _refreshPending;

    // TvHomeScreen.kt keeps the populated server sections separate from their local projection.
    // Hiding every row must not lose the rows the Settings editor needs to show again.
    public IReadOnlyList<MediaRow> AvailableRows { get; private set; } = [];
    public IReadOnlyList<MediaRow> Rows { get; private set; } = [];
    public HomeSectionLayout Layout { get; private set; } = new();
    public bool SectionsHiddenByPreference => Layout.AllHidden(AvailableRows);
    public string? Error { get; private set; }
    public bool IsShown { get; private set; }
    public bool IsBusy => _request is not null;
    public bool IsLoading => !_hasResult || (_request is { Quiet: false } && AvailableRows.Count == 0);
    public HomeFeedStatus Status => HomeFeedback.Status(IsLoading, Rows.Count, Error);

    /// <summary>The retry a transiently failed quiet read is waiting for; the page starts it with <see cref="StartRetry"/>.</summary>
    public HomeRetry? PendingRetry => _retry;

    /// <summary>Initial load or an explicit Refresh/Retry; never race an existing read. It replaces a waiting retry.</summary>
    public HomeLoadRequest? BeginLoad() => IsBusy ? null : Start(quiet: false);

    public HomeLoadRequest? OnShown(bool deferUntilFocusRestored = false)
    {
        if (IsShown) return null;
        IsShown = true;
        _restoringFocus = deferUntilFocusRestored;
        // TvHomeScreen.kt skips the resume read while the very first load is still running.
        // A first load that finished before Loaded must not be fetched a second time either.
        // A real return supplies one freshness signal, held behind Detail-return focus restoration like
        // TvHomeScreen.kt's pending realtime read (1b4732d90 re-reads on every return, not on a first load). The
        // events socket (HomeWatchState) adds its own on every subscription and change.
        if (_everShown && _hasResult) _refreshPending = true;
        _everShown = true;
        return TakePendingRefresh();
    }

    public void OnHidden()
    {
        IsShown = false;
        _restoringFocus = false;
        // HomeViewModel.onHidden drops a quiet read, but keeps the one filling an empty Home. A waiting retry goes
        // with it, and the read runs when Home is shown again.
        if (_request is { Quiet: true } || _retry is not null)
        {
            if (_request is { Quiet: true }) _request = null;
            _retry = null;
            _refreshPending = true;
        }
    }

    public HomeLoadRequest? OnFocusRestored()
    {
        _restoringFocus = false;
        return TakePendingRefresh();
    }

    /// <summary>Coalesce a freshness signal rather than dropping it beside an in-flight read.</summary>
    public HomeLoadRequest? RequestRefresh()
    {
        _refreshPending = true;
        return TakePendingRefresh();
    }

    public HomeLoadRequest? TakePendingRefresh()
    {
        // A waiting retry covers the signals that arrive meanwhile.
        if (!IsShown || _restoringFocus || IsBusy || _retry is not null || !_hasResult || !_refreshPending) return null;
        _refreshPending = false;
        return Start(quiet: true);
    }

    /// <summary>
    /// The retry's wait is over: its read, unless Home was hidden or another read replaced it meanwhile. HomeViewModel.kt:
    /// "The retry will cover any events that arrived while it waited."
    /// </summary>
    public HomeLoadRequest? StartRetry(HomeRetry retry)
    {
        if (!ReferenceEquals(retry, _retry) || !IsShown || IsBusy) return null;
        _refreshPending = false;
        return Start(quiet: true, retry.Retry);
    }

    private HomeLoadRequest Start(bool quiet, int retry = 0)
    {
        if (!quiet) Error = null;
        _retry = null;
        return _request = new HomeLoadRequest(++_generation, quiet) { Retry = retry };
    }

    public bool IsCurrent(HomeLoadRequest request) => ReferenceEquals(request, _request);

    public bool Apply(HomeLoadRequest request, IReadOnlyList<MediaRow> rows)
    {
        if (!IsCurrent(request)) return false;
        SetRows(rows);
        _request = null;
        return true;
    }

    public bool Fail(HomeLoadRequest request, Exception error)
    {
        if (!IsCurrent(request)) return false;
        _hasResult = true;
        _request = null;
        // TvHomeScreen.kt leaves good prior sections visible, without a blocking error or spinner.
        Error = AvailableRows.Count > 0 ? null : HomeFeedback.ErrorMessage(error);
        // HomeViewModel.kt refreshFromRealtime (1b4732d90): a quiet read that failed transiently is read again up to
        // three times, 1, 2 and 4 s later, while Home stays shown and no other read replaces it. The initial load and an
        // explicit Refresh or Retry are not retried, as there.
        if (request.Quiet && IsShown && request.Retry < MaxRetries && HomeFeedback.IsTransient(error))
            _retry = new HomeRetry(request.Retry + 1, TimeSpan.FromSeconds(1 << request.Retry));
        return true;
    }

    /// <summary>HomeViewModel.kt's optimistic card removals also change whether Home is empty.</summary>
    public void SetRows(IReadOnlyList<MediaRow> rows)
    {
        AvailableRows = rows.Where(row => row.Cards.Count > 0).ToList();
        Rows = Layout.Arrange(AvailableRows);
        _hasResult = true;
        Error = null;
    }

    public void SetLayout(HomeSectionLayout layout)
    {
        Layout = layout;
        Rows = Layout.Arrange(AvailableRows);
    }
}
