namespace Bale.Core.Auth;

/// <summary>The four-digit entry shared by the TV keypad and keyboard (TvPinEntryDialog.kt).</summary>
public sealed class ProfilePin
{
    public const int Length = 4;

    public string Digits { get; private set; } = string.Empty;

    public bool IsComplete => Digits.Length == Length;

    /// <summary>Returns true only when a new digit was accepted; a completed PIN auto-submits once.</summary>
    public bool Append(char digit)
    {
        if (digit is < '0' or > '9' || IsComplete)
        {
            return false;
        }

        Digits += digit;
        return true;
    }

    public void Delete()
    {
        if (Digits.Length > 0)
        {
            Digits = Digits[..^1];
        }
    }

    public void Clear() => Digits = string.Empty;
}
