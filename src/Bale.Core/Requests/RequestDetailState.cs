using System.Globalization;
using Bale.Core.Api.Models;

namespace Bale.Core.Requests;

public enum RequestPrimaryAction { Request, Submitting, Status, OpenInLibrary }

/// <summary>shared RequestDetailViewModel.kt: stable primary action, own-record fallback and follow gating.</summary>
public sealed record RequestDetailState(RequestMediaDetail Detail, MediaRequest? Record, bool FollowSupported = false,
    bool IsSubmitting = false, bool IsCancelling = false, bool SubmissionUnconfirmed = false, bool CancelUnconfirmed = false)
{
    private RequestProgress? TitleProgress => RequestPresentation.Progress(Detail.Availability, Detail.Request);
    private RequestProgress? ActiveRecord => Record?.Outcome == "active" && RequestPresentation.Progress(Record) is
        { Display: RequestDisplay.Pending or RequestDisplay.OnTheWay } active ? active : null;
    private MediaRequest? CurrentOwnRecord => Record is not { } own ? null
        : own.Outcome == "active" ? own
        : TitleProgress?.Display == RequestDisplay.InLibrary || Detail.Request?.RequestId is { } id && id != own.Id ? null : own;
    public MediaRequest? EndedRequest => CurrentOwnRecord is { } own && RequestPresentation.Progress(own).Display is
        RequestDisplay.Declined or RequestDisplay.Failed ? own : null;

    public RequestProgress? Progress => CurrentOwnRecord is { } own && (ActiveRecord is not null || EndedRequest is not null
        || Detail.Request?.RequestId == own.Id) ? RequestPresentation.Progress(own)
        : TitleProgress is { Display: not RequestDisplay.Unavailable } progress ? progress : null;
    public bool ShowsStatus => Progress is { Display: not RequestDisplay.InLibrary };
    public string? LibraryItemToOpen => TitleProgress?.LibraryItemToOpen(Detail.LibraryContentId);
    public RequestPrimaryAction PrimaryAction => LibraryItemToOpen is not null ? RequestPrimaryAction.OpenInLibrary
        : IsSubmitting ? RequestPrimaryAction.Submitting
        : SubmissionUnconfirmed ? RequestPrimaryAction.Status
        : TitleProgress is { } state && !(state.Display is RequestDisplay.Declined or RequestDisplay.Failed && Detail.Request?.Requestable == true)
            || ActiveRecord is not null ? RequestPrimaryAction.Status : RequestPrimaryAction.Request;

    public string PrimaryTitle => PrimaryAction switch
    {
        RequestPrimaryAction.OpenInLibrary => "Open in Library",
        RequestPrimaryAction.Submitting => "Requesting…",
        RequestPrimaryAction.Request => EndedRequest is null ? "Request" : "Request Again",
        _ when SubmissionUnconfirmed => "Not confirmed yet",
        _ => (Progress ?? TitleProgress)?.DetailTitle ?? "Unavailable",
    };
    public bool CanCancel => !IsCancelling && !CancelUnconfirmed && Record is { Id.Length: > 0 }
        && RequestPresentation.Progress(Record).Display == RequestDisplay.Pending;
    public bool CanFollow => FollowSupported && Detail.Request is
        { Requestable: not true, RequestedByViewer: not true, Reason: "already_requested" }
        && TitleProgress is { Display: not (RequestDisplay.InLibrary or RequestDisplay.Unavailable) };
    public bool IsFollowing => Detail.Request?.Following == true;
    public string FollowTitle => IsFollowing ? "Stop notifying me" : "Notify me when available";
    public IReadOnlyList<RequestTitle> Recommendations => (Detail.Recommendations ?? [])
        .Where(result => RequestPresentation.CanRequest(result.MediaType)).Select(RequestPresentation.Title).ToArray();
    public string? RequestedDay => RequestPresentation.Day(Record?.CreatedAt);
    public string? Quality => RequestPresentation.QualitySummary(Record?.Targets);
    public string? Credit => !string.IsNullOrWhiteSpace(Detail.Director) ? $"Directed by {Detail.Director}"
        : Detail.Creators is { Count: > 0 } creators ? $"Created by {string.Join(", ", creators.Take(2))}" : null;
    public string Facts => string.Join(" · ", new[]
    {
        Detail.Year is > 0 ? Detail.Year.Value.ToString(CultureInfo.CurrentCulture) : null,
        Detail.MediaType == "series" ? Detail.NumberOfSeasons is > 0
            ? $"{Detail.NumberOfSeasons} {(Detail.NumberOfSeasons == 1 ? "season" : "seasons")}" : null
            : Detail.Runtime is > 0 ? Detail.Runtime >= 60 ? $"{Detail.Runtime / 60}h {Detail.Runtime % 60}m" : $"{Detail.Runtime}m" : null,
        Detail.VoteAverage is > 0 ? $"TMDB {Detail.VoteAverage.Value.ToString("0.0", CultureInfo.CurrentCulture)}" : null,
    }.Where(value => value is not null));
}
