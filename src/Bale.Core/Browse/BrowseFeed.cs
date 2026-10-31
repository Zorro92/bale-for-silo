using System.Globalization;
using Bale.Core.Api;
using Bale.Core.Api.Api.V2.Catalog;
using Bale.Core.Api.Api.V2.Catalog.People;
using Bale.Core.Api.Models;

namespace Bale.Core.Browse;

/// <summary>
/// Rows for Home and library landings, and search results. Row order and contents are the
/// server's (and the profile's customizations); Bale never invents its own rows.
/// </summary>
public sealed class BrowseFeed
{
    /// <summary>SectionRowLimits.kt: full sections live on their paged "Explore all" screen.</summary>
    public const int CardsPerRow = 20;

    private readonly SiloApiClient _api;
    private readonly Uri _server;
    private HomeCapabilities? _capabilities;

    public BrowseFeed(SiloApiClient api, Uri server)
    {
        _api = api;
        _server = server;
    }

    /// <summary>Home's rows.</summary>
    public async Task<IReadOnlyList<MediaRow>> LoadHomeAsync(ArtworkWidths widths, CancellationToken cancellationToken = default)
    {
        // item_limit is only safe to send when the server advertises it.
        _capabilities ??= await _api.Api.V2.Home.Capabilities.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var limitMax = _capabilities?.SectionItemLimitMax;

        var response = await _api.Api.V2.Home.Sections.GetAsync(config =>
        {
            config.QueryParameters.PosterWidth = widths.Poster;
            config.QueryParameters.BackdropWidth = widths.Backdrop;
            config.QueryParameters.LogoWidth = widths.Logo;
            config.QueryParameters.StillWidth = widths.Still;
            if (limitMax is > 0)
            {
                config.QueryParameters.ItemLimit = Math.Min(CardsPerRow, limitMax.Value);
            }
        }, cancellationToken).ConfigureAwait(false);

        return ToRows(response?.Sections);
    }

    /// <summary>HomeSectionsV2Api.kt sends only the observed progress stamp or the Next Up series ID.</summary>
    public Task DismissFromHomeAsync(MediaCard card, CancellationToken cancellationToken = default)
    {
        var dismissal = HomeCardDismissal.For(card)
            ?? throw new ArgumentException("A Home dismissal needs its observed progress stamp or series ID.", nameof(card));
        if (string.IsNullOrWhiteSpace(card.ContentId))
        {
            throw new ArgumentException("A Home dismissal needs a content ID.", nameof(card));
        }

        return _api.Api.V2.Home.Dismissals[dismissal.Surface][card.ContentId].PutAsync(new Api.Models.HomeDismissal
        {
            ProgressUpdatedAt = dismissal.Surface == "continue_watching" ? card.ProgressUpdatedAt : null,
            SeriesId = dismissal.Surface == "next_up" ? card.SeriesId : null,
        }, cancellationToken: cancellationToken);
    }

    /// <summary>A library's Browse landing: its server-defined rows.</summary>
    public async Task<IReadOnlyList<MediaRow>> LoadLibraryAsync(string libraryId, ArtworkWidths widths, CancellationToken cancellationToken = default)
    {
        var response = await _api.Api.V2.Library[libraryId].Sections.GetAsync(config =>
        {
            config.QueryParameters.PosterWidth = widths.Poster;
            config.QueryParameters.BackdropWidth = widths.Backdrop;
            config.QueryParameters.LogoWidth = widths.Logo;
            config.QueryParameters.StillWidth = widths.Still;
        }, cancellationToken).ConfigureAwait(false);

        return ToRows(response?.Sections, libraryId);
    }

