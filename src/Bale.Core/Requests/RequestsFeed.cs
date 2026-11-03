using System.Net.Sockets;
using Bale.Core.Api;
using Bale.Core.Api.Models;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace Bale.Core.Requests;

public sealed record RequestsCapability(bool Enabled = false, bool Resolved = false, bool FollowSupported = false);
public sealed record RequestMutationResult(bool Succeeded, string? Message = null, bool Unconfirmed = false);

/// <summary>
/// RequestsApi.kt / RequestsRepository.kt: viewer-only v2 operations, complete cursor reads, and
/// session-owned state. No read submits a request; non-idempotent writes never retry in middleware.
/// </summary>
public sealed class RequestsFeed(SiloApiClient api, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<(string Type, long Id), Hold> _submissions = new();
    private readonly Dictionary<string, Hold> _cancellations = new();
    private readonly HashSet<string> _inFlight = new();
    private IReadOnlyList<MediaRequest> _mine = [];
    private DateTimeOffset? _mineReadAt;
    private RequestsCapability _capability = new();
    private Exception? _lastProbeError;
    private string? _lastProbeSummary;
    private int _generation;
    private int _mutations;

    private sealed record Hold(DateTimeOffset Since, DateTimeOffset? UpdatedAt = null);
    public RequestsCapability Capability { get { lock (_sync) return _capability; } }
    public Exception? LastProbeError { get { lock (_sync) return _lastProbeError; } }
    public string? LastProbeSummary { get { lock (_sync) return _lastProbeSummary; } }

    public void Reset()
    {
        lock (_sync)
        {
            _generation++;
            _mutations++;
            _mine = [];
            _mineReadAt = null;
            _capability = new();
            _lastProbeError = null;
            _lastProbeSummary = null;
            _submissions.Clear();
            _cancellations.Clear();
            _inFlight.Clear();
        }
    }

    public async Task<RequestsCapability> RefreshCapabilityAsync(CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        try
        {
            var status = await api.Api.V2.Requests.Status.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (status is null) throw new InvalidDataException("The server returned an empty requests status.");
            lock (_sync)
            {
                CheckGeneration(generation);
                var enabled = RequestPresentation.IsEnabled(status);
                _capability = new(enabled, true, enabled && status.FollowSupported == true);
                _lastProbeError = null;
                _lastProbeSummary = ($"requests_enabled={status.RequestsEnabled?.ToString() ?? "null"}, "
                    + $"allowed={status.Allowed?.ToString() ?? "null"}, state={status.State?.ToString() ?? "null"}, "
                    + $"follow_supported={status.FollowSupported?.ToString() ?? "null"}").ToLowerInvariant();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // RequestsFeatureStore.kt: start hidden, retain the last answer on a transient failure.
            lock (_sync)
            {
                CheckGeneration(generation);
                _capability = _capability with { Resolved = true };
                _lastProbeError = ex;
                _lastProbeSummary = null;
            }
        }
        return Capability;
    }

    public async Task<IReadOnlyList<RequestRow>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        // TvRequestsPage.kt waits for both reads, so rows cannot appear above a focused card.
        var discoverTask = api.Api.V2.Requests.Discover.GetAsync(cancellationToken: cancellationToken);
        var mineTask = LoadMineAsync(force: true, cancellationToken);
        await Task.WhenAll(discoverTask, mineTask).ConfigureAwait(false);
        var discover = await discoverTask.ConfigureAwait(false);
        if (discover?.Items is null || discover.Items.Any(section => section.Results is null || string.IsNullOrWhiteSpace(section.Key)))
            throw new InvalidDataException("The server returned incomplete request discovery.");
        lock (_sync) CheckGeneration(generation);
        return RequestPresentation.Rows(await mineTask.ConfigureAwait(false), discover);
    }

    public async Task<IReadOnlyList<MediaRequest>> LoadMineAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        int generation;
        int mutations;
        lock (_sync)
        {
            generation = _generation;
            mutations = _mutations;
            if (!force && _mineReadAt is { } at && _time.GetUtcNow() - at < TimeSpan.FromSeconds(30)) return _mine;
        }
        var records = new List<MediaRequest>();
        var seen = new HashSet<string>();
        string? cursor = null;
        for (var page = 0; page < 100; page++)
        {
            lock (_sync) CheckGeneration(generation);
            var response = await api.Api.V2.Requests.Mine.GetAsync(config =>
            {
                config.QueryParameters.Limit = 50;
                config.QueryParameters.Cursor = cursor;
            }, cancellationToken).ConfigureAwait(false);
            if (response?.Items is null || response.Page?.HasMore is null || response.Items.Any(record => !Valid(record)))
                throw new InvalidDataException("The server returned an incomplete request list.");
            records.AddRange(response.Items);
            if (response.Page.HasMore == false)
            {
                lock (_sync)
                {
                    CheckGeneration(generation);
                    if (mutations != _mutations) return _mine;
                    _mine = records.ToArray();
                    _mineReadAt = _time.GetUtcNow();
                    foreach (var id in _cancellations.Keys.ToArray())
                    {
                        var current = records.FirstOrDefault(record => record.Id == id);
                        if (current is null || current.UpdatedAt != _cancellations[id].UpdatedAt || Expired(_cancellations[id]))
                            _cancellations.Remove(id);
                    }
                    return _mine;
                }
            }
            cursor = response.Page.NextCursor;
            if (string.IsNullOrEmpty(cursor) || !seen.Add(cursor))
                throw new InvalidDataException("The server returned invalid request pagination.");
        }
        throw new InvalidDataException("The request list exceeded the page limit.");
    }

    public async Task<RequestDetailState> LoadDetailAsync(string mediaType, long tmdbId, bool forceMine = false,
        CancellationToken cancellationToken = default)
    {
        var generation = Generation();
        var detail = await api.Api.V2.Requests.Detail[mediaType][tmdbId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (detail?.Request is null || detail.TmdbId != tmdbId || detail.MediaType != mediaType || string.IsNullOrWhiteSpace(detail.Title))
            throw new InvalidDataException("The server returned incomplete request details.");
        try { await LoadMineAsync(forceMine, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            // RequestDetailViewModel.kt: an optional own-list failure keeps the title usable.
            lock (_sync) CheckGeneration(generation);
        }
        lock (_sync)
        {
            CheckGeneration(generation);
            return DetailState(detail);
        }
    }

    public RequestDetailState StateFor(RequestMediaDetail detail)
    {
        lock (_sync) return DetailState(detail);
    }

    private RequestDetailState DetailState(RequestMediaDetail detail)
    {
        var record = _mine.Where(record => record.MediaType == detail.MediaType && record.TmdbId == detail.TmdbId)
            .OrderByDescending(record => record.Outcome == "active").ThenByDescending(record => record.CreatedAt).FirstOrDefault();
        var key = (detail.MediaType!, detail.TmdbId!.Value);
        var state = new RequestDetailState(detail, record, _capability.FollowSupported);
        if (_submissions.TryGetValue(key, out var submit))
        {
            if (Expired(submit) || state.PrimaryAction != RequestPrimaryAction.Request) _submissions.Remove(key);
            else state = state with { SubmissionUnconfirmed = true };
        }
        if (record?.Id is { } id && _cancellations.TryGetValue(id, out var cancel))
        {
            if (Expired(cancel) || record.UpdatedAt != cancel.UpdatedAt) _cancellations.Remove(id);
            else state = state with { CancelUnconfirmed = true };
        }
        return state;
    }

    public async Task<RequestMutationResult> CreateAsync(RequestMediaDetail detail, CancellationToken cancellationToken = default)
    {
        if (!RequestPresentation.CanRequest(detail.MediaType) || detail.TmdbId is not > 0 || string.IsNullOrWhiteSpace(detail.Title))
            return new(false, "That request couldn't be submitted");
        var key = (detail.MediaType!, detail.TmdbId.Value);
        var operation = $"create:{key}";
        int generation;
        lock (_sync)
        {
            if (!_capability.Enabled || DetailState(detail).PrimaryAction != RequestPrimaryAction.Request || !_inFlight.Add(operation))
                return new(false);
            generation = _generation;
        }
        // RequestDetailViewModel.kt: the explicit Request button is the confirmation (one tap).
        var body = new MediaRequestCreate
        {
            MediaType = detail.MediaType == "movie" ? MediaRequestCreate_media_type.Movie : MediaRequestCreate_media_type.Series,
            TmdbId = detail.TmdbId, TvdbId = detail.TvdbId, ImdbId = detail.ImdbId, Title = detail.Title,
            Year = detail.Year, Overview = detail.Overview, PosterPath = detail.PosterPath, BackdropPath = detail.BackdropPath,
        };
        return await MutateAsync(operation, generation,
            () => api.Api.V2.Requests.PostAsync(body, config => config.Options.Add(new RetryHandlerOption { MaxRetry = 0 }), cancellationToken),
            () => _submissions[key] = new(_time.GetUtcNow()), RequestPresentation.UnconfirmedSubmit,
            "Failed to submit request").ConfigureAwait(false);
    }

    public bool CanCancel(MediaRequest record)
    {
        lock (_sync)
            return _capability.Enabled && Valid(record) && RequestPresentation.Progress(record).Display == RequestDisplay.Pending
                && (!_cancellations.TryGetValue(record.Id!, out var held) || Expired(held))
                && !_inFlight.Contains($"cancel:{record.Id}");
    }

    public async Task<RequestMutationResult> CancelAsync(MediaRequest record, CancellationToken cancellationToken = default)
    {
        if (!Valid(record)) return new(false);
        var operation = $"cancel:{record.Id}";
        int generation;
        lock (_sync)
        {
            if (!_capability.Enabled || RequestPresentation.Progress(record).Display != RequestDisplay.Pending
                || _cancellations.TryGetValue(record.Id!, out var held) && !Expired(held) || !_inFlight.Add(operation))
                return new(false);
            generation = _generation;
        }
        // TvRequestDetailScreen.kt cancels on the explicit pill; the hub's long-press dialog starts on Close.
        return await MutateAsync(operation, generation,
            () => api.Api.V2.Requests[record.Id!].Cancel.PostAsync(new RequestCancelInputBody(),
                config => config.Options.Add(new RetryHandlerOption { MaxRetry = 0 }), cancellationToken),
            () => _cancellations[record.Id!] = new(_time.GetUtcNow(), record.UpdatedAt), RequestPresentation.UnconfirmedCancel,
            "Couldn't cancel the request").ConfigureAwait(false);
    }

    private async Task<RequestMutationResult> MutateAsync(string operation, int generation, Func<Task<MediaRequest?>> send,
        Action hold, string unconfirmed, string fallback)
    {
        try
        {
            var record = await send().ConfigureAwait(false);
            if (!Valid(record)) throw new InvalidDataException("The server returned an incomplete request response.");
            lock (_sync)
            {
                if (generation == _generation)
                {
                    _mutations++;
                    _mine = _mine.Where(item => item.Id != record!.Id).Append(record!).ToArray();
                    _mineReadAt = null;
                }
            }
            return new(true);
        }
        catch (Exception ex)
        {
            var uncertain = IsUncertain(ex);
            lock (_sync)
            {
                if (uncertain && generation == _generation) hold();
            }
            return new(false, uncertain ? unconfirmed : ErrorMessage(ex, fallback), uncertain);
        }
        finally
        {
            lock (_sync)
            {
                if (generation == _generation) _inFlight.Remove(operation);
            }
        }
    }

    public async Task<RequestMutationResult> ToggleFollowAsync(RequestDetailState state, CancellationToken cancellationToken = default)
    {
        var detail = state.Detail;
        var operation = $"follow:{detail.MediaType}:{detail.TmdbId}";
        var wasFollowing = state.IsFollowing;
        int generation;
        lock (_sync)
        {
            if (!_capability.Enabled || !_capability.FollowSupported || !state.CanFollow || !_inFlight.Add(operation)) return new(false);
            generation = _generation;
        }
        try
        {
            if (state.IsFollowing)
            {
                await api.Api.V2.Requests.Follows[detail.MediaType!][detail.TmdbId!.Value]
                    .DeleteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                lock (_sync) { CheckGeneration(generation); detail.Request!.Following = false; }
            }
            else
            {
                var answer = await api.Api.V2.Requests.Follows[detail.MediaType!][detail.TmdbId!.Value]
                    .PutAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (answer?.Following is null) throw new InvalidDataException("The server returned an incomplete follow response.");
                lock (_sync)
                {
                    CheckGeneration(generation);
                    answer.RequestId ??= detail.Request?.RequestId;
                    detail.Request = answer;
                }
            }
            return new(true, wasFollowing ? "Notification turned off" : "We'll let you know when it's available");
        }
        catch (Exception ex) { return new(false, ErrorMessage(ex, "Failed to update notification")); }
        finally
        {
            lock (_sync) { if (generation == _generation) _inFlight.Remove(operation); }
        }
    }

    private static bool Valid(MediaRequest? record) => record is { Id.Length: > 0, MediaType.Length: > 0, TmdbId: > 0, Status: not null, Outcome: not null };
    private int Generation() { lock (_sync) return _generation; }
    private void CheckGeneration(int generation)
    {
        if (generation != _generation) throw new OperationCanceledException("The acting account or profile changed.");
    }
    private bool Expired(Hold hold) => _time.GetUtcNow() - hold.Since >= TimeSpan.FromSeconds(60);

    public static bool IsUncertain(Exception error)
    {
        // RequestMutations.kt: no usable answer may mean the server acted. Never automatically resend.
        if (error is ApiException apiError)
            return apiError.ResponseStatusCode is 502 or 503 or 504 && (error is not Problem problem || string.IsNullOrEmpty(problem.Type));
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.ConnectionRefused or SocketError.NetworkUnreachable or SocketError.HostUnreachable })
                return false;
        }
        return true;
    }

    public static string ErrorMessage(Exception error, string fallback = "Failed to load requests")
    {
        if (error is Problem problem)
        {
            var code = problem.Type?.Split('/', ':').LastOrDefault();
            if (code == "validation_failed") return "That request couldn't be submitted";
            if (code is "capability_disabled" or "capability_not_configured" or "capability_unsupported") return "Requests are turned off";
            if (!string.IsNullOrWhiteSpace(problem.Detail)) return problem.Detail;
        }
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is TimeoutException or OperationCanceledException) return "The server took too long to respond. Try again.";
        return error is HttpRequestException ? "Network error. Check your connection." : fallback;
    }
}
