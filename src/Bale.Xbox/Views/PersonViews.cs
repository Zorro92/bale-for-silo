using Bale.Core.People;

namespace Bale.Xbox.Views;

public sealed partial class PersonBadgeView(string text)
{
    public string Text => text;
}

public sealed partial class PersonFilterView(PersonMediaFilter filter, bool selected)
{
    public PersonMediaFilter Filter => filter;
    public string Label => filter.Label;
    public bool Selected => selected;
}

/// <summary>
/// Per-stack-entry state, not a per-page-type navigation cache: Person → Detail → another Person
/// must not overwrite the first person's filmography or return target (TvPersonDetailScreen.kt).
/// </summary>
internal sealed class PersonPageEntry(string personId)
{
    public string PersonId { get; } = personId;
    public PersonProfile? Profile { get; set; }
    public PersonWorks Works { get; } = new();
    public IReadOnlyList<PersonMediaFilter> Filters { get; set; } = [PersonMediaFilter.All, PersonMediaFilter.Movies, PersonMediaFilter.Series];
    public bool FiltersLoaded { get; set; }
    public bool ShowAudiobooks { get; set; } = true;
    public string? ReturnItemId { get; set; }
}
