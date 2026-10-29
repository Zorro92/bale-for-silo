using Bale.Core.Detail;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media.Imaging;

namespace Bale.Xbox.Views;

/// <summary>An episode card on the series page (TvDetailEpisodeRail): 180x101 still, eyebrow, title, synopsis.</summary>
public sealed partial class EpisodeView
{
    public const double StillWidth = 180;
    public const double StillHeight = 101;

    public EpisodeView(EpisodeCard episode)
    {
        Episode = episode;
        ProgressVisibility = episode.Progress is > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProgressFillWidth = StillWidth * (episode.Progress ?? 0);
        WatchedVisibility = episode.Played ? Visibility.Visible : Visibility.Collapsed;
        if (episode.StillUrl is not null)
        {
            Still = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = (int)StillWidth, UriSource = episode.StillUrl };
        }
    }

    public EpisodeCard Episode { get; }

    public string Label => Episode.Label;

    public string Title => Episode.Title;

    public string Overview => Episode.Overview ?? string.Empty;

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
            Photo = new BitmapImage { DecodePixelType = DecodePixelType.Logical, DecodePixelWidth = 100, UriSource = member.PhotoUrl };
        }
    }

    public CastMember Member { get; }

    public string Name => Member.Name;

    public string Role => Member.Role ?? string.Empty;

    public BitmapImage? Photo { get; }

    public Visibility PlaceholderVisibility { get; }
}

/// <summary>A season chip.</summary>
public sealed partial class SeasonView
{
    public SeasonView(SeasonChoice season) => Season = season;

    public SeasonChoice Season { get; }

    public string Title => Season.Title;
}
