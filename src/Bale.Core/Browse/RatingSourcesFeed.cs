using Bale.Core.Api;
using Bale.Core.Api.Models;

namespace Bale.Core.Browse;

/// <summary>
/// model/feature/RatingSourcesStore.kt + network/api/RatingsCapabilityApi.kt: the rating sources the
/// server shows, from GET /api/v2/capabilities/ratings, read as the current profile. Sort lists offer
/// a rating sort (RT Critic, RT Audience) only while its source is shown. Empty until the first
/// answer, so a rating the server may hide is never offered early; a failed probe keeps the previous
/// answer; <see cref="Reset"/> clears it on a profile or session change and drops late answers.
/// </summary>
public sealed class RatingSourcesFeed(SiloApiClient api)
{
    private static readonly IReadOnlySet<string> None = new HashSet<string>();
    private readonly object _sync = new();
    private IReadOnlySet<string> _shown = None;
    private int _generation;

    public IReadOnlySet<string> Shown { get { lock (_sync) return _shown; } }

    public void Reset()
    {
        lock (_sync)
        {
            _generation++;
            _shown = None;
        }
    }

    /// <summary>Probes the capability. A failure throws and leaves the previous answer in place.</summary>
    public async Task<IReadOnlySet<string>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        int generation;
        lock (_sync) generation = _generation;
        var capability = await api.Api.V2.Capabilities.Ratings.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var shown = ShownSources(capability);
        lock (_sync)
        {
            if (generation == _generation) _shown = shown;
            return _shown;
        }
    }

    /// <summary>RatingsCapabilityApi.kt: the sources only while the capability's state is available.</summary>
    public static IReadOnlySet<string> ShownSources(RatingsCapability? capability) =>
        capability?.State == RatingsCapability_state.Available
            ? (capability.Sources ?? []).Select(source => source.Source).OfType<string>().Where(source => source.Length > 0).ToHashSet(StringComparer.Ordinal)
            : None;
}
