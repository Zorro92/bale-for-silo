using Bale.Core.Api.Models;
using Bale.Core.Auth;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace Bale.Xbox;

/// <summary>"Who's watching?": picks the household profile Home and playback act as.</summary>
public sealed partial class ProfilesPage : Page
{
    /// <summary>Navigation parameter: opened from Home's menu, so Back returns there.</summary>
    public const string FromHome = "from-home";

    public ProfilesPage()
    {
        InitializeComponent();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (Session.Client is not { } client)
        {
            return;
        }

        try
        {
            var profiles = await client.GetProfilesAsync();
            Profiles.ItemsSource = profiles.Select(p => new ProfileView(p, ProfileAvatars.ImageUrl(p.AvatarUrl, client.Browse.Resolve))).ToList();
            await Task.Yield();
            var current = profiles.ToList().FindIndex(p => p.Id == client.ProfileId);
            if (Profiles.ContainerFromIndex(Math.Max(current, 0)) is GridViewItem item)
            {
                item.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Profiles load", ex);
            ErrorText.Text = $"Couldn't load profiles. {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadingRing.Visibility = Visibility.Collapsed;
        }
    }

    private void OnProfileFocus(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView or ProfileView } item)
        {
            CardFocus.Apply(item, focused: true);
        }
    }

    private void OnProfileBlur(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ContentControl { Content: CardView or ProfileView } item)
        {
            CardFocus.Apply(item, focused: false);
        }
    }

    private async void OnProfileClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not ProfileView { Profile: var profile } chosen || Session.Client is not { } client)
        {
            return;
        }

        if (profile.HasPin == true)
        {
            if (!await AskPinAsync(profile))
            {
                return;
            }
        }
        else
        {
            client.UseProfile(profile.Id!);
        }

        Session.ProfileName = profile.Name;
        Session.ProfileAvatar = chosen.AvatarUrl;
        Session.Persist();
        Frame.Navigate(typeof(ShellPage));
        Frame.BackStack.Clear();
    }

    private async Task<bool> AskPinAsync(Profile profile)
    {
        var pinBox = new PasswordBox { PlaceholderText = "PIN", MaxLength = 8, InputScope = NumberScope() };
        var error = new TextBlock { Foreground = ErrorText.Foreground, Visibility = Visibility.Collapsed };
        var dialog = new ContentDialog
        {
            Title = $"Enter {profile.Name}'s PIN",
            Content = new StackPanel { Spacing = 12, Children = { pinBox, error } },
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        var verified = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            verified = await Session.Client!.VerifyPinAsync(profile.Id!, pinBox.Password);
            if (!verified)
            {
                args.Cancel = true;
                error.Text = "That PIN isn't right.";
                error.Visibility = Visibility.Visible;
                pinBox.Password = string.Empty;
            }

            deferral.Complete();
        };

        await dialog.ShowAsync();
        return verified;
    }

    private static InputScope NumberScope()
    {
        var scope = new InputScope();
        scope.Names.Add(new InputScopeName(InputScopeNameValue.NumericPin));
        return scope;
    }
}
