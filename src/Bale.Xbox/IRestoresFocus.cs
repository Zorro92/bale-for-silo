namespace Bale.Xbox;

/// <summary>A root screen that puts focus back where it was when the user returns from a detail page.</summary>
internal interface IRestoresFocus
{
    void RestoreFocus();
}
