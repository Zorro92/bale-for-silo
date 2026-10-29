using Bale.Core.Api;
using Bale.Core.Api.Models;
using Microsoft.Kiota.Abstractions;

namespace Bale.Core.Auth;

/// <summary>An open pairing request: show <see cref="DisplayCode"/> and <see cref="ActivateText"/> on screen.</summary>
public sealed record DeviceLoginTicket(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    string VerificationUriComplete,
    DateTimeOffset ExpiresAt,
    TimeSpan Interval)
{
    public string DisplayCode => DeviceCodeFormat.Display(UserCode);

    public string ActivateText => DeviceCodeFormat.ActivateText(VerificationUri, VerificationUriComplete);
}

public enum DeviceLoginOutcome
{
    Approved,
    Denied,
    Expired,
    Canceled,
}

/// <summary>How a pairing request ended. <see cref="Tokens"/> is set only when approved.</summary>
public sealed record DeviceLoginResult(DeviceLoginOutcome Outcome, TokenPair? Tokens = null);

/// <summary>
/// Signs a TV in without typing a password: the TV shows a code, the user approves it
/// from a signed-in phone or browser, and the TV collects tokens by polling.
/// </summary>
public sealed class DeviceLoginFlow
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly SiloApiClient _api;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public DeviceLoginFlow(SiloApiClient api, TimeProvider? time = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _api = api;
        _time = time ?? TimeProvider.System;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Whether the server has device sign-in turned on, and which extras it supports.</summary>
    public Task<DeviceLoginCapability?> GetCapabilityAsync(CancellationToken cancellationToken = default) =>
        _api.Api.V2.Auth.Device.Capability.GetAsync(cancellationToken: cancellationToken);

    public async Task<DeviceLoginTicket> StartAsync(string deviceName, string devicePlatform, CancellationToken cancellationToken = default)
    {
        var start = await _api.Api.V2.Auth.Device.Start.PostAsync(
            new StartDeviceLoginInputBody { DeviceName = deviceName, DevicePlatform = devicePlatform },
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The server returned an empty device login response.");

        return new DeviceLoginTicket(
            start.DeviceCode ?? throw new InvalidOperationException("The server did not return a device code."),
            start.UserCode ?? string.Empty,
            start.VerificationUri ?? string.Empty,
            start.VerificationUriComplete ?? string.Empty,
            start.ExpiresAt ?? _time.GetUtcNow().AddSeconds(start.ExpiresIn ?? 600),
            Seconds(start.Interval) ?? DefaultInterval);
    }

    /// <summary>
    /// Polls until the request is approved, denied or expires. <paramref name="onOpened"/> fires
    /// when the approver opens the link, so the TV can say "Approve on your phone".
    /// </summary>
    public async Task<DeviceLoginResult> WaitForApprovalAsync(
        DeviceLoginTicket ticket,
        Action? onOpened = null,
        CancellationToken cancellationToken = default)
    {
        var interval = ticket.Interval;
        var openedReported = false;

        while (true)
        {
            await _delay(interval, cancellationToken).ConfigureAwait(false);
            if (_time.GetUtcNow() >= ticket.ExpiresAt)
            {
                return new DeviceLoginResult(DeviceLoginOutcome.Expired);
            }

            DeviceLoginPoll? poll;
            try
            {
                poll = await _api.Api.V2.Auth.Device.Poll.PostAsync(
                    new PollDeviceLoginInputBody { DeviceCode = ticket.DeviceCode },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (ApiException e) when (e.ResponseStatusCode == 404)
            {
                // The pairing row is gone: it expired server-side.
                return new DeviceLoginResult(DeviceLoginOutcome.Expired);
            }

            if (poll is null)
            {
                continue;
            }

            if (poll.Opened == true && !openedReported)
            {
                openedReported = true;
                onOpened?.Invoke();
            }

            switch (poll.Status)
            {
                case DeviceLoginPoll_status.Approved when poll.Tokens is not null:
                    return new DeviceLoginResult(DeviceLoginOutcome.Approved, poll.Tokens);
                case DeviceLoginPoll_status.Denied:
                    return new DeviceLoginResult(DeviceLoginOutcome.Denied);
                case DeviceLoginPoll_status.Canceled:
                    return new DeviceLoginResult(DeviceLoginOutcome.Canceled);
                case DeviceLoginPoll_status.Expired:
                case DeviceLoginPoll_status.Consumed:
                    return new DeviceLoginResult(DeviceLoginOutcome.Expired);
            }

            interval = Seconds(poll.PollAfter) ?? interval;
        }
    }

    /// <summary>Withdraws the request so an abandoned code can't be approved later. Best effort.</summary>
    public async Task CancelAsync(DeviceLoginTicket ticket, CancellationToken cancellationToken = default)
    {
        try
        {
            await _api.Api.V2.Auth.Device.Cancel.PostAsync(
                new CancelDeviceLoginInputBody { DeviceCode = ticket.DeviceCode },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ApiException)
        {
            // Older servers lack cancel; the code will simply expire.
        }
    }

    private static TimeSpan? Seconds(long? value) => value is > 0 ? TimeSpan.FromSeconds(value.Value) : null;
}
