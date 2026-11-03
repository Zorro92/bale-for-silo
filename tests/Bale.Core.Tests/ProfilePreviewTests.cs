using Bale.Core.Profiles;
using Bale.Xbox.Services;

namespace Bale.Core.Tests;

public sealed class ProfilePreviewTests
{
    [Fact]
    public async Task SaveAndDeleteOnlyChangeTheirDisposableHousehold()
    {
        var preview = new ProfilePreview();
        var original = await preview.ListAsync();
        var form = new ProfileForm { Name = "New viewer" };
        form.ConfirmPin("5678");
        var created = await preview.CreateAsync(form.BuildCreate());
        Assert.Equal(4, (await preview.ListAsync()).Count);
        Assert.Equal("fixture-proof", await preview.VerifyPinAsync(created.Id!, "5678"));

        var edit = new ProfileForm(original[1]) { Name = "Edited viewer" };
        edit.Access = edit.Access.WithRating("PG").WithRestrictions(true).WithLibrary("fixture-movies", true);
        var saved = await preview.UpdateAsync(original[1].Id!, edit.BuildUpdate());
        Assert.Equal("Edited viewer", saved.Name);
        Assert.Equal("Sam", original[1].Name); // Reads are snapshots, not aliases into fixture storage.
        Assert.Equal(["fixture-movies"], saved.AllowedLibraryIds);
        await preview.DeleteAsync(created.Id!);
        Assert.Equal(3, (await preview.ListAsync()).Count);
        Assert.Equal("Sam", (await new ProfilePreview().ListAsync())[1].Name);
    }

    [Fact]
    public async Task PreviewUsesTheSameManagementPinAndPrimaryDeletionRules()
    {
        var preview = new ProfilePreview();
        var profiles = await preview.ListAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => preview.DeleteAsync(profiles[0].Id!));
        var gate = new ProfileManagementGate(preview.Management);
        gate.Done();
        await Assert.ThrowsAsync<InvalidOperationException>(() => preview.DeleteAsync(profiles[1].Id!));
        Assert.Equal(ProfileManagementAction.EnterPin, gate.Request(profiles, add: true));
        Assert.Equal(ProfileManagementAction.None, gate.CompletePin(await preview.VerifyPinAsync(profiles[0].Id!, "0000")));
        Assert.Equal(ProfileManagementAction.Create, gate.CompletePin(await preview.VerifyPinAsync(profiles[0].Id!, "1234")));
        await preview.DeleteAsync(profiles[1].Id!);
        Assert.Equal(2, (await preview.ListAsync()).Count);
    }

    [Fact]
    public async Task PreviewPatchHonorsExplicitClearsAndCancellation()
    {
        var preview = new ProfilePreview();
        var primary = (await preview.ListAsync())[0];
        var form = new ProfileForm(primary);
        form.TogglePin(false);
        var saved = await preview.UpdateAsync(primary.Id!, form.BuildUpdate());
        Assert.False(saved.HasPin);
        Assert.Null(await preview.VerifyPinAsync(primary.Id!, "1234"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preview.CreateAsync(new() { Name = "Cancelled" }, cancelled.Token));
        Assert.Equal(3, (await preview.ListAsync()).Count);
    }
}
