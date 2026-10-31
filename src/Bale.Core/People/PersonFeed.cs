using Bale.Core.Api;
using Bale.Core.Api.Api.V2.Catalog;
using Bale.Core.Browse;
using Bale.Core.Detail;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.People;

/// <summary>TvPersonDetailViewModel.kt: profile, independently gated media chips, and cursor-paged works.</summary>
public sealed class PersonFeed(SiloApiClient api, BrowseFeed browse)
{
    public async Task<PersonProfile> GetAsync(string personId, CancellationToken cancellationToken = default)
    {
        var person = await api.Api.V2.Catalog.People[personId].GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The server returned no person.");
        return new PersonProfile(person.Id ?? personId, person.Name ?? string.Empty, browse.Resolve(person.PhotoUrl),
            PersonProfile.CleanBio(person.Bio), Date(person.BirthDate), Date(person.DeathDate), person.Birthplace);
    }

    public Task<SearchResultPage> WorksAsync(string personId, PersonMediaFilter filter, ArtworkWidths widths,
        SearchContinuation? continuation = null, bool showAudiobooks = true, CancellationToken cancellationToken = default) =>
        browse.PersonWorksPageAsync(personId, widths, filter.Type, continuation, showAudiobooks, cancellationToken);

    /// <summary>Both the viewer's libraries and the person's nonzero type totals gate optional chips; failed probes retain chips.</summary>
    public async Task<IReadOnlyList<PersonMediaFilter>> FiltersAsync(string personId, CancellationToken cancellationToken = default)
    {
        var libraryTask = LibraryTypesAsync(cancellationToken);
        var probes = PersonMediaFilter.Choices.Where(filter => filter.Type is not null).Select(async filter =>
        {
            try
            {
                var page = await api.Api.V2.Catalog.GetAsync(config =>
                {
                    config.QueryParameters.Source = GetSourceQueryParameterType.Person;
                    config.QueryParameters.PersonId = personId;
                    config.QueryParameters.Type = filter.Type;
                    config.QueryParameters.Limit = 1;
                }, cancellationToken).ConfigureAwait(false);
                return (Filter: filter, HasContent: page?.Total != 0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return (Filter: filter, HasContent: true);
            }
        });
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        var libraries = await libraryTask.ConfigureAwait(false);
        return PersonMediaFilter.Choices.Where(filter =>
            (filter.Type is null || results.Any(result => result.Filter == filter && result.HasContent))
            && (filter != PersonMediaFilter.Audiobooks || libraries is null || libraries.Any(type => type is "audiobook" or "audiobooks"))
            && (filter != PersonMediaFilter.Music || libraries is null || libraries.Any(type => type is "music" or "audio"))).ToList();
    }

    private async Task<IReadOnlyList<string>?> LibraryTypesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var libraries = await api.Api.V2.User.Libraries.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return libraries?.Items?.Select(library => (library.Type ?? string.Empty).ToLowerInvariant()).ToList();
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

    /// <summary>TvItemDetailViewModel.openPerson can resolve older cast credits by name; unresolved credits remain harmless.</summary>
    public async Task<string?> ResolveAsync(CastMember member, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(member.PersonId))
        {
            return member.PersonId;
        }

        try
        {
            var people = await api.Api.V2.Catalog.People.GetAsync(config =>
            {
                config.QueryParameters.Q = member.Name;
                config.QueryParameters.Limit = 20;
            }, cancellationToken).ConfigureAwait(false);
            if (people?.Page?.HasMore == true || !string.IsNullOrWhiteSpace(people?.Page?.NextCursor))
            {
                return null;
            }

            return (people?.Items?.FirstOrDefault(person => string.Equals(person.Name, member.Name, StringComparison.OrdinalIgnoreCase))
                ?? people?.Items?.FirstOrDefault())?.Id;
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

    private static DateOnly? Date(Date? date) => date is { } value ? new DateOnly(value.Year, value.Month, value.Day) : null;
}