    /// <summary>For You: the server's recommendation rows (TvRecommendationsScreen, /recommendations/discover).</summary>
    public async Task<IReadOnlyList<MediaRow>> LoadForYouAsync(ArtworkWidths widths, CancellationToken cancellationToken = default)
    {
        var response = await _api.Api.V2.Recommendations.Discover.GetAsync(config =>
        {
            config.QueryParameters.PosterWidth = widths.Poster;
            config.QueryParameters.BackdropWidth = widths.Backdrop;
            config.QueryParameters.LogoWidth = widths.Logo;
            config.QueryParameters.StillWidth = widths.Still;
        }, cancellationToken).ConfigureAwait(false);

        return (response?.Items ?? [])
            .Where(row => row.Items is { Count: > 0 })
            .Select(row => new MediaRow(
                row.Key ?? string.Empty,
                row.Title ?? string.Empty,
                row.Kind ?? string.Empty,
                false,
                CardShape.Poster,
                row.Items!.Take(CardsPerRow).Select(item => ToCard(item, CardShape.Poster)).ToList()))
            .ToList();
    }

    /// <summary>First-page convenience for the CLI; the TV uses <see cref="SearchPageAsync"/>.</summary>
    public async Task<IReadOnlyList<MediaCard>> SearchAsync(string query, ArtworkWidths widths, string? type = null, int limit = 40, CancellationToken cancellationToken = default) =>
        (await SearchPageAsync(query, widths, type, showAudiobooks: true, limit: limit, cancellationToken: cancellationToken).ConfigureAwait(false)).Cards;

    /// <summary>
    /// TvSearchViewModel.kt pages source=query in batches of 40 using the opaque page.next_cursor,
    /// never an offset/seek. Drain hidden-only pages: an empty grid cannot trigger its own load-more.
    /// </summary>
    public Task<SearchResultPage> SearchPageAsync(string query, ArtworkWidths widths, string? type = null,
        SearchContinuation? continuation = null, bool showAudiobooks = false, int limit = 40, CancellationToken cancellationToken = default) =>
        CatalogPageAsync(GetSourceQueryParameterType.Query, query.Trim(), null, widths, type, continuation, showAudiobooks, limit, cancellationToken);

    /// <summary>TvPersonDetailViewModel uses the same opaque catalog pagination, with source=person and sixty-card pages.</summary>
    internal Task<SearchResultPage> PersonWorksPageAsync(string personId, ArtworkWidths widths, string? type,
        SearchContinuation? continuation, bool showAudiobooks, CancellationToken cancellationToken) =>
        CatalogPageAsync(GetSourceQueryParameterType.Person, null, personId, widths, type, continuation, showAudiobooks, 60, cancellationToken);

    /// <summary>SectionApi.kt: source=section, explicit home/library scope, opaque cursors, fifty-item pages.</summary>
    public Task<SearchResultPage> SectionPageAsync(SectionBrowseSource section, ArtworkWidths widths,
        SearchContinuation? continuation = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(section.SectionId);
        if (section.LibraryId is { } libraryId && (!long.TryParse(libraryId, out var id) || id <= 0))
        {
            throw new ArgumentException("Invalid section library.", nameof(section));
        }

        // SectionBrowseViewModel applies mediaScope locally; changing the catalog query's type would change the row.
        // The TV section page hides reading media, but unlike Search it does not hide audiobooks.
        return CatalogPageAsync(GetSourceQueryParameterType.Section, null, null, widths, null, continuation,
            showAudiobooks: true, 50, cancellationToken, section);
    }

