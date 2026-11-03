using Bale.Core.Auth;
using Bale.Xbox.Services;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace Bale.Xbox;

public sealed partial class MainPage
{
    private SignInOptions _options = new();
    private bool _passwordOnly;
    private bool _passwordBusy;

    private void OnPasswordClick(object sender, RoutedEventArgs args)
    {
        if (_options.PasswordAvailable && !_passwordBusy) ShowPasswordScreen();
    }

    private void ShowPasswordScreen()
    {
        HideKeyboard();
        PasswordPanel.Visibility = PhoneHelpCard.Visibility = Visibility.Visible;
        CodeActions.Visibility = PhoneHint.Visibility = CodeCard.Visibility = Visibility.Collapsed;
        UsePhoneButton.Visibility = _passwordOnly ? Visibility.Collapsed : Visibility.Visible;
        UpdatePasswordCopy();
        RestoreFocus();
    }

    private void UpdatePasswordCopy()
    {
        if (PasswordPanel.Visibility != Visibility.Visible) return;
        SignInTitle.Text = "Sign in with\na password";
        SignInInstructions.Text = _passwordOnly ? "This server only supports password sign-in."
            : $"Use your {ServerNameText.Text} username and password.";
        PhoneHelpText.Text = _passwordOnly
            ? "Signed in to Silo on your phone? Open it on the same Wi‑Fi and it can sign this TV in for you."
            : "Choose \"Use your phone instead\" to scan a code and approve on your phone. Nothing to type with the remote.";
        ProviderHint.Text = _options.ProviderName is { } provider ? $"If you sign in with {provider}, use your phone instead." : string.Empty;
        ProviderHint.Visibility = _options.ProviderName is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnUsePhoneClick(object sender, RoutedEventArgs args)
    {
        if (_loginServer is { } server && !_passwordOnly)
        {
            ClearPassword();
            await StartLoginAsync(server);
        }
    }

    private void ClearPassword()
    {
        PasswordInput.Password = string.Empty;
        PasswordInput.PasswordRevealMode = PasswordRevealMode.Hidden;
        AutomationProperties.SetName(RevealButton, "Show password");
        RevealIcon.Visibility = Visibility.Visible;
        HideIcon.Visibility = Visibility.Collapsed;
    }

    private void OnRevealClick(object sender, RoutedEventArgs args)
    {
        if (_passwordBusy) return;
        var reveal = PasswordInput.PasswordRevealMode != PasswordRevealMode.Visible;
        PasswordInput.PasswordRevealMode = reveal ? PasswordRevealMode.Visible : PasswordRevealMode.Hidden;
        AutomationProperties.SetName(RevealButton, reveal ? "Hide password" : "Show password");
        RevealIcon.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        HideIcon.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUsernameChanged(object sender, TextChangedEventArgs args) => ClearPasswordError();
    private void OnPasswordChanged(object sender, RoutedEventArgs args) => ClearPasswordError();
    private void ClearPasswordError()
    {
        if (PasswordError is null) return;
        PasswordError.Visibility = Visibility.Collapsed;
        VisualStateManager.GoToState(UsernameBox, "Valid", false);
        VisualStateManager.GoToState(PasswordInput, "Valid", false);
    }

    private void SetPasswordBusy(bool busy)
    {
        _passwordBusy = busy;
        UsernameBox.IsEnabled = PasswordInput.IsEnabled = RevealButton.IsEnabled = !busy;
        SignInButton.IsEnabled = !busy && _options.PasswordAvailable;
        SignInButton.Content = busy ? "Signing in…" : "Sign in";
    }

    private async void OnSignInClick(object sender, RoutedEventArgs args)
    {
        if (_passwordBusy || !_options.PasswordAvailable || _loginServer is not { } server) return;
        HideKeyboard();
        if (AuthInputRules.ValidationError(UsernameBox.Text, PasswordInput.Password) is { } error)
        {
            ShowPasswordError(error);
            return;
        }
        if (_preview is not null)
        {
            // Preview input exercises the real blank/error/focus rules, never a password POST or vault write.
            ShowPasswordError("Incorrect username or password.");
            return;
        }
        if (_loginLife is not { } lifetime || !IsLoginCurrent(lifetime) || Servers.RequiresHttpApproval(server)) return;
        SetPasswordBusy(true);
        (_passwordOnly ? PasswordChangeServerButton : UsePhoneButton).Focus(FocusState.Programmatic);
        try
        {
            using var flow = new PasswordSignIn(server, DeviceIdentity.Current());
            var result = await flow.SubmitAsync(UsernameBox.Text, PasswordInput.Password, _options, lifetime.Token);
            if (!IsLoginCurrent(lifetime)) return;
            Log.Info($"Password sign-in completed accepted={result.Tokens is not null} passwordDisabled={result.PasswordTurnedOff}");
            SetPasswordBusy(false);
            if (result.Tokens is { } tokens && Session.Client is { } client) CompleteSignIn(client, tokens);
            else if (result.PasswordTurnedOff)
            {
                _options = _options with { PasswordAvailable = false };
                PasswordButton.Visibility = Visibility.Collapsed;
                if (!_passwordOnly)
                {
                    ShowCodeScreen(server);
                    ShowCodeFailure(result.Error!, "Try again");
                }
                else
                {
                    // No usable credential path: retain Change server rather than submitting again.
                    ShowPasswordError(result.Error!);
                    SignInButton.IsEnabled = false;
                    PasswordChangeServerButton.Focus(FocusState.Programmatic);
                }
            }
            else ShowPasswordError(result.Error ?? "Login failed");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Info($"Password sign-in failed type={ex.GetType().Name}");
            if (IsLoginCurrent(lifetime)) ShowPasswordError("Network error. Check your connection.");
        }
        finally
        {
            if (ReferenceEquals(_loginLife, lifetime)) SetPasswordBusy(false);
        }
    }

    private void ShowPasswordError(string message)
    {
        PasswordError.Text = message;
        PasswordError.Visibility = Visibility.Visible;
        var usernameMissing = message == "Username is required";
        var field = usernameMissing ? (Control)UsernameBox : PasswordInput;
        field.ApplyTemplate();
        VisualStateManager.GoToState(field, "Invalid", false);
        if (message.StartsWith("Incorrect username or password.", StringComparison.Ordinal)) ProviderHint.Visibility = Visibility.Collapsed;
        FocusRetry.Until(PasswordPanel, () => !_active || PasswordPanel.Visibility != Visibility.Visible || field.Focus(FocusState.Programmatic));
    }

    private void OnCredentialPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var current = FocusManager.GetFocusedElement();
        var field = ReferenceEquals(current, UsernameBox) || ReferenceEquals(current, PasswordInput);
        if (_keyboardVisible)
        {
            if (args.Key == VirtualKey.Enter && field)
            {
                args.Handled = true;
                if (ReferenceEquals(current, UsernameBox))
                {
                    HideKeyboard();
                    PasswordInput.Focus(FocusState.Programmatic);
                }
                else if (AuthInputRules.CanSubmit(UsernameBox.Text, PasswordInput.Password, _passwordBusy)) OnSignInClick(sender, args);
            }
            return;
        }
        if (field && args.Key is VirtualKey.Enter or VirtualKey.GamepadA)
        {
            args.Handled = true;
            if (!_passwordBusy) _inputPane?.TryShow();
            return;
        }
        var step = SettingsInput.VerticalStep(args.Key);
        if (step != 0)
        {
            args.Handled = true;
            if (ReferenceEquals(current, UsernameBox) && step > 0) PasswordInput.Focus(FocusState.Programmatic);
            else if (ReferenceEquals(current, PasswordInput)) (step < 0 ? (Control)UsernameBox : SignInButton).Focus(FocusState.Programmatic);
            // Reveal is the second column: no Up/Down target, never a diagonal jump.
        }
        else if (ReferenceEquals(current, UsernameBox) && SettingsInput.HorizontalStep(args.Key) != 0) args.Handled = true;
    }

    private void OnPasswordActionsPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var step = SettingsInput.VerticalStep(args.Key);
        if (step == 0) return;
        args.Handled = true;
        if (step < 0 && ReferenceEquals(FocusManager.GetFocusedElement(), SignInButton)) PasswordInput.Focus(FocusState.Programmatic);
    }
}
