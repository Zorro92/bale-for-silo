using Bale.Core.Api.Models;
using Bale.Core.Profiles;

namespace Bale.Xbox.Services;

/// <summary>Carry the same household gate through picker/create/edit without changing the viewer.</summary>
/// <remarks>StartAdd opens the new-profile form (after the primary's PIN) once the picker loads: the profile menu's Add Profile.</remarks>
internal sealed record ProfileNavigation(IProfileRepository Repository, Func<string?, Uri?> ResolveAvatar, bool IsPreview = false,
    bool StartAdd = false);
internal sealed record ProfileEditorNavigation(ProfileNavigation Household, Profile? Profile = null);
