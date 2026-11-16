using Bale.Core.Api;
using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Http.HttpClientLibrary.Middleware.Options;

namespace Bale.Core.Profiles;

public interface IProfileRepository
{
    HouseholdManagementSession Management { get; }
    Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UserLibrary>> ListLibrariesAsync(CancellationToken cancellationToken = default);
    Task<string?> VerifyPinAsync(string profileId, string pin, CancellationToken cancellationToken = default);
    Task<Profile> CreateAsync(ProfileCreate payload, CancellationToken cancellationToken = default);
    Task<Profile> UpdateAsync(string profileId, ProfilePatch payload, CancellationToken cancellationToken = default);
    Task DeleteAsync(string profileId, CancellationToken cancellationToken = default);
}

/// <summary>ProfileRepository.kt / ProfileApi.kt: v2 mutations as the ephemeral primary; account-only PIN and library reads.</summary>
public sealed class ProfileRepository : IProfileRepository
{
    private readonly SiloApiClient _api;
    private readonly Func<long> _accountGeneration;
    private readonly Action<string> _deleted;
    public ProfileRepository(SiloApiClient api, Func<long> accountGeneration, Action<string> deleted)
    {
        _api = api;
        _accountGeneration = accountGeneration;
        _deleted = deleted;
        Management = new(VerifyPinAsync, accountGeneration);
    }
    public HouseholdManagementSession Management { get; }

    public async Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default)
    {
        var collection = await _api.Api.V2.Profiles.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        MaxProfiles = collection?.MaxProfiles;
        return collection?.Items ?? [];
    }

    /// <summary>The account's profile limit from the last list (<c>max_profiles</c>); null when not sent.</summary>
    public long? MaxProfiles { get; private set; }

    public async Task<IReadOnlyList<UserLibrary>> ListLibrariesAsync(CancellationToken cancellationToken = default)
    {
        var generation = _accountGeneration();
        var response = await _api.Api.V2.User.Libraries.GetAsync(config =>
            config.Options.Add(new ProfileRequestOption(isCurrent: () => generation == _accountGeneration())), cancellationToken).ConfigureAwait(false);
        return response?.Items?.OrderBy(library => library.SortOrder).ToArray() ?? [];
    }

    public async Task<string?> VerifyPinAsync(string profileId, string pin, CancellationToken cancellationToken = default)
    {
        var generation = _accountGeneration();
        var verification = await _api.Api.V2.Profiles[profileId].VerifyPin.PostAsync(new ProfilePINCheck { Pin = pin }, config =>
            config.Options.Add(new ProfileRequestOption(isCurrent: () => generation == _accountGeneration())), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != _accountGeneration()) throw new OperationCanceledException("The profile management account changed.");
        return verification is { Valid: true, ProfileToken.Length: > 0 } && !string.IsNullOrWhiteSpace(verification.ProfileToken)
            ? verification.ProfileToken : null;
    }

    public Task<Profile> CreateAsync(ProfileCreate payload, CancellationToken cancellationToken = default) =>
        Management.RunAsync(async (id, token, generation) => await _api.Api.V2.Profiles.PostAsync(payload,
            config => Configure(config, id, token, generation), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Failed to create profile"), cancellationToken);

    public Task<Profile> UpdateAsync(string profileId, ProfilePatch payload, CancellationToken cancellationToken = default) =>
        Management.RunAsync(async (id, token, generation) => await _api.Api.V2.Profiles[profileId].PatchAsync(payload,
            config => Configure(config, id, token, generation), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Failed to update profile"), cancellationToken);

    public async Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        await Management.RunAsync(async (id, token, generation) =>
        {
            await _api.Api.V2.Profiles[profileId].DeleteAsync(config => Configure(config, id, token, generation), cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        // TvProfileSelectionViewModel.kt: clear a deleted selected profile before reloading the grid.
        _deleted(profileId);
    }

    private void Configure<T>(RequestConfiguration<T> config, string id, string? token, long generation) where T : class, new()
    {
        config.Options.Add(new ProfileRequestOption(id, token, () => generation == _accountGeneration()));
        config.Options.Add(new RetryHandlerOption { MaxRetry = 0 });
    }

    /// <summary>
    /// TvProfileSelectionViewModel.kt (65664d7ce): the TV has no form for a temporary password, so it
    /// says where to change it.
    /// </summary>
    public const string PasswordChangeRequiredMessage =
        "Your password needs to be reset. Use the Silo mobile app or website to set a new one, then come back.";

    /// <summary>
    /// The server's 403 <c>password_change_required</c>: the account holds a temporary password, and
    /// until it changes every route but the account read, the password change and sign-out refuses.
    /// </summary>
    public static bool IsPasswordChangeRequired(Exception error) => error switch
    {
        Problem { Type: { } type } => type.EndsWith("/password_change_required", StringComparison.Ordinal),
        ApiException { ResponseStatusCode: 403 } api => api.Message.Contains("password_change_required", StringComparison.Ordinal),
        _ => false,
    };

    public static string ErrorMessage(Exception error, string fallback) => error switch
    {
        HttpRequestException => "Network error. Please try again.",
        _ when IsPasswordChangeRequired(error) => PasswordChangeRequiredMessage,
        Problem { Detail: { Length: > 0 } detail } => detail,
        Problem { Title: { Length: > 0 } title } => title,
        _ => string.IsNullOrWhiteSpace(error.Message) ? fallback : error.Message,
    };
}
