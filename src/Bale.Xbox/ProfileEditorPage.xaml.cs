using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Bale.Core.Profiles;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>TvProfileForm.kt, TvCreateProfileViewModel.kt and TvEditProfileViewModel.kt.</summary>
public sealed partial class ProfileEditorPage : Page, IBackHandler, IRestoresFocus
{
    private readonly Rail _stylesRail;
    private readonly Rail _ratingsRail;
    private readonly Rail _librariesRail;
    private readonly ProfileManagementPrompt _reverify;
    private ProfileNavigation? _household;
    private ProfileForm _form = new();
    private IReadOnlyList<Profile> _profiles = [];
    private List<ProfileChoiceView> _styles = [];
    private List<ProfileChoiceView> _ratings = [];
    private List<ProfilePresetView> _presets = [];
    private List<ProfileLibraryView> _libraries = [];
    private IReadOnlySet<string>? _assignable;
    private CancellationTokenSource? _lifetime;
    private Control? _lastFocus;
    private int _lastAvatar;
    private bool _active;
    private bool _saving;
    private bool _editingPin;
    private bool _loadingLibraries;
    private bool _librariesLoaded;
    private string? _librariesError;
    internal bool IsProfilePreview => _household?.IsPreview == true;
    private bool IsCurrent => _active && _household is { } household
        && (household.IsPreview || ReferenceEquals(Session.Client?.Profiles, household.Repository));

