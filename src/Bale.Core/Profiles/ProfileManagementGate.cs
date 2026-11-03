using Bale.Core.Api.Models;

namespace Bale.Core.Profiles;

public enum ProfileManagementAction { None, EnterPin, Manage, Create }

/// <summary>TvProfileSelectionViewModel.kt: Manage and Add share the primary gate, independent of account role.</summary>
public sealed class ProfileManagementGate(HouseholdManagementSession session)
{
    private bool _addAfterUnlock;
    public Profile? PinProfile { get; private set; }
    public bool IsManaging => session.IsActive;
    public static Profile? Primary(IReadOnlyList<Profile> profiles) => profiles.FirstOrDefault(profile => profile.IsPrimary == true)
        ?? (profiles.Count == 1 ? profiles[0] : null);
    public static bool CanManage(IReadOnlyList<Profile> profiles) => Primary(profiles) is not null;
    public static bool CanDelete(Profile profile) => profile.IsPrimary != true;

    public ProfileManagementAction Request(IReadOnlyList<Profile> profiles, bool add = false)
    {
        CancelPin();
        if (Primary(profiles) is not { } primary) return ProfileManagementAction.None;
        if (session.IsActive) return add ? ProfileManagementAction.Create : ProfileManagementAction.Manage;
        _addAfterUnlock = add;
        if (primary.HasPin == true)
        {
            PinProfile = primary;
            return ProfileManagementAction.EnterPin;
        }
        session.Begin(primary, null);
        return add ? ProfileManagementAction.Create : ProfileManagementAction.Manage;
    }

    public ProfileManagementAction CompletePin(string? token)
    {
        if (PinProfile is not { } primary || string.IsNullOrWhiteSpace(token)) return ProfileManagementAction.None;
        session.Begin(primary, token);
        var action = _addAfterUnlock ? ProfileManagementAction.Create : ProfileManagementAction.Manage;
        CancelPin();
        return action;
    }

    public void CancelPin() { PinProfile = null; _addAfterUnlock = false; }
    public void Done() { CancelPin(); session.End(); }
}
