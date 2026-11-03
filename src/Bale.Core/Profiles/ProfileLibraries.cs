using Bale.Core.Api.Models;
using Bale.Core.Browse;

namespace Bale.Core.Profiles;

/// <summary>TvProfileAccessControls.kt shownOnTv: reading libraries stay hidden without reordering the server list.</summary>
public static class ProfileLibraries
{
    public static IReadOnlyList<UserLibrary> ShownOnTv(IEnumerable<UserLibrary> libraries) => libraries
        .Where(library => !string.IsNullOrEmpty(library.Id) && BrowseFeed.IsVisibleTvType(library.Type, showAudiobooks: true))
        .OrderBy(library => library.SortOrder ?? long.MaxValue).ToArray();
}