    public ProfileEditorPage()
    {
        InitializeComponent();
        _stylesRail = Rail.Attach(Styles);
        _ratingsRail = Rail.Attach(Ratings);
        _librariesRail = Rail.Attach(LibraryModes);
        _stylesRail.Pin = _ratingsRail.Pin = _librariesRail.Pin = false;
        _stylesRail.EntryItem = () => _styles.FirstOrDefault(style => style.Selected);
        _ratingsRail.EntryItem = () => _ratings.FirstOrDefault(rating => rating.Selected);
        _librariesRail.EntryItem = () => _form.Access.LibraryRestrictionsEnabled ? ChooseLibraries : AllLibraries;
        _reverify = new(PinPrompt, profile => new ProfileView(profile, ProfileAvatars.ImageUrl(profile.AvatarUrl, _household!.ResolveAvatar)));
        TextPrompt.Confirmed += OnTextConfirmed;
        foreach (var (button, label) in new (Button, TextBlock)[] {
            (CancelButton, CancelLabel), (ShuffleButton, ShuffleLabel), (RemovePinButton, RemovePinLabel), (RetryLibraries, RetryLibrariesLabel) })
        {
            // The Focused state inks the ContentPresenter black, but each icon binds to its label's
            // own Foreground, which inheritance doesn't change: set it so the glyph flips with the text
            // (TvHeroActionPill.kt draws icon and label in one content color).
            button.GotFocus += (_, _) => label.Foreground = new SolidColorBrush(Colors.Black);
            button.LostFocus += (_, _) => label.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs args)
    {
        base.OnNavigatedTo(args);
        if (args.Parameter is not ProfileEditorNavigation request) return;
        _active = true;
        _household = request.Household;
        var lifetime = _lifetime = new();
        _reverify.Activate(_household.Repository.Management);
        _form = new(request.Profile);
        SetupForm();
        _ = LoadLibrariesAsync();
        if (request.Profile is not null)
        {
            EditorLayout.Visibility = Visibility.Collapsed;
            LoadingRing.Visibility = Visibility.Visible;
            LoadingRing.IsActive = true;
        }
        try
        {
            _profiles = await _household.Repository.ListAsync(lifetime.Token);
            if (!IsCurrent) return;
            if (request.Profile is { } selected)
            {
                if (_profiles.FirstOrDefault(profile => profile.Id == selected.Id) is { } loaded)
                {
                    _form = new(loaded);
                    SetupForm();
                }
                else SetError("Profile not found");
            }
        }
        catch (OperationCanceledException) when (!IsCurrent || lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error("Profile editor load", ex);
            // Create's duplicate pre-check is best effort; the server still enforces uniqueness.
            if (IsCurrent && request.Profile is not null) SetError(ProfileRepository.ErrorMessage(ex, "Failed to load profile"));
        }
        finally
        {
            if (IsCurrent)
            {
                EditorLayout.Visibility = Visibility.Visible;
                LoadingRing.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = false;
                FocusRetry.Until(Styles, () => !IsCurrent || _stylesRail.Focus());
            }
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs args)
    {
        _active = false;
        _lifetime?.Cancel();
        _reverify.Deactivate();
        if (args.SourcePageType != typeof(ProfilesPage)) _household?.Repository.Management.End();
        TextPrompt.Hide(restoreFocus: false);
        PinPrompt.Hide(restoreFocus: false);
        base.OnNavigatedFrom(args);
    }

    private void SetupForm()
    {
        Title.Text = _form.IsEditing ? "Edit Profile" : "New Profile";
        Subtitle.Text = _form.IsEditing ? "Pick a look and update this profile." : "Pick a look and give it a name.";
        SubmitLabel.Text = _form.IsEditing ? "Save Changes" : "Create Profile";
        CancelButton.Visibility = _form.IsEditing ? Visibility.Collapsed : Visibility.Visible;
        _styles = ProfileAvatarPresets.Styles.Select(style => new ProfileChoiceView(style.Id, style.Label)).ToList();
        Styles.ItemsSource = _styles;
        _ratings = ProfileAccessLimits.RatingOptions(_form.Access.MaxContentRating).Select(rating => new ProfileChoiceView(rating, rating ?? "Any")).ToList();
        Ratings.ItemsSource = _ratings;
        PopulatePresets();
        RefreshIdentity();
        RefreshAccess();
    }

    private void PopulatePresets()
    {
        var initials = _form.AvatarStyleId == "initials";
        InitialsHint.Visibility = initials ? Visibility.Visible : Visibility.Collapsed;
        Avatars.Visibility = ShuffleButton.Visibility = initials ? Visibility.Collapsed : Visibility.Visible;
        _presets = ProfileAvatarPresets.Batch(_form.AvatarStyleId, _form.AvatarBatch).Select(preset => new ProfilePresetView(preset)).ToList();
        Avatars.ItemsSource = _presets;
        _lastAvatar = Math.Max(0, _presets.FindIndex(preset => preset.Preset.Seed == _form.ActivePresetSeed));
    }

    private void RefreshIdentity()
    {
        foreach (var style in _styles) style.Update(style.Key == _form.AvatarStyleId);
        foreach (var preset in _presets) preset.Update(_form.ActivePresetSeed);
        PreviewName.Text = string.IsNullOrWhiteSpace(_form.Name) ? "Your Name" : _form.Name;
        var tint = ProfileAvatarPresets.PreviewTint(PreviewName.Text, _form.EffectiveAvatar);
        PreviewArt.Background = new SolidColorBrush(Color.FromArgb((byte)(tint >> 24), (byte)(tint >> 16), (byte)(tint >> 8), (byte)tint));
        PreviewHint.Visibility = string.IsNullOrWhiteSpace(_form.Name) ? Visibility.Visible : Visibility.Collapsed;
        PreviewInitial.Text = string.IsNullOrWhiteSpace(_form.Name) ? "Y" : _form.Name[..1].ToUpperInvariant();
        var url = ProfileAvatars.ImageUrl(_form.PreviewAvatarUrl, _household!.ResolveAvatar)
            ?? ProfileAvatarPresets.Parse(_form.EffectiveAvatar)?.ImageUrl;
        PreviewImage.ImageSource = url is null ? null : ProfileView.AvatarImage(url, 140);
        PinBadge.Visibility = RemovePinButton.Visibility = _form.PinEnabled ? Visibility.Visible : Visibility.Collapsed;
        NameField.Content = string.IsNullOrEmpty(_form.Name) ? "Profile name" : _form.Name;
        NameField.Foreground = new SolidColorBrush(Color.FromArgb(string.IsNullOrEmpty(_form.Name) ? (byte)107 : (byte)224, 255, 255, 255));
        PinField.Content = _form.Pin.Length > 0 ? new string('•', _form.Pin.Length)
            : _form.IsEditing ? "New 4-Digit PIN (leave blank to keep)" : "4-Digit PIN";
        PinField.Foreground = new SolidColorBrush(Color.FromArgb(_form.Pin.Length == 0 ? (byte)107 : (byte)224, 255, 255, 255));
    }

    private void RefreshAccess()
    {
        var access = _form.Access;
        foreach (var rating in _ratings) rating.Update(rating.Key == access.MaxContentRating,
            rating.Key is { } value && ProfileAccessLimits.UnderCeiling(value, access.MaxContentRating));
        RatingCaption.Text = ProfileAccessLimits.RatingCaption(access.MaxContentRating);
        AllLibraries.IsChecked = !access.LibraryRestrictionsEnabled;
        ChooseLibraries.IsChecked = access.LibraryRestrictionsEnabled;
        RestrictedLibraries.Visibility = access.LibraryRestrictionsEnabled ? Visibility.Visible : Visibility.Collapsed;
        ChildBadge.Visibility = access.IsChild ? Visibility.Visible : Visibility.Collapsed;
        SwitchTrack.Background = new SolidColorBrush(Color.FromArgb(access.IsChild ? (byte)255 : (byte)51, 255, 255, 255));
        SwitchThumb.Fill = new SolidColorBrush(access.IsChild ? Colors.Black : Colors.White);
        SwitchThumb.HorizontalAlignment = access.IsChild ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        AutomationProperties.SetItemStatus(ChildSwitch, access.IsChild ? "On" : "Off");
        foreach (var library in _libraries) library.Update(access.AllowedLibraryIds);
        UpdateSegmentWidths();
    }

    private async Task LoadLibrariesAsync()
    {
        if (_loadingLibraries || !IsCurrent || _lifetime is not { } lifetime) return;
        _loadingLibraries = true;
        var retry = _librariesError is not null;
        var handoff = retry && ReferenceEquals(FocusManager.GetFocusedElement(), RetryLibraries);
        if (retry) RetryLibrariesLabel.Text = "Loading…"; // Keep the focused retry button mounted and enabled.
        try
        {
            var libraries = await _household!.Repository.ListLibrariesAsync(lifetime.Token);
            if (!IsCurrent || lifetime.IsCancellationRequested) return;
            // All assignable IDs validate changed payloads, while TV only renders non-reading libraries.
            _assignable = libraries.Where(library => !string.IsNullOrEmpty(library.Id)).Select(library => library.Id!).ToHashSet();
            _libraries = ProfileLibraries.ShownOnTv(libraries).Select(library => new ProfileLibraryView(library)).ToList();
            Libraries.ItemsSource = _libraries;
            foreach (var library in _libraries) library.Update(_form.Access.AllowedLibraryIds);
            _librariesLoaded = true;
            _librariesError = null;
            // Only hand off if the user stayed on Retry; completing a background read must not steal focus.
            handoff &= ReferenceEquals(FocusManager.GetFocusedElement(), RetryLibraries);
            RetryLibraries.Visibility = Visibility.Collapsed;
            if (handoff && _form.Access.LibraryRestrictionsEnabled)
                FocusRetry.Until(Libraries, () => !IsCurrent || (_libraries.Count > 0 ? FocusLibrary(0) : _librariesRail.Focus()));
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error("Profile libraries", ex);
            if (IsCurrent) _librariesError = ProfileRepository.ErrorMessage(ex, "Failed to load libraries");
        }
        finally
        {
            _loadingLibraries = false;
            if (IsCurrent)
            {
                RetryLibraries.Visibility = _librariesError is null ? Visibility.Collapsed : Visibility.Visible;
                RetryLibrariesLabel.Text = "Try Again";
                LibrariesStatus.Text = _librariesError ?? (_librariesLoaded ? "No libraries are available to assign." : "Loading libraries…");
                LibrariesStatus.Visibility = _librariesError is not null || !_librariesLoaded || _libraries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void OnStyleClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ProfileChoiceView style } || style.Key is null) return;
        _form.SelectStyle(style.Key);
        PopulatePresets();
        RefreshIdentity();
    }
    private void OnShuffleClick(object sender, RoutedEventArgs args) { _form.Shuffle(); PopulatePresets(); RefreshIdentity(); }
    private void OnAvatarClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ProfilePresetView preset }) return;
        _form.SelectAvatar(preset.Preset);
        RefreshIdentity();
    }
    private void OnNameClick(object sender, RoutedEventArgs args) { _editingPin = false; TextPrompt.Show(_form.Name, pin: false); }
    private void OnPinClick(object sender, RoutedEventArgs args) { _editingPin = true; TextPrompt.Show(_form.Pin, pin: true); }
    private void OnTextConfirmed(string value)
    {
        if (_editingPin) _form.ConfirmPin(value); else _form.Name = value.Trim();
        SetError(null);
        RefreshIdentity();
    }
    private void OnRemovePinClick(object sender, RoutedEventArgs args)
    {
        _form.TogglePin(false);
        RefreshIdentity();
        FocusTarget(PinField);
    }
    private void OnChildClick(object sender, RoutedEventArgs args)
    {
        _form.Access = _form.Access.WithChild(!_form.Access.IsChild);
        SetError(null);
        RefreshAccess();
    }
    private void OnRatingClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ProfileChoiceView rating }) return;
        _form.Access = _form.Access.WithRating(rating.Key);
        RefreshAccess();
    }
    private void OnAllLibrariesClick(object sender, RoutedEventArgs args) => SetRestrictions(false);
    private void OnChooseLibrariesClick(object sender, RoutedEventArgs args) => SetRestrictions(true);
    private void SetRestrictions(bool enabled) { _form.Access = _form.Access.WithRestrictions(enabled); SetError(null); RefreshAccess(); }
    private void OnLibraryClick(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ProfileLibraryView library }) return;
        _form.Access = _form.Access.WithLibrary(library.Id, !library.Allowed);
        SetError(null);
        RefreshAccess();
    }
    private async void OnRetryLibrariesClick(object sender, RoutedEventArgs args) => await LoadLibrariesAsync();
    private void OnSegmentsSizeChanged(object sender, SizeChangedEventArgs args) => UpdateSegmentWidths();
    private void UpdateSegmentWidths()
    {
        if (_ratings.Count > 0 && Ratings.ActualWidth > 0 && Ratings.ItemsPanelRoot is StackPanel ratingsPanel)
        {
            var width = Math.Max(0, (Ratings.ActualWidth - ratingsPanel.Spacing * (_ratings.Count - 1)) / _ratings.Count);
            for (var i = 0; i < _ratings.Count; i++) _ratings[i].SetGeometry(width, i, _ratings.Count);
        }
        if (LibraryModes.ActualWidth > 0 && LibraryModes.ItemsPanelRoot is StackPanel modesPanel)
            AllLibraries.Width = ChooseLibraries.Width = Math.Max(0, (LibraryModes.ActualWidth - modesPanel.Spacing) / 2);
    }

    private async void OnSubmitClick(object sender, RoutedEventArgs args)
    {
        if (_saving || !IsCurrent || _lifetime is not { } lifetime) return;
        var error = string.IsNullOrWhiteSpace(_form.Name) ? "Enter a profile name" : _form.Validate(_profiles, _assignable);
        if (error is not null) { SetError(error); return; }
        _saving = true;
        SetError(null);
        SubmitLabel.Text = "Saving...";
        try
        {
            var repository = _household!.Repository;
            var saved = _form.IsEditing
                ? await repository.UpdateAsync(_form.ProfileId!, _form.BuildUpdate(_assignable), lifetime.Token)
                : await repository.CreateAsync(_form.BuildCreate(), lifetime.Token);
            if (!IsCurrent || lifetime.IsCancellationRequested) return;
            if (!IsProfilePreview && Session.Client?.ProfileId == saved.Id)
            {
                Session.ProfileName = saved.Name;
                Session.ProfileAvatar = ProfileAvatars.ImageUrl(saved.AvatarUrl, _household.ResolveAvatar);
                Session.Persist();
            }
            ReturnToPicker();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error("Profile save", ex);
            if (IsCurrent) SetError(ProfileRepository.ErrorMessage(ex, _form.IsEditing ? "Failed to update profile" : "Failed to create profile"));
        }
        finally
        {
            _saving = false;
            SubmitLabel.Text = _form.IsEditing ? "Save Changes" : "Create Profile";
        }
    }

    private void SetError(string? error)
    {
        ErrorText.Text = error ?? string.Empty;
        ErrorText.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OnCancelClick(object sender, RoutedEventArgs args) => ReturnToPicker();
    private void ReturnToPicker()
    {
        if (Frame.CanGoBack) Frame.GoBack();
        else Frame.Navigate(typeof(ProfilesPage), _household);
    }
    public bool OnBack()
    {
        if (TextPrompt.Dismiss() || _reverify.Dismiss()) return true;
        ReturnToPicker();
        return true;
    }
    public void RestoreFocus() => FocusRetry.Until(EditorLayout, () => !IsCurrent || TextPrompt.IsOpen || PinPrompt.IsOpen
        || (_lastFocus?.Focus(FocusState.Programmatic) ?? _stylesRail.Focus()));

    private void OnFormGotFocus(object sender, RoutedEventArgs args)
    {
        if (FocusManager.GetFocusedElement() is not Control focused) return;
        _lastFocus = focused;
        if (FindData<ProfilePresetView>(focused) is { } preset) _lastAvatar = Math.Max(0, _presets.IndexOf(preset));
        Reveal(focused);
    }
    private void Reveal(FrameworkElement target)
    {
        if (!SettingsInput.IsWithin(target, FormScroller)) return;
        var bounds = target.TransformToVisual(FormScroller).TransformBounds(new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        if (bounds.Top < 0) FormScroller.ChangeView(null, Math.Max(0, FormScroller.VerticalOffset + bounds.Top), null, disableAnimation: true);
        else if (bounds.Bottom > FormScroller.ViewportHeight)
            FormScroller.ChangeView(null, FormScroller.VerticalOffset + bounds.Bottom - FormScroller.ViewportHeight, null, disableAnimation: true);
    }
    private void FocusTarget(Control control) => FocusRetry.Until(EditorLayout, () => !IsCurrent || control.Focus(FocusState.Keyboard));
    private static T? FindData<T>(DependencyObject? element) where T : class
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { DataContext: T value }) return value;
        return null;
    }
    private bool FocusAvatar(int index) => FocusItem(Avatars, _presets.ElementAtOrDefault(index));
    private bool FocusLibrary(int index) => FocusItem(Libraries, _libraries.ElementAtOrDefault(index));
    private static bool FocusItem(ItemsControl list, object? view)
    {
        if (view is null || list.ItemsPanelRoot is not { } panel) return false;
        // GridView gives ContentControl; a plain ItemsControl's presenter projects only as FrameworkElement.
        var container = panel.Children.OfType<FrameworkElement>().FirstOrDefault(element =>
            element is ContentControl control ? ReferenceEquals(control.Content, view) : ReferenceEquals(element.DataContext, view));
        return container is not null && FocusManager.FindFirstFocusableElement(container) is Control target && target.Focus(FocusState.Keyboard);
    }
    private void EnterAvatars()
    {
        if (_form.AvatarStyleId == "initials") FocusTarget(NameField);
        else FocusRetry.Until(Avatars, () => !IsCurrent || FocusAvatar(_lastAvatar));
    }
    private void EnterLibraryRows(bool last = false)
    {
        if (!_form.Access.LibraryRestrictionsEnabled) { if (last) _librariesRail.Focus(); else FocusTarget(SubmitButton); }
        else if (_librariesError is not null) FocusTarget(RetryLibraries);
        else if (_libraries.Count > 0) FocusRetry.Until(Libraries, () => !IsCurrent || FocusLibrary(last ? _libraries.Count - 1 : 0));
        else if (last) _librariesRail.Focus(); else FocusTarget(SubmitButton);
    }

    private void OnFormPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var x = SettingsInput.HorizontalStep(args.Key);
        var y = SettingsInput.VerticalStep(args.Key);
        if (x == 0 && y == 0) return;
        if (FocusManager.GetFocusedElement() is not Control focused) return;
        if (SettingsInput.IsWithin(focused, Avatars) && FindData<ProfilePresetView>(focused) is { } preset)
        {
            args.Handled = true;
            var index = _presets.IndexOf(preset);
            var target = index + x + y * 6;
            if (x != 0) { if (target >= 0 && target < _presets.Count && target / 6 == index / 6) FocusAvatar(target); }
            else if (target >= 0 && target < _presets.Count) FocusAvatar(target);
            else if (y > 0) FocusTarget(NameField);
            else FocusTarget(ShuffleButton);
            return;
        }
        var inStyles = SettingsInput.IsWithin(focused, Styles);
        var inRatings = SettingsInput.IsWithin(focused, Ratings);
        var inModes = SettingsInput.IsWithin(focused, LibraryModes);
        if (x != 0)
        {
            // TvProfileForm.kt explicitly links Name → PIN → Child. Keep those vertical links;
            // the PIN section's trailing Remove pill has a deliberate Right / Left utility link.
            // Grid edges above and every other field/vertical library row still stop horizontally.
            if (!inStyles && !inRatings && !inModes)
            {
                args.Handled = true;
                if (focused == PinField && x > 0 && RemovePinButton.Visibility == Visibility.Visible) FocusTarget(RemovePinButton);
                else if (focused == RemovePinButton && x < 0) FocusTarget(PinField);
            }
            return;
        }
        args.Handled = true;
        if (focused == CancelButton) { if (y > 0) _stylesRail.Focus(); }
        else if (inStyles)
        {
            if (y > 0) { if (_form.AvatarStyleId == "initials") FocusTarget(NameField); else FocusTarget(ShuffleButton); }
            else if (!_form.IsEditing) FocusTarget(CancelButton);
        }
        else if (focused == ShuffleButton) { if (y > 0) EnterAvatars(); else _stylesRail.Focus(); }
        else if (focused == NameField) { if (y > 0) FocusTarget(PinField); else if (_form.AvatarStyleId == "initials") _stylesRail.Focus(); else EnterAvatars(); }
        else if (focused == PinField) FocusTarget(y > 0 ? ChildSwitch : NameField);
        else if (focused == RemovePinButton) FocusTarget(y > 0 ? PinField : NameField);
        else if (focused == ChildSwitch) { if (y > 0) _ratingsRail.Focus(); else FocusTarget(PinField); }
        else if (inRatings) { if (y > 0) _librariesRail.Focus(); else FocusTarget(ChildSwitch); }
        else if (inModes) { if (y > 0) EnterLibraryRows(); else _ratingsRail.Focus(); }
        else if (focused == RetryLibraries) { if (y > 0) FocusTarget(SubmitButton); else _librariesRail.Focus(); }
        else if (FindData<ProfileLibraryView>(focused) is { } library)
        {
            var index = _libraries.IndexOf(library) + y;
            if (index < 0) _librariesRail.Focus();
            else if (index >= _libraries.Count) FocusTarget(SubmitButton);
            else FocusLibrary(index);
        }
        else if (focused == SubmitButton && y < 0) EnterLibraryRows(last: true);
    }
}
