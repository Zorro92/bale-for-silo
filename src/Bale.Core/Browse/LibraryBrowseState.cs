using System.Text;
using System.Text.Json;

namespace Bale.Core.Browse;

public sealed record LibraryBrowseRequest(int Generation, LibraryBrowseQuery Query, LibraryBrowseContinuation? Continuation, bool Append);

/// <summary>Only the newest filter generation may publish. Appending preserves existing card identities.</summary>
public sealed class LibraryBrowseState
{
    private readonly List<MediaCard> _items = [];
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    public int Generation { get; private set; }
    public LibraryBrowseQuery? Query { get; private set; }
    public IReadOnlyList<MediaCard> Items => _items;
    public LibraryBrowseContinuation? Continuation { get; private set; }
    public bool HasMore => Continuation is not null;
    public bool IsLoading { get; private set; }
    public bool IsLoadingMore { get; private set; }
    public string? Error { get; private set; }
    public bool? LibraryHasItems { get; private set; }

    public LibraryBrowseRequest Begin(LibraryBrowseQuery query)
    {
        Generation++;
        Query = query;
        _items.Clear();
        _ids.Clear();
        Continuation = null;
        Error = null;
        LibraryHasItems = null;
        IsLoading = true;
        IsLoadingMore = false;
        return new LibraryBrowseRequest(Generation, query, null, false);
    }

    public LibraryBrowseRequest? BeginMore()
    {
        if (Query is null || Continuation is null || IsLoading || IsLoadingMore || Error is not null) return null;
        IsLoadingMore = true;
        return new LibraryBrowseRequest(Generation, Query, Continuation, true);
    }

    public bool Apply(LibraryBrowseRequest request, LibraryBrowseResult result)
    {
        if (request.Generation != Generation) return false;
        foreach (var card in result.Cards)
        {
            if (_ids.Add(card.ContentId)) _items.Add(card);
        }

        Continuation = result.Continuation;
        if (!request.Append) LibraryHasItems = result.LibraryHasItems;
        IsLoading = IsLoadingMore = false;
        Error = null;
        return true;
    }

    public bool Fail(LibraryBrowseRequest request, Exception error)
    {
        if (request.Generation != Generation) return false;
        IsLoading = IsLoadingMore = false;
        Error = error.Message;
        return true;
    }

    public void UpdateCard(MediaCard card)
    {
        var index = _items.FindIndex(item => item.ContentId == card.ContentId);
        if (index >= 0) _items[index] = card;
    }
}

/// <summary>BrowsePrefsStore: preserve defaults ON; prefix is per-visit and must never be persisted.</summary>
public static class LibraryBrowsePreferences
{
    // Explicit JSON avoids runtime reflection under Native AOT and gives cursors a canonical query key.
    public static string Serialize(LibraryBrowseFilter filter)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("sort", filter.Sort.Field);
            writer.WriteBoolean("descending", filter.Descending);
            writer.WriteBoolean("matchAll", filter.Facets.MatchAll);
            writer.WriteStartObject("facets");
            foreach (var pair in filter.Facets.Values.OrderBy(pair => pair.Key))
            {
                if (pair.Value.Count == 0) continue;
                writer.WriteStartArray(pair.Key.ToString());
                foreach (var value in pair.Value.Order(StringComparer.Ordinal)) writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static LibraryBrowseFilter? Deserialize(string? json, string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var field = root.GetProperty("sort").GetString();
            var sort = LibrarySortOption.For(mediaType).FirstOrDefault(option => option.Field == field) ?? LibrarySortOption.DateAdded;
            var selection = new LibraryFacetSelection { MatchAll = root.GetProperty("matchAll").GetBoolean() };
            var available = LibraryFacets.For(mediaType);
            foreach (var property in root.GetProperty("facets").EnumerateObject())
            {
                if (!Enum.TryParse<LibraryFacet>(property.Name, out var facet) || !available.Contains(facet)) continue;
                foreach (var value in property.Value.EnumerateArray())
                {
                    if (value.GetString() is { Length: > 0 } text && !selection.Selected(facet).Contains(text)) selection = selection.Toggle(facet, text);
                }
            }

            return new LibraryBrowseFilter { Sort = sort, Descending = root.GetProperty("descending").GetBoolean(), Facets = selection };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
}
