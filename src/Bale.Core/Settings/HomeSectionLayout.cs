using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bale.Core.Browse;
using Bale.Core.Connection;

namespace Bale.Core.Settings;

/// <summary>
/// TvHomeSectionPreferences.kt: a device-local projection of populated server rows, scoped to
/// server/profile. The v2 Home endpoints are reads; profile/sections writes are profile-wide,
/// not the TV editor's device-only choice. Never send this local payload to those endpoints.
/// </summary>
public sealed record HomeSectionLayout
{
    public IReadOnlyList<string> OrderedSectionIds { get; init; } = [];
    public IReadOnlySet<string> HiddenSectionIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyList<MediaRow> Arrange(IReadOnlyList<MediaRow> sections, bool includingHidden = false)
    {
        var rank = Unique(OrderedSectionIds).Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index, StringComparer.Ordinal);
        return sections.Where(row => row.Cards.Count > 0 && (includingHidden || !HiddenSectionIds.Contains(row.Id)))
            .OrderBy(row => rank.GetValueOrDefault(row.Id, int.MaxValue)).ToList();
    }

    public bool AllHidden(IReadOnlyList<MediaRow> sections) =>
        sections.Any(row => row.Cards.Count > 0) && Arrange(sections).Count == 0;

    public HomeSectionLayout WithVisible(string sectionId, bool visible)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionId);
        var hidden = new HashSet<string>(HiddenSectionIds, StringComparer.Ordinal);
        if (visible) hidden.Remove(sectionId);
        else hidden.Add(sectionId);
        return this with { HiddenSectionIds = hidden };
    }

    // TvHomeSectionPreferences.retainedOrder: keep rows temporarily absent (e.g. empty Continue Watching).
    public HomeSectionLayout WithOrder(IEnumerable<string> sectionIds)
    {
        var known = Unique(sectionIds);
        var ids = known.ToHashSet(StringComparer.Ordinal);
        return this with { OrderedSectionIds = known.Concat(Unique(OrderedSectionIds).Where(id => !ids.Contains(id))).ToList() };
    }

    public HomeSectionLayout Move(IReadOnlyList<MediaRow> sections, string sectionId, int offset)
    {
        if (offset is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(offset));
        var ids = Arrange(sections, includingHidden: true).Select(row => row.Id).ToList();
        var index = ids.IndexOf(sectionId);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= ids.Count) return this;
        (ids[index], ids[target]) = (ids[target], ids[index]);
        return WithOrder(ids);
    }

    public static string StorageKey(Uri server, string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        // Length-prefixing prevents ambiguous server/profile pairs; no address or identity in the key.
        var url = ServerAddress.ToBaseUrl(server);
        var identity = $"{url.Length}:{url}{profileId.Length}:{profileId}";
        return "HomeSections." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    // Explicit mapping, not reflection-based serialization: Xbox uses Native AOT. This is a
    // local settings payload only, mirroring TvHomeSectionPreferences' retained order + hidden set.
    public string Serialize()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("orderedSectionIds");
            foreach (var id in Unique(OrderedSectionIds)) writer.WriteStringValue(id);
            writer.WriteEndArray();
            writer.WriteStartArray("hiddenSectionIds");
            foreach (var id in Unique(HiddenSectionIds).Order(StringComparer.Ordinal)) writer.WriteStringValue(id);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static HomeSectionLayout Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new HomeSectionLayout
            {
                OrderedSectionIds = Unique(ReadIds(root.GetProperty("orderedSectionIds"))),
                HiddenSectionIds = Unique(ReadIds(root.GetProperty("hiddenSectionIds"))).ToHashSet(StringComparer.Ordinal),
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            // A corrupt/old device preference must not hide Home or prevent entering Settings.
            return new();
        }
    }

    private static IEnumerable<string> ReadIds(JsonElement array) => array.EnumerateArray().Select(value => value.GetString() ?? string.Empty);
    private static List<string> Unique(IEnumerable<string> ids) => ids.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
}
