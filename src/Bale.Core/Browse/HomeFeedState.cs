namespace Bale.Core.Browse;

public sealed record HomeLoadRequest(int Generation, bool Quiet);

/// <summary>
/// TvHomeScreen.kt / HomeViewModel.kt: one initial load, quiet single-flight resume reads,
/// hidden reads deferred, and Detail-return refresh held until the shell restores focus.
/// </summary>
public sealed class HomeFeedState
{
    private HomeLoadRequest? _request;
    private int _generation;
    private bool _everShown;
    private bool _hasResult;
    private bool _restoringFocus;
    private bool _refreshPending;

    public IReadOnlyList<MediaRow> Rows { get; private set; } = [];
    public string? Error { get; private set; }
    public bool IsShown { get; private set; }
    public bool IsBusy => _request is not null;
    public bool IsLoading => !_hasResult || (_request is { Quiet: false } && Rows.Count == 0);
    public HomeFeedStatus Status => HomeFeedback.Status(IsLoading, Rows.Count, Error);

    /// <summary>Initial load or an explicit Refresh/Retry; never race an existing read.</summary>
    public HomeLoadRequest? BeginLoad() => IsBusy ? null : Start(quiet: false);

    public HomeLoadRequest? OnShown(bool deferUntilFocusRestored = false)
    {
        if (IsShown) return null;
        IsShown = true;
        _restoringFocus = deferUntilFocusRestored;
        // TvHomeScreen.kt skips the resume read while the very first load is still running.
        // A first load that finished before Loaded must not be fetched a second time either.
        if (_everShown && _hasResult) _refreshPending = true;
        _everShown = true;
        return TakePendingRefresh();
    }

    public void OnHidden()
    {
        IsShown = false;
        _restoringFocus = false;
        // HomeViewModel.onHidden drops a quiet read, but keeps the one filling an empty Home.
        if (_request is { Quiet: true })
        {
            _request = null;
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
        if (!IsShown || _restoringFocus || IsBusy || !_hasResult || !_refreshPending) return null;
        _refreshPending = false;
        return Start(quiet: true);
    }

    private HomeLoadRequest Start(bool quiet)
    {
        if (!quiet) Error = null;
        return _request = new HomeLoadRequest(++_generation, quiet);
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
        Error = Rows.Count > 0 ? null : HomeFeedback.ErrorMessage(error);
        return true;
    }

    /// <summary>HomeViewModel.kt's optimistic card removals also change whether Home is empty.</summary>
    public void SetRows(IReadOnlyList<MediaRow> rows)
    {
        Rows = rows.Where(row => row.Cards.Count > 0).ToList();
        _hasResult = true;
        Error = null;
    }
}
