namespace Bale.Core.Auth;

/// <summary>The profile picker's loading/error branches, independent of the TV view.</summary>
public readonly record struct ProfilePickerState(bool IsLoading, int ProfileCount, string? Error = null)
{
    // TvProfileSelectionScreen.kt keeps an existing grid mounted during loading and failures.
    // A successful empty list is still the picker, without an invented empty-state error.
    public bool ShowLoading => IsLoading && ProfileCount == 0;
    public bool ShowFullScreenError => !ShowLoading && Error is not null && ProfileCount == 0;
    public bool ShowPicker => !ShowLoading && !ShowFullScreenError;
    public bool ShowInlineError => ShowPicker && Error is not null;
}
