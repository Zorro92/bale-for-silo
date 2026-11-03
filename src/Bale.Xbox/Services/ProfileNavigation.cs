using Bale.Core.Api.Models;
using Bale.Core.Profiles;

namespace Bale.Xbox.Services;

/// <summary>Carry the same household gate through picker/create/edit without changing the viewer.</summary>
internal sealed record ProfileNavigation(IProfileRepository Repository, Func<string?, Uri?> ResolveAvatar, bool IsPreview = false);
internal sealed record ProfileEditorNavigation(ProfileNavigation Household, Profile? Profile = null);
