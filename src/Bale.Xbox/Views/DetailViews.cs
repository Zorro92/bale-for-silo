using System.ComponentModel;
using Bale.Core.Detail;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

/// <summary>An episode card on the series page (TvDetailEpisodeRail, series geometry): 180x101 still, "S01E02 · Title", date and runtime.</summary>
public sealed partial class EpisodeView : INotifyPropertyChanged
{
    public const double StillWidth = 180;
    public const double StillHeight = 101;

    public EpisodeView(EpisodeCard episode, bool isCurrent)
    {
        Episode = episode;
        CurrentVisibility = isCurrent ? Visibility.Visible : Visibility.Collapsed;
        ProgressVisibility = episode.Progress is > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgressFillWidth = StillWidth * (episode.Progress ?? 0);
        WatchedVisibility = episode.Played ? Visibility.Visible : Visibility.Collapsed;
        if (episode.StillUrl is not null)
        {
            Still = Services.Artwork.Load(episode.StillUrl, width: (int)StillWidth);
        }
    }

    public EpisodeCard Episode { get; }

    public string Caption => Episode.Caption;

    public string CaptionMeta => Episode.CaptionMeta;

    /// <summary>The active Play target retains its 2px border after focus leaves the rail (TvDetailEpisodeRail.kt).</summary>
    public Visibility CurrentVisibility { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetCurrent(bool current)
    {
        var visibility = current ? Visibility.Visible : Visibility.Collapsed;
        if (CurrentVisibility != visibility)
        {
            CurrentVisibility = visibility;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentVisibility)));
        }
    }

    public BitmapImage? Still { get; }

    public Visibility ProgressVisibility { get; }

    public double ProgressFillWidth { get; }

    public Visibility WatchedVisibility { get; }
}

/// <summary>A cast member (TvCastCrewSection): 100 px round photo, name, role.</summary>
public sealed partial class CastView
{
    public CastView(CastMember member)
    {
        Member = member;
        PlaceholderVisibility = member.PhotoUrl is null ? Visibility.Visible : Visibility.Collapsed;
        if (member.PhotoUrl is not null)
        {
            Photo = Services.Artwork.Load(member.PhotoUrl, width: 100);
        }
    }

    public CastMember Member { get; }

    public string Name => Member.Name;

    public string Role => Member.Role ?? string.Empty;

    public Visibility RoleVisibility => string.IsNullOrWhiteSpace(Member.Role) ? Visibility.Collapsed : Visibility.Visible;

    public BitmapImage? Photo { get; }

    public Visibility PlaceholderVisibility { get; }
}

/// <summary>A local extra or supported remote trailer (TvDetailTrailersSection).</summary>
public sealed partial class TrailerView
{
    public TrailerView(TrailerCard card)
    {
        Card = card;
        Thumbnail = card.ThumbnailUrl is { } url ? Services.Artwork.Load(url, width: 235) : null;
    }

    public TrailerCard Card { get; }
    public string Title => Card.Title;
    public string Secondary => Card.Secondary ?? string.Empty;
    public BitmapImage? Thumbnail { get; }
    public Visibility PlaceholderVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SecondaryVisibility => Card.Secondary is null ? Visibility.Collapsed : Visibility.Visible;
}

public sealed partial class DetailFactView(DetailFact fact, int index)
{
    public string Label => fact.Label.ToUpperInvariant();
    public string Value => fact.Value;
    public Visibility DividerVisibility => index == 0 ? Visibility.Collapsed : Visibility.Visible;
}

/// <summary>A tab in the series mode row: "Show" (no season), then each season (TvSeriesModePicker).</summary>
public sealed partial class SeasonView
{
    public SeasonView(SeasonChoice? season) => Season = season;

    /// <summary>Null for the "Show" tab.</summary>
    public SeasonChoice? Season { get; }

    public string Title => Season?.Title ?? "Show";
}
