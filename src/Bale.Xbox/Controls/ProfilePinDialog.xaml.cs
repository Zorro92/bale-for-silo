using Bale.Core.Auth;
using Bale.Xbox.Services;
using Bale.Xbox.Views;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Bale.Xbox.Controls;

/// <summary>TvPinEntryDialog.kt. Verification belongs to the caller; this never selects or persists a profile.</summary>
public sealed partial class ProfilePinDialog : UserControl
{
    private readonly ProfilePin _pin = new();
    private UIElement? _focusRoot;
    private Control? _opener;
    private string _prompt = "Enter your PIN";
    private bool _verifying;
    private bool _error;
    public bool IsOpen => PinPanel.Visibility == Visibility.Visible;
    public event Action<string>? PinEntered;
    public event Action? Dismissed;

    public ProfilePinDialog()
    {
        InitializeComponent();
        Unloaded += (_, _) => Hide(restoreFocus: false);
    }

    public void Show(ProfileView profile, string prompt = "Enter your PIN")
    {
        Hide(restoreFocus: false);
        _opener = FocusManager.GetFocusedElement() as Control;
        _prompt = prompt;
        _pin.Clear();
        _error = _verifying = false;
        PinName.Text = profile.Name;
        PinInitial.Text = profile.Initial;
        PinAvatarFill.Fill = profile.AvatarFill;
        PinAvatarBrush.ImageSource = profile.Avatar;
        ClearError();
        PaintDots();
        _focusRoot = Window.Current.Content;
        if (_focusRoot is not null) _focusRoot.GettingFocus += OnGettingFocus;
        PinPanel.Visibility = Visibility.Visible;
        IsHitTestVisible = true;
        FocusRetry.Until(this, () => !IsOpen || PinFiveButton.Focus(FocusState.Programmatic));
    }

    public void Hide(bool restoreFocus = true)
    {
        var wasOpen = IsOpen;
        PinPanel.Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        _pin.Clear();
        _verifying = false;
        if (_focusRoot is not null) _focusRoot.GettingFocus -= OnGettingFocus;
        _focusRoot = null;
        var opener = _opener;
        _opener = null;
        if (wasOpen && restoreFocus && opener is not null)
            FocusRetry.Until(opener, () => IsOpen || opener.Focus(FocusState.Programmatic));
    }

    public bool Dismiss()
    {
        if (!IsOpen) return false;
        Hide();
        Dismissed?.Invoke();
        return true;
    }

    public void SetVerifying(bool value)
    {
        _verifying = value;
        if (value) PinStatus.Text = "Checking…";
        else if (!_error) ClearError();
    }

    public void ShowError(string message)
    {
        _pin.Clear();
        _verifying = false;
        _error = true;
        PinStatus.Text = message;
        PinStatus.Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 138, 128));
        PaintDots();
    }

    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        try
        {
            if (!IsOpen || SettingsInput.IsWithin(args.NewFocusedElement, this)) return;
            if (!args.TrySetNewFocusedElement(PinFiveButton)) args.Cancel = true;
        }
        catch (Exception ex) { Log.Error("Profile PIN focus", ex); }
    }

    private void OnPinCancelClick(object sender, RoutedEventArgs args) => Dismiss();
    private void OnPinKeyClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string tag }) return;
        if (tag == "delete") DeleteDigit();
        else if (tag.Length == 1) EnterDigit(tag[0]);
    }

    private void OnPinPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Back)
        {
            // App.OnBackKey treats Backspace as Back outside text boxes. On the keypad it deletes a dot.
            args.Handled = true;
            DeleteDigit();
            return;
        }
        var key = (int)args.Key;
        if (key >= (int)VirtualKey.Number0 && key <= (int)VirtualKey.Number9)
        {
            args.Handled = true;
            EnterDigit((char)('0' + key - (int)VirtualKey.Number0));
            return;
        }
        if (key >= (int)VirtualKey.NumberPad0 && key <= (int)VirtualKey.NumberPad9)
        {
            args.Handled = true;
            EnterDigit((char)('0' + key - (int)VirtualKey.NumberPad0));
            return;
        }
        var x = SettingsInput.HorizontalStep(args.Key);
        var y = SettingsInput.VerticalStep(args.Key);
        if (x == 0 && y == 0) return;
        args.Handled = true;
        if (FocusManager.GetFocusedElement() is not Button focused) return;
        if (focused == PinCancelButton)
        {
            if (y < 0) PinZeroButton.Focus(FocusState.Keyboard);
            return;
        }
        if (!PinKeypad.Children.Contains(focused)) return;
        var row = Grid.GetRow(focused);
        var column = Grid.GetColumn(focused);
        var target = PinKeypad.Children.OfType<Button>().FirstOrDefault(button =>
            Grid.GetRow(button) == row + y * 2 && Grid.GetColumn(button) == column + x * 2);
        if (target is not null) target.Focus(FocusState.Keyboard);
        else if (y > 0 && focused == PinZeroButton) PinCancelButton.Focus(FocusState.Keyboard);
        // TvPinEntryDialog.kt: empty cells and every keypad edge stay put, never move diagonally.
    }

    private void EnterDigit(char digit)
    {
        if (_verifying || !IsOpen || !_pin.Append(digit)) return;
        ClearError();
        PaintDots();
        if (_pin.IsComplete)
        {
            SetVerifying(true);
            PinEntered?.Invoke(_pin.Digits);
        }
    }

    private void DeleteDigit()
    {
        if (_verifying) return;
        _pin.Delete();
        ClearError();
        PaintDots();
    }

    private void ClearError()
    {
        _error = false;
        PinStatus.Text = _prompt;
        PinStatus.Foreground = new SolidColorBrush(Color.FromArgb(191, 237, 237, 237));
    }

    private void PaintDots()
    {
        var index = 0;
        foreach (var dot in PinDots.Children.OfType<Border>())
        {
            var filled = _error || index++ < _pin.Digits.Length;
            var ink = _error ? Color.FromArgb(255, 255, 138, 128) : Colors.White;
            dot.BorderBrush = new SolidColorBrush(ink);
            dot.Background = new SolidColorBrush(filled ? ink : Colors.Transparent);
        }
    }
}
