#if DEBUG_SERVER
using Bale.Core.Api.Models;
using Bale.Core.Profiles;

namespace Bale.Xbox.Services;

/// <summary>Disposable profile previews. Deliberately has no client, network, Session, vault or settings dependency.</summary>
internal sealed class ProfilePreview : IProfileRepository
{
    private readonly List<Profile> _profiles =
    [
        new() { Id = "fixture-primary", Name = "Alex", IsPrimary = true, HasPin = true, Avatar = "preset:dicebear:bottts-neutral:cosmic-otter" },
        new() { Id = "fixture-viewer", Name = "Sam", Avatar = "preset:dicebear:fun-emoji:pepper-pebble" },
        new() { Id = "fixture-child", Name = "Kids", IsChild = true, MaxContentRating = "PG", LibraryRestrictionsEnabled = true,
            AllowedLibraryIds = ["fixture-movies"], Avatar = "preset:dicebear:pixel-art-neutral:bubble-fox" },
    ];
    private readonly Dictionary<string, string> _pins = new() { ["fixture-primary"] = "1234" };
    private int _nextId;
    public HouseholdManagementSession Management { get; }
    public IReadOnlyList<Profile> Snapshot => _profiles.Select(Copy).ToArray();

    public ProfilePreview()
    {
        Management = new(VerifyPinAsync, () => 0);
        // The direct Manage/Create/Edit previews start unlocked; Done permits testing the PIN gate (1234).
        Management.Begin(_profiles[0], "fixture-proof");
    }

    public Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Snapshot);
    }

    public Task<IReadOnlyList<UserLibrary>> ListLibrariesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<UserLibrary>>([
            new() { Id = "fixture-movies", Name = "Movies", Type = "movies", SortOrder = 0 },
            new() { Id = "fixture-shows", Name = "TV Shows", Type = "shows", SortOrder = 1 },
            new() { Id = "fixture-books", Name = "Books", Type = "books", SortOrder = 2 },
        ]);
    }

    public Task<string?> VerifyPinAsync(string profileId, string pin, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(_pins.TryGetValue(profileId, out var expected) && pin == expected ? "fixture-proof" : null);
    }

    public Task<Profile> CreateAsync(ProfileCreate payload, CancellationToken cancellationToken = default)
    {
        RequireManagement(cancellationToken);
        var profile = new Profile
        {
            Id = $"fixture-created-{++_nextId}", Name = payload.Name?.Trim(), Avatar = payload.Avatar,
            HasPin = !string.IsNullOrEmpty(payload.Pin), IsChild = payload.IsChild == true,
            MaxContentRating = payload.MaxContentRating, LibraryRestrictionsEnabled = payload.LibraryRestrictionsEnabled == true,
            AllowedLibraryIds = payload.AllowedLibraryIds?.ToList(), SubtitleMode = payload.SubtitleMode,
        };
        if (profile.HasPin == true) _pins[profile.Id] = payload.Pin!;
        _profiles.Add(profile);
        return Task.FromResult(Copy(profile));
    }

    public Task<Profile> UpdateAsync(string profileId, ProfilePatch payload, CancellationToken cancellationToken = default)
    {
        RequireManagement(cancellationToken);
        var profile = _profiles.First(item => item.Id == profileId);
        profile.Name = payload.Name?.Trim() ?? profile.Name;
        if (payload.Avatar is not null) { profile.Avatar = payload.Avatar; profile.AvatarUrl = null; }
        if (payload.ClearPin) { profile.HasPin = false; _pins.Remove(profileId); }
        else if (payload.Pin is { Length: > 0 } pin) { profile.HasPin = true; _pins[profileId] = pin; }
        if (payload.IsChild is { } child) profile.IsChild = child;
        if (payload.ClearRating) profile.MaxContentRating = null;
        else if (payload.MaxContentRating is not null) profile.MaxContentRating = payload.MaxContentRating;
        if (payload.LibraryRestrictionsEnabled is { } restricted) profile.LibraryRestrictionsEnabled = restricted;
        if (payload.AllowedLibraryIds is not null) profile.AllowedLibraryIds = payload.AllowedLibraryIds.ToList();
        if (payload.SubtitleMode is not null) profile.SubtitleMode = payload.SubtitleMode;
        return Task.FromResult(Copy(profile));
    }

    public Task DeleteAsync(string profileId, CancellationToken cancellationToken = default)
    {
        RequireManagement(cancellationToken);
        var profile = _profiles.First(item => item.Id == profileId);
        if (!ProfileManagementGate.CanDelete(profile)) throw new InvalidOperationException("The primary profile cannot be deleted.");
        _profiles.Remove(profile);
        _pins.Remove(profileId);
        return Task.CompletedTask;
    }

    private void RequireManagement(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Management.IsActive) throw new InvalidOperationException("Enter the primary profile's PIN to manage profiles.");
    }

    private static Profile Copy(Profile profile) => new()
    {
        Id = profile.Id, Name = profile.Name, Avatar = profile.Avatar,
        AvatarUrl = ProfileAvatarPresets.Parse(profile.Avatar)?.ImageUrl.ToString(),
        HasPin = profile.HasPin, IsPrimary = profile.IsPrimary, IsChild = profile.IsChild,
        MaxContentRating = profile.MaxContentRating, LibraryRestrictionsEnabled = profile.LibraryRestrictionsEnabled,
        AllowedLibraryIds = profile.AllowedLibraryIds?.ToList(), QualityPreference = profile.QualityPreference, SubtitleMode = profile.SubtitleMode,
    };
}
#endif
