using Bale.Core.Auth;

namespace Bale.Core.Tests;

public class ProfilePickerStateTests
{
    [Fact]
    public void FirstLoadReplacesThePickerWithLoading()
    {
        var state = new ProfilePickerState(IsLoading: true, ProfileCount: 0);

        Assert.True(state.ShowLoading);
        Assert.False(state.ShowPicker);
        Assert.False(state.ShowFullScreenError);
        Assert.False(state.ShowInlineError);
    }

    [Fact]
    public void FirstLoadFailureReplacesThePickerWithRetry()
    {
        var state = new ProfilePickerState(IsLoading: false, ProfileCount: 0, Error: "Offline");

        Assert.True(state.ShowFullScreenError);
        Assert.False(state.ShowLoading);
        Assert.False(state.ShowPicker);
        Assert.False(state.ShowInlineError);
    }

    [Fact]
    public void RetryShowsLoadingInsteadOfThePreviousError()
    {
        var state = new ProfilePickerState(IsLoading: true, ProfileCount: 0, Error: "Offline");

        Assert.True(state.ShowLoading);
        Assert.False(state.ShowFullScreenError);
        Assert.False(state.ShowPicker);
        Assert.False(state.ShowInlineError);
    }

    [Fact]
    public void EmptySuccessIsAPickerNotAnError()
    {
        var state = new ProfilePickerState(IsLoading: false, ProfileCount: 0);

        Assert.True(state.ShowPicker);
        Assert.False(state.ShowLoading);
        Assert.False(state.ShowFullScreenError);
        Assert.False(state.ShowInlineError);
    }

    [Theory]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [InlineData(false, "Offline", true)]
    public void ExistingProfilesStayVisibleDuringReloadAndFailure(bool loading, string? error, bool inlineError)
    {
        var state = new ProfilePickerState(loading, ProfileCount: 2, error);

        Assert.True(state.ShowPicker);
        Assert.False(state.ShowLoading);
        Assert.False(state.ShowFullScreenError);
        Assert.Equal(inlineError, state.ShowInlineError);
    }
}