    private async Task<SearchResultPage> CatalogPageAsync(GetSourceQueryParameterType source, string? query, string? personId,
        ArtworkWidths widths, string? type, SearchContinuation? continuation, bool showAudiobooks, int limit, CancellationToken cancellationToken,
        SectionBrowseSource? section = null)
    {
        var cursor = continuation?.Cursor;
        var seen = new HashSet<string>(continuation?.Seen ?? [], StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await _api.Api.V2.Catalog.GetAsync(config =>
            {
                config.QueryParameters.Source = source;
                config.QueryParameters.Q = query;
                config.QueryParameters.PersonId = personId;
                if (section is not null)
                {
                    config.QueryParameters.SectionId = section.SectionId;
                    config.QueryParameters.Scope = section.LibraryId is null ? GetScopeQueryParameterType.Home : GetScopeQueryParameterType.Library;
                    config.QueryParameters.LibraryId = section.LibraryId;
                }

                config.QueryParameters.Type = type;
                config.QueryParameters.Cursor = cursor;
                config.QueryParameters.Limit = limit;
                config.QueryParameters.PosterWidth = widths.Poster;
                config.QueryParameters.BackdropWidth = widths.Backdrop;
                config.QueryParameters.LogoWidth = widths.Logo;
                config.QueryParameters.StillWidth = widths.Still;
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var items = response?.Items ?? [];
            SearchContinuation? next = null;
            if (response?.Page?.HasMore == true && items.Count > 0)
            {
                var nextCursor = response.Page.NextCursor;
                // CatalogV2Api.kt rejects missing/repeated cursors, rather than looping the console.
                if (string.IsNullOrWhiteSpace(nextCursor) || !seen.Add(nextCursor))
                {
                    throw new InvalidDataException("Invalid catalog continuation.");
                }

                next = new SearchContinuation(nextCursor, seen);
            }

            var cards = items.Where(item => IsVisibleTvType(item.Type, showAudiobooks) && (section?.MatchesMediaScope(item.Type) ?? true))
                .Select(item => ToCard(item, CardShape.Poster) is var card && item.Type == "episode" && !string.IsNullOrWhiteSpace(item.Title)
                    ? card with { Title = item.Title!, Subtitle = item.Year is > 0 ? item.Year.Value.ToString(CultureInfo.InvariantCulture) : null }
                    : card)
                .ToList();
            if (cards.Count == 0 && next is not null)
            {
                cursor = next.Cursor;
                continue;
            }

            return new SearchResultPage(cards, response?.Total ?? cards.Count, response?.TotalExact == true, next);
        }
    }

    internal static bool IsVisibleTvType(string? type, bool showAudiobooks) => type?.Trim().ToLowerInvariant() switch
    {
        // TvMediaTypeFilters.kt / MediaMode.kt: TV has no ebook, comic or manga reader.
        "ebook" or "ebooks" or "book" or "books" or "comic" or "comics" or "manga" or "reading" => false,
        "audiobook" or "audiobooks" => showAudiobooks,
        _ => true,
    };

    /// <summary>
    /// SearchPeople.kt gates every lookup on people_media_scope; old servers otherwise leak people
    /// outside the viewer's libraries. A slow/failed people lookup never fails the title search.
    /// </summary>
    public async Task<IReadOnlyList<SearchPerson>> SearchPeopleAsync(string query, string? type = null,
        bool showAudiobooks = false, CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        try
        {
            var capabilities = await _api.Api.V2.Catalog.Search.Capabilities.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (capabilities?.PeopleMediaScope != true)
            {
                return [];
            }

            GetMedia_scopeQueryParameterType[] scopes = type switch
            {
                "movie" => [GetMedia_scopeQueryParameterType.Movie],
                "series" => [GetMedia_scopeQueryParameterType.Series],
                "audiobook" => [GetMedia_scopeQueryParameterType.Audiobook],
                _ when showAudiobooks => [GetMedia_scopeQueryParameterType.Video, GetMedia_scopeQueryParameterType.Audiobook],
                _ => [GetMedia_scopeQueryParameterType.Video],
            };
            var lists = await Task.WhenAll(scopes.Select(scope => SearchPeopleScopeAsync(query, scope, cancellationToken))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return MergePeople(query, lists.OfType<IReadOnlyList<SearchPerson>>().ToList());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<SearchPerson>?> SearchPeopleScopeAsync(string query, GetMedia_scopeQueryParameterType scope, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _api.Api.V2.Catalog.People.GetAsync(config =>
            {
                config.QueryParameters.Q = query;
                config.QueryParameters.MediaScope = scope;
                config.QueryParameters.Limit = 20;
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // CatalogV2Api.kt refuses partial envelopes on this bounded, non-pageable endpoint.
            if (response?.Page?.HasMore == true || !string.IsNullOrWhiteSpace(response?.Page?.NextCursor))
            {
                return null;
            }

            return (response?.Items ?? []).Where(person => !string.IsNullOrEmpty(person.Id))
                .Select(person => new SearchPerson(person.Id!, person.Name ?? string.Empty, Resolve(person.PhotoUrl))).ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<SearchPerson> MergePeople(string query, IReadOnlyList<IReadOnlyList<SearchPerson>> lists)
    {
        // SearchPeople.kt keeps a single scope's server ranking; multiple scopes rank exact names,
        // then name and numeric person ID, and deduplicate before taking the 20-card TV limit.
        if (lists.Count == 1)
        {
            return lists[0].Take(20).ToList();
        }

        return lists.SelectMany(list => list).DistinctBy(person => person.Id)
            .OrderBy(person => !person.Name.Trim().Equals(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenBy(person => person.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(person => long.TryParse(person.Id, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : long.MaxValue)
            .ThenBy(person => person.Id, StringComparer.Ordinal)
            .Take(20).ToList();
    }

    /// <summary>The libraries this profile can see, grouped into the top bar's content-type tabs.</summary>
    public async Task<IReadOnlyList<LibraryTab>> LoadLibraryTabsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _api.Api.V2.User.Libraries.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return LibraryTabs.From(response?.Items ?? []);
    }

    private IReadOnlyList<MediaRow> ToRows(IEnumerable<Section>? sections, string? libraryId = null) =>
        (sections ?? [])
            .Where(section => section.Items is { Count: > 0 })
            .Select(section => ToRow(section, libraryId))
            .ToList();

    internal MediaRow ToRow(Section section, string? libraryId = null)
    {
        // Servers without section_item_limit_max send whole sections; long rows cost memory on a console.
        var items = section.Items!.Take(CardsPerRow).ToList();
        var shape = IsProgressRow(section, items) ? CardShape.Landscape : CardShape.Poster;
        return new MediaRow(
            section.Id ?? string.Empty,
            section.Title ?? string.Empty,
            section.SectionType ?? string.Empty,
            section.Featured == true,
            shape,
            items.Select(item => ToCard(item, shape)).ToList())
        {
            ExploreSource = string.IsNullOrWhiteSpace(section.Id) ? null : new SectionBrowseSource(section.Id, section.Title ?? string.Empty, libraryId),
        };
    }

    /// <summary>Continue Watching / Next Up: the server marks their cards with item_source.</summary>
    internal static bool IsProgressRow(Section section, IReadOnlyCollection<CatalogItem> items) =>
        items.Any(item => !string.IsNullOrEmpty(item.ItemSource))
        || CardMenu.IsProgressRow(section.SectionType ?? string.Empty);

    internal MediaCard ToCard(CatalogItem item, CardShape shape)
    {
        var isEpisode = item.Type == "episode";
        var title = isEpisode && !string.IsNullOrEmpty(item.SeriesTitle) ? item.SeriesTitle! : item.Title ?? string.Empty;

        // Landscape cards prefer the episode still; the banner prefers series art over a still.
        var image = shape == CardShape.Landscape
            ? Resolve(item.BackdropUrl) ?? Resolve(item.SeriesBackdropUrl) ?? Resolve(item.PosterUrl)
            : Resolve(isEpisode ? item.SeriesPosterUrl ?? item.PosterUrl : item.PosterUrl);
        var backdrop = Resolve(isEpisode ? item.SeriesBackdropUrl ?? item.BackdropUrl : item.BackdropUrl);

        return new MediaCard(
            item.ContentId ?? string.Empty,
            item.Type ?? string.Empty,
            title,
            Subtitle(item, isEpisode),
            image,
            backdrop,
            Resolve(item.LogoUrl),
            item.Overview,
            Meta(item),
            Progress(item),
            RemainingMinutes(item),
            item.PlayContentId,
            item.SeriesId,
            Badges(item.OverlaySummary, item.ContentRating),
            item.Type == "episode" && item.SeasonNumber is { } season && item.EpisodeNumber is { } episode ? $"S{season} E{episode}" : null,
            item.Type == "episode" ? item.Title : null,
            Runtime(item.Runtime))
        {
            Flags = new Detail.ItemFlags(item.UserState?.InWatchlist == true, item.UserState?.IsFavorite == true, item.UserState?.Played == true),
            PositionSeconds = item.PositionSeconds,
            ProgressUpdatedAt = item.ProgressUpdatedAt,
            ContentRating = string.IsNullOrWhiteSpace(item.ContentRating) ? null : item.ContentRating!.Trim().ToUpperInvariant(),
            MarqueeFacts = MarqueeFacts(item, isEpisode),
            Rating = isEpisode ? null : Detail.DetailFeed.OutOfTen("imdb", "IMDb", item.RatingImdb) ?? Detail.DetailFeed.OutOfTen("tmdb", "TMDB", item.RatingTmdb),
            Genre = isEpisode ? null : (item.Genres ?? []).FirstOrDefault(genre => !string.IsNullOrWhiteSpace(genre)),
            SpecBadges = SpecBadges(item.OverlaySummary),
            CornerTop = new[] { PrettyResolution(item.OverlaySummary?.Resolution), Upper(item.OverlaySummary?.Hdr), Upper(item.OverlaySummary?.Audio) }.OfType<string>().ToList(),
            CornerBottom = new[] { Upper(item.OverlaySummary?.ReleaseType) }.OfType<string>().ToList(),
        };
    }

    private static IReadOnlyList<string> MarqueeFacts(CatalogItem item, bool isEpisode)
    {
        var facts = new List<string>();
        if (isEpisode)
        {
            if (EpisodeTag(item.SeasonNumber, item.EpisodeNumber) is { } tag)
            {
                facts.Add(tag);
            }

            if (!string.IsNullOrWhiteSpace(item.Title))
            {
                facts.Add(item.Title!);
            }
        }
        else
        {
            if (item.Year is > 0)
            {
                facts.Add(item.Year.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (Length(item.Runtime, item.DurationSeconds) is { } length)
            {
                facts.Add(length);
            }
        }

        return facts;
    }

    /// <summary>"S01E02" (tvEpisodeTag).</summary>
    internal static string? EpisodeTag(long? season, long? episode) =>
        season is null && episode is null ? null : $"{(season is { } s ? $"S{s:00}" : "")}{(episode is { } e ? $"E{e:00}" : "")}";

    /// <summary>"1h 36m", "2h", "52 min": the runtime, else the file's duration (lengthText).</summary>
    internal static string? Length(long? runtimeMinutes, double? durationSeconds)
    {
        var minutes = runtimeMinutes is > 0 ? runtimeMinutes.Value
            : durationSeconds is > 0 && double.IsFinite(durationSeconds.Value) ? (long)Math.Round(durationSeconds.Value / 60) : 0;
        return minutes switch
        {
            <= 0 => null,
            < 60 => $"{minutes} min",
            _ when minutes % 60 == 0 => $"{minutes / 60}h",
            _ => $"{minutes / 60}h {minutes % 60}m",
        };
    }

    /// <summary>"4K · DOLBY VISION · EAC3 5.1" as separate, uppercased badges (TvMarqueeContent.specLine).</summary>
    internal static IReadOnlyList<string> SpecBadges(CatalogItemOverlay? overlay)
    {
        var badges = new List<string>();
        if (overlay is null)
        {
            return badges;
        }

        if (PrettyResolution(overlay.Resolution) is { } resolution)
        {
            badges.Add(resolution);
        }

        if (overlay.Hdr?.Trim() is { Length: > 0 } hdr)
        {
            badges.Add(hdr.Contains("dv", StringComparison.OrdinalIgnoreCase) || hdr.Contains("dolby", StringComparison.OrdinalIgnoreCase) ? "DOLBY VISION" : hdr.ToUpperInvariant());
        }

        if (overlay.Audio?.Trim() is { Length: > 0 } audio)
        {
            var codec = audio.Contains("atmos", StringComparison.OrdinalIgnoreCase) ? "ATMOS" : audio.ToUpperInvariant();
            badges.Add(overlay.AudioChannels?.Trim() is { Length: > 0 } channels ? $"{codec} {channels.ToUpperInvariant()}" : codec);
        }

        return badges;
    }

    /// <summary>"4K" for 2160p, "8K" for 4320p, "1080P" otherwise (OverlayRegistry.prettyResolution, uppercased by the preset).</summary>
    internal static string? PrettyResolution(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "2160p" or "4k" or "uhd" => "4K",
        "4320p" or "8k" => "8K",
        var other => other.ToUpperInvariant(),
    };

    private static string? Upper(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim().ToUpperInvariant();

    /// <summary>Second caption line, as Silo's Android TV cards show it: the episode's own title, else the year.</summary>
    private static string? Subtitle(CatalogItem item, bool isEpisode)
    {
        if (isEpisode)
        {
            // "S01E01 • Pilot", as the official cards caption an episode.
            var tag = EpisodeTag(item.SeasonNumber, item.EpisodeNumber);
            var title = string.IsNullOrWhiteSpace(item.Title) ? null : item.Title;
            return tag is null ? title : title is null ? tag : $"{tag} • {title}";
        }

        return item.Year is > 0 ? item.Year.Value.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Marquee facts for a movie or series: "2025 · 1h 58m · Drama · Thriller". Rating is a badge instead.</summary>
    internal static string? Meta(CatalogItem item)
    {
        var parts = new List<string>();
        if (item.Year is > 0)
        {
            parts.Add(item.Year.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (Runtime(item.Runtime) is { } runtime)
        {
            parts.Add(runtime);
        }

        parts.AddRange((item.Genres ?? []).Take(2));
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    /// <summary>"52 min" under an hour, "1h 58m" over.</summary>
    internal static string? Runtime(long? minutes) => minutes switch
    {
        null or <= 0 => null,
        < 60 => $"{minutes} min",
        _ => $"{minutes / 60}h {minutes % 60}m",
    };

    /// <summary>
    /// The marquee's outlined badges, as Silo's Android TV app shows them: resolution, dynamic range,
    /// audio codec, then the content rating, e.g. "4K · HDR · EAC3 · TV-MA".
    /// </summary>
    internal static IReadOnlyList<string> Badges(CatalogItemOverlay? overlay, string? contentRating = null)
    {
        var badges = new List<string>();
        if (overlay?.Resolution == "2160p")
        {
            badges.Add("4K");
        }

        var hdr = overlay?.Hdr ?? string.Empty;
        if (hdr.Contains("DV", StringComparison.OrdinalIgnoreCase) || hdr.Contains("Dolby Vision", StringComparison.OrdinalIgnoreCase))
        {
            badges.Add("DOLBY VISION");
        }
        else if (hdr.Length > 0)
        {
            badges.Add("HDR");
        }

        var audio = overlay?.Audio ?? string.Empty;
        if (audio.Contains("atmos", StringComparison.OrdinalIgnoreCase))
        {
            badges.Add("ATMOS");
        }
        else if (audio.Length > 0)
        {
            badges.Add(audio.ToUpperInvariant());
        }

        if (!string.IsNullOrEmpty(contentRating))
        {
            badges.Add(contentRating!.ToUpperInvariant());
        }

        return badges;
    }

    private static int? RemainingMinutes(CatalogItem item) =>
        item.PositionSeconds is double position and > 0 && item.DurationSeconds is double duration && duration > position
            ? (int)Math.Ceiling((duration - position) / 60)
            : null;

    private static double? Progress(CatalogItem item) =>
        item.PositionSeconds is > 0 && item.DurationSeconds is > 0
            ? Math.Min(1, item.PositionSeconds.Value / item.DurationSeconds.Value)
            : null;

    /// <summary>Turns an artwork URL from the API (presigned, possibly relative) into one an image control can load.</summary>
    public Uri? Resolve(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && absolute.Scheme.StartsWith("http", StringComparison.Ordinal))
        {
            return absolute;
        }

        var root = new Uri(_server.ToString().TrimEnd('/') + "/");
        return new Uri(root, url!.TrimStart('/'));
    }
}
