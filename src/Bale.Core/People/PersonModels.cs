using System.Globalization;
using Bale.Core.Browse;

namespace Bale.Core.People;

/// <summary>PersonHeader in TvPersonDetailScreen.kt; no homepage/provider badges are shown on TV.</summary>
public sealed record PersonProfile(string Id, string Name, Uri? PhotoUrl, string? Bio, DateOnly? BirthDate, DateOnly? DeathDate, string? Birthplace)
{
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word => char.ToUpperInvariant(word[0]))) is { Length: > 0 } initials ? initials : "?";

    public IReadOnlyList<string> Metadata(DateOnly today)
    {
        var badges = new List<string>();
        var english = CultureInfo.GetCultureInfo("en-US");
        if (BirthDate is { } birth)
        {
            badges.Add("Born " + birth.ToString("MMM d, yyyy", english));
        }

        var age = Age(BirthDate, DeathDate ?? today);
        if (DeathDate is { } death)
        {
            badges.Add("Died " + death.ToString("MMM d, yyyy", english) + (age is null ? string.Empty : $" (age {age})"));
        }
        else if (age is not null)
        {
            badges.Add($"{age} years old");
        }

        if (!string.IsNullOrWhiteSpace(Birthplace))
        {
            badges.Add(Birthplace.Trim());
        }

        return badges;
    }

    internal static int? Age(DateOnly? birthDate, DateOnly end)
    {
        if (birthDate is not { } birth)
        {
            return null;
        }

        var age = end.Year - birth.Year;
        if (end.Month < birth.Month || (end.Month == birth.Month && end.Day < birth.Day))
        {
            age--;
        }

        return age >= 0 ? age : null;
    }

    /// <summary>cleanPersonBio strips TMDB's Wikipedia attribution and trailing truncation markers, not genuine sentence punctuation.</summary>
    public static string? CleanBio(string? raw)
    {
        var text = raw?.Trim() ?? string.Empty;
        var attribution = text.IndexOf("Description above from the Wikipedia", StringComparison.OrdinalIgnoreCase);
        if (attribution > 0)
        {
            text = text[..attribution].Trim();
        }

        if (text.EndsWith('…') || text.EndsWith("...", StringComparison.Ordinal))
        {
            var end = text.Length;
            while (end > 0 && (text[end - 1] is '.' or '…' || char.IsWhiteSpace(text[end - 1])))
            {
                end--;
            }

            text = text[..end];
            if (text.Length > 0 && char.IsLetterOrDigit(text[^1]))
            {
                text += ".";
            }
        }

        return text.Length > 0 ? text : null;
    }
}

public sealed record PersonMediaFilter(string Label, string? Type)
{
    public static PersonMediaFilter All { get; } = new("All", null);
    public static PersonMediaFilter Movies { get; } = new("Movies", "movie");
    public static PersonMediaFilter Series { get; } = new("Series", "series");
    public static PersonMediaFilter Audiobooks { get; } = new("Audiobooks", "audiobook");
    public static PersonMediaFilter Music { get; } = new("Music", "music");
    public static IReadOnlyList<PersonMediaFilter> Choices { get; } = [All, Movies, Series, Audiobooks, Music];
}

public sealed record PersonWorksRequest(int Generation, PersonMediaFilter Filter, SearchContinuation? Continuation, bool Append);

/// <summary>TvPersonDetailViewModel's filter generation fence, retryable paging and deduplicated filmography.</summary>
public sealed class PersonWorks
{
    private readonly List<MediaCard> _items = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private int _generation;

    public PersonMediaFilter Filter { get; private set; } = PersonMediaFilter.All;
    public IReadOnlyList<MediaCard> Items => _items;
    public SearchContinuation? Continuation { get; private set; }
    public bool HasMore => Continuation is not null;
    public bool IsLoading { get; private set; }
    public bool HasLoaded { get; private set; }
    public string? Error { get; private set; }
    public long Total { get; private set; }
    public string? CountLabel => Total > 0 ? Total == 1 ? "1 title" : $"{Total} titles"
        : _items.Count == 0 ? null : HasMore ? $"{_items.Count}+ titles" : _items.Count == 1 ? "1 title" : $"{_items.Count} titles";

    /// <summary>Keep card actions in the retained route snapshot without resetting filmography focus/paging.</summary>
    public void UpdateCard(MediaCard card)
    {
        var index = _items.FindIndex(item => item.ContentId == card.ContentId);
        if (index >= 0) _items[index] = card;
    }

    public PersonWorksRequest Begin(PersonMediaFilter filter)
    {
        _generation++;
        Filter = filter;
        _items.Clear();
        _ids.Clear();
        Continuation = null;
        Error = null;
        Total = 0;
        HasLoaded = false;
        IsLoading = true;
        return new PersonWorksRequest(_generation, filter, null, false);
    }

    public PersonWorksRequest? BeginMore(bool retry = false)
    {
        if (IsLoading || !HasMore || (Error is not null && !retry))
        {
            return null;
        }

        IsLoading = true;
        Error = null;
        return new PersonWorksRequest(_generation, Filter, Continuation, true);
    }

    public bool Apply(PersonWorksRequest request, SearchResultPage page)
    {
        if (request.Generation != _generation)
        {
            return false;
        }

        var before = _items.Count;
        _items.AddRange(page.Cards.Where(card => _ids.Add(card.ContentId)));
        Continuation = page.Continuation;
        Total = page.Total;
        IsLoading = false;
        HasLoaded = true;
        Error = request.Append && page.HasMore && before == _items.Count ? "Couldn't load more works. Try again." : null;
        return true;
    }

    /// <summary>Leaving the route cancels transport, but also fences transports that complete despite cancellation.</summary>
    public void StopLoading()
    {
        _generation++;
        IsLoading = false;
    }

    public bool Fail(PersonWorksRequest request)
    {
        if (request.Generation != _generation)
        {
            return false;
        }

        IsLoading = false;
        Error = "Couldn't load works. Try again.";
        return true;
    }
}
