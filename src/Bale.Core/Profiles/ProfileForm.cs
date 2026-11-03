using Bale.Core.Api.Models;
using Microsoft.Kiota.Abstractions.Serialization;

namespace Bale.Core.Profiles;

/// <summary>TvCreateProfileViewModel.kt / TvEditProfileViewModel.kt, independent of XAML and transport.</summary>
public sealed class ProfileForm
{
    private readonly ProfileAccessLimits? _loadedAccess;
    private readonly bool _loadedHasPin;

    public ProfileForm(Profile? profile = null)
    {
        ProfileId = profile?.Id;
        Name = profile?.Name ?? string.Empty;
        SelectedAvatar = profile?.Avatar;
        SelectedAvatarUrl = profile?.AvatarUrl;
        var preset = ProfileAvatarPresets.Parse(SelectedAvatar);
        AvatarStyleId = preset?.StyleId ?? ProfileAvatarPresets.DefaultStyleId;
        SelectedAvatarSeed = preset?.Seed;
        _loadedAccess = profile is null ? null : ProfileAccessLimits.From(profile);
        Access = _loadedAccess ?? new();
        PinEnabled = _loadedHasPin = profile?.HasPin == true;
        QualityPreference = profile?.QualityPreference;
        SubtitleMode = profile?.SubtitleMode;
    }

    public string? ProfileId { get; }
    public bool IsEditing => ProfileId is not null;
    public string Name { get; set; }
    public string? SelectedAvatar { get; private set; }
    public string? SelectedAvatarUrl { get; private set; }
    public string AvatarStyleId { get; private set; }
    public string? SelectedAvatarSeed { get; private set; }
    public int AvatarBatch { get; private set; }
    public ProfileAccessLimits Access { get; set; }
    public bool PinEnabled { get; private set; }
    public string Pin { get; private set; } = string.Empty;
    public string? QualityPreference { get; private set; }
    public string? SubtitleMode { get; private set; }
    public string EffectiveAvatar => ProfileAvatarPresets.EffectiveReference(AvatarStyleId, SelectedAvatarSeed, AvatarBatch, Name, SelectedAvatar);
    public string? PreviewAvatarUrl => EffectiveAvatar == SelectedAvatar ? SelectedAvatarUrl : null;
    public string ActivePresetSeed => SelectedAvatarSeed ?? ProfileAvatarPresets.Parse(SelectedAvatar)?.Seed ?? ProfileAvatarPresets.Batch(AvatarStyleId, AvatarBatch)[0].Seed;

    public void SelectStyle(string styleId)
    {
        AvatarStyleId = styleId;
        AvatarBatch = 0;
        ClearAvatar();
    }

    public void SelectAvatar(ProfileAvatarPresets.Preset preset)
    {
        AvatarStyleId = preset.StyleId;
        SelectedAvatar = preset.Reference;
        SelectedAvatarSeed = preset.Seed;
        SelectedAvatarUrl = null;
    }

    public void Shuffle()
    {
        AvatarBatch++;
        ClearAvatar();
    }

    private void ClearAvatar() => (SelectedAvatar, SelectedAvatarUrl, SelectedAvatarSeed) = (null, null, null);
    public void TogglePin(bool enabled)
    {
        PinEnabled = enabled;
        if (!enabled) Pin = string.Empty;
    }

    // TvProfileForm.kt: a blank confirmation keeps the enabled flag; Remove is the deliberate clear.
    public void ConfirmPin(string value)
    {
        Pin = new string(value.Where(char.IsDigit).Take(4).ToArray());
        if (Pin.Length > 0) PinEnabled = true;
    }

    public void SelectQuality(string value) => QualityPreference = value.Trim().ToLowerInvariant() switch
    {
        "" or "auto" or "original" or "source" or "max" => null,
        "4k" or "2160p" => "2160p",
        var quality => quality,
    };
    public void SelectSubtitleMode(string value) => SubtitleMode = value.ToLowerInvariant().Replace(' ', '_');

    public string? Validate(IReadOnlyList<Profile> profiles, IReadOnlySet<string>? assignable = null)
    {
        if (string.IsNullOrWhiteSpace(Name)) return "Profile name is required";
        if (profiles.Any(profile => profile.Id != ProfileId && string.Equals(profile.Name?.Trim(), Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            return "A profile with this name already exists";
        if (PinEnabled && (!IsEditing || Pin.Length > 0) && Pin.Length != 4) return "PIN must be 4 digits";
        if (Access.NeedsLibrary(_loadedAccess, assignable)) return "Select at least one library for this profile.";
        return null;
    }

    /// <summary>The v1-shaped VM value: new digits set, explicit empty removes, null keeps. ProfileApi.kt maps the clear to v2 null.</summary>
    public string? PinValue => PinEnabled && Pin.Length > 0 ? Pin : !PinEnabled && _loadedHasPin ? "" : null;

    public ProfileCreate BuildCreate() => new()
    {
        Name = Name,
        Avatar = EffectiveAvatar,
        Pin = PinEnabled ? Pin : null,
        IsChild = Access.IsChild ? true : null,
        MaxContentRating = Access.MaxContentRating,
        LibraryRestrictionsEnabled = Access.LibraryRestrictionsEnabled ? true : null,
        AllowedLibraryIds = Access.LibraryRestrictionsEnabled ? Access.AllowedLibraryIds.Order(StringComparer.Ordinal).ToList() : null,
        SubtitleMode = SubtitleMode,
    };

    public ProfilePatch BuildUpdate(IReadOnlySet<string>? assignable = null)
    {
        var loaded = _loadedAccess ?? throw new InvalidOperationException("A new profile is not an edit.");
        var ratingChanged = Access.MaxContentRating != loaded.MaxContentRating;
        return new()
        {
            Name = Name,
            Avatar = EffectiveAvatar,
            Pin = string.IsNullOrEmpty(PinValue) ? null : PinValue,
            ClearPin = PinValue == "",
            IsChild = Access.IsChild,
            MaxContentRating = ratingChanged ? Access.MaxContentRating : null,
            ClearRating = ratingChanged && Access.MaxContentRating is null,
            LibraryRestrictionsEnabled = Access.LibraryRestrictionsEnabled != loaded.LibraryRestrictionsEnabled ? Access.LibraryRestrictionsEnabled : null,
            AllowedLibraryIds = Access.AllowlistChangedFrom(loaded)
                ? Access.LibraryRestrictionsEnabled
                    ? Access.AllowedLibraryIds.Where(id => assignable is null || assignable.Contains(id)).Order(StringComparer.Ordinal).ToList() : []
                : null,
            QualityPreference = QualityPreference,
            SubtitleMode = SubtitleMode,
        };
    }
}

/// <summary>ProfileApi.kt toProfileUpdate: preserve omission versus an explicit v2 PATCH null without editing generated models.</summary>
public sealed class ProfilePatch : ProfileUpdate
{
    public bool ClearPin { get; init; }
    public bool ClearRating { get; init; }
    public override void Serialize(ISerializationWriter writer)
    {
        base.Serialize(writer);
        if (ClearPin) writer.WriteNullValue("pin");
        if (ClearRating) writer.WriteNullValue("max_content_rating");
    }
}
