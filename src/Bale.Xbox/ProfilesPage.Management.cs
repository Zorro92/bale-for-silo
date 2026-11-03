using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Bale.Core.Profiles;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox;

public sealed partial class ProfilesPage
{
    private readonly ProfileManagementPrompt _reverify;
    private ProfileNavigation? _household;
    private ProfileManagementGate? _gate;
    private IReadOnlyList<Profile> _profiles = [];
    private ProfileView? _deleteCandidate;
    private CancellationTokenSource? _mutation;
    private Control? _lastAction;
    private bool _focusInUtilities;
    private bool _lastDelete;
    private bool _pinForManagement;
    private bool _deleting;
    internal bool IsProfilePreview => _household?.IsPreview == true;
    private bool IsCurrent => _active && _household is { } household
        && (household.IsPreview || ReferenceEquals(Session.Client?.Profiles, household.Repository));

    private ProfileView ViewFor(Profile profile) => new(profile, ProfileAvatars.ImageUrl(profile.AvatarUrl, _household!.ResolveAvatar));

    private void RebuildProfiles(bool restoreFocus)
    {
        var previous = Profiles.Items.ElementAtOrDefault(_lastProfileIndex) as ProfileView;
        if (!ProfileManagementGate.CanManage(_profiles)) _gate?.Done();
        var managing = _gate?.IsManaging == true;
        var views = _profiles.Select(profile => new ProfileView(profile,
            ProfileAvatars.ImageUrl(profile.AvatarUrl, _household!.ResolveAvatar), managing)).ToList();
        var canManage = ProfileManagementGate.CanManage(_profiles);
        if (canManage) views.Add(new(new Profile(), null, managing, isAdd: true));
        Profiles.ItemsSource = views;
        Profiles.Width = Math.Min(6, Math.Max(1, views.Count)) * 142;
        ManageButton.Visibility = canManage ? Visibility.Visible : Visibility.Collapsed;
        ManageLabel.Text = managing ? "Done" : "Manage";
        Windows.UI.Xaml.Automation.AutomationProperties.SetName(ManageButton, ManageLabel.Text);
        ManageIconBox.Visibility = managing ? Visibility.Collapsed : Visibility.Visible;
        var selectedId = IsProfilePreview ? null : Session.Client?.ProfileId ?? AppSettings.ProfileId;
        var index = restoreFocus && previous is not null
            ? views.FindIndex(view => previous.IsAdd ? view.IsAdd : !view.IsAdd && view.Profile.Id == previous.Profile.Id)
            : views.FindIndex(view => !view.IsAdd && view.Profile.Id == selectedId);
        _lastProfileIndex = index >= 0 ? index : Math.Clamp(_lastProfileIndex, 0, Math.Max(0, views.Count - 1));
        _lastDelete &= views.ElementAtOrDefault(_lastProfileIndex)?.DeleteVisibility == Visibility.Visible;
    }

    // TvProfileSelectionScreen (pinOpenedBy): closing a PIN prompt, whether it was
    // accepted or cancelled, puts focus back on the profile it asked about. The
    // manage gate asks for the primary profile's PIN, so that tile, not Manage.
    private void ReturnFocusToProfile(string? id)
    {
        var index = Profiles.Items.IndexOf(Profiles.Items.OfType<ProfileView>().FirstOrDefault(view => !view.IsAdd && view.Profile.Id == id));
        if (index < 0) return;
        _lastProfileIndex = index;
        _lastDelete = _focusInUtilities = false;
    }

    public void RestoreFocus()
    {
        if (!_active || PinPrompt.IsOpen || DeletePrompt.IsOpen || SignOutPrompt.IsOpen || RememberPanel.Visibility == Visibility.Visible) return;
        if (_focusInUtilities || Profiles.Items.Count == 0)
        {
            FocusRetry.Until(PickerActions, () => !_active || PickerPanel.Visibility != Visibility.Visible
                || (_lastAction is { Visibility: Visibility.Visible } action ? action.Focus(FocusState.Programmatic) : _actions.Focus()));
        }
        else RestoreProfileFocus();
    }

    private static ContentControl? ProfileContainer(DependencyObject? element)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ContentControl { Content: ProfileView } container) return container;
        return null;
    }

    private void OnManageClick(object sender, RoutedEventArgs args)
    {
        if (_deleting || !IsCurrent || _gate is null) return;
        if (_gate.IsManaging)
        {
            _gate.Done();
            DeletePrompt.Hide();
            _deleteCandidate = null;
            RebuildProfiles(restoreFocus: true);
            RestoreFocus();
        }
        else ApplyManagementAction(_gate.Request(_profiles));
    }

    private void ApplyManagementAction(ProfileManagementAction action)
    {
        if (action == ProfileManagementAction.EnterPin && _gate?.PinProfile is { } primary)
        {
            OpenPin(ViewFor(primary), management: true);
            return;
        }
        if (action is not (ProfileManagementAction.Manage or ProfileManagementAction.Create)) return;
        RebuildProfiles(restoreFocus: true);
        if (action == ProfileManagementAction.Create) OpenEditor();
        else RestoreFocus();
    }

    private void OpenEditor(Profile? profile = null)
    {
        if (_household is null || _gate?.IsManaging != true || _deleting || !IsCurrent) return;
        Frame.Navigate(typeof(ProfileEditorPage), new ProfileEditorNavigation(_household, profile));
    }

    private void OnDeleteProfileClick(object sender, RoutedEventArgs args)
    {
        if (_deleting || !IsCurrent || _gate?.IsManaging != true || sender is not FrameworkElement { DataContext: ProfileView view }
            || view.IsAdd || !ProfileManagementGate.CanDelete(view.Profile)) return;
        _lastProfileIndex = Math.Max(0, Profiles.Items.IndexOf(view));
        _lastDelete = true;
        _focusInUtilities = false;
        _deleteCandidate = view;
        DeletePrompt.ShowDeleteProfile(view.Name);
    }

    private async void OnDeleteConfirmed()
    {
        if (_deleteCandidate is not { Profile.Id: { } id } candidate || _deleting || !IsCurrent || _household is not { } household) return;
        _deleteCandidate = null;
        _deleting = true;
        using var mutation = new CancellationTokenSource();
        _mutation = mutation;
        var selected = !household.IsPreview && Session.Client?.ProfileId == id;
        try
        {
            await household.Repository.DeleteAsync(id, mutation.Token);
            if (!IsCurrent || mutation.IsCancellationRequested || !ReferenceEquals(_household, household)) return;
            if (!household.IsPreview)
            {
                // Core cleared the live deleted selection before the following reload. Clear any remembered pick too.
                if (AppSettings.ProfileId == id)
                {
                    AppSettings.ProfileId = null;
                    AppSettings.ProfileName = null;
                    AppSettings.ProfileAvatar = null;
                }
                if (selected) { Session.ProfileName = null; Session.ProfileAvatar = null; }
                Session.Persist();
            }
            await LoadAsync(restoreFocus: true);
        }
        catch (OperationCanceledException) when (mutation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error("Profile delete", ex);
            if (IsCurrent && ReferenceEquals(_household, household))
            {
                RebuildProfiles(restoreFocus: true);
                ShowError(ProfileRepository.ErrorMessage(ex, $"Failed to delete {candidate.Name}"));
            }
        }
        finally
        {
            if (ReferenceEquals(_mutation, mutation))
            {
                _mutation = null;
                _deleting = false;
                RestoreFocus();
            }
        }
    }
}
