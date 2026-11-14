using Bale.Core;
using Bale.Core.Playback;
using Windows.Networking.Connectivity;

namespace Bale.Xbox.Services;

/// <summary>
/// When Bale checks and tests the connection (silo-server buffering-quality design, "When to test"):
/// on every sign-in (the shell opening for a profile), on a return to the foreground and after a network
/// change. A check reads the capability and GET /playback/connection at once; an automatic quick test
/// waits a few seconds and runs only if the server allows it, no current reading proves the link, no
/// media plays or loads, the app is in the foreground, the link isn't metered and no 429 holds tests.
/// There is no periodic test. Test speed (Settings) runs the accurate test.
/// </summary>
internal static class ConnectionSpeedService
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(4);
    private static readonly Lock Gate = new();
    private static CancellationTokenSource? _test;
    private static int _mediaActive;
    private static string? _owner;
    private static bool _subscribed;

    /// <summary>The last result kind, for Settings and diagnostics.</summary>
    internal static string LastOutcome { get; private set; } = "none";

    internal static void Start()
    {
        lock (Gate)
        {
            if (_subscribed) return;
            _subscribed = true;
        }
        NetworkInformation.NetworkStatusChanged += _ => Check("network");
    }

    /// <summary>Media started loading or playing: no automatic test, and a running one measured nothing.</summary>
    internal static void MediaStarting()
    {
        Interlocked.Increment(ref _mediaActive);
        CancelTest();
    }

    internal static void MediaEnded()
    {
        if (Interlocked.Decrement(ref _mediaActive) < 0) Interlocked.Exchange(ref _mediaActive, 0);
    }

    internal static void CancelTest()
    {
        lock (Gate) _test?.Cancel();
    }

    /// <summary>Reads the connection now; a due automatic test follows after a short settle.</summary>
    internal static void Check(string reason)
    {
        if (Session.Client is not { } client || client.ProfileId is null) return;
        _ = CheckAsync(client, reason);
    }

    private static async Task CheckAsync(SiloClient client, string reason)
    {
        try
        {
            var speed = client.Playback.Speed;
            // A result from another profile or account is not this viewer's.
            var owner = client.ProfileId;
            lock (Gate)
            {
                if (_owner != owner) speed.Clear();
                _owner = owner;
            }
            var (metered, transport) = Link();
            speed.Metered = metered;
            if (metered)
            {
                Log.Info($"connection speed: {reason} skipped (metered)");
                return;
            }
            var state = await client.Playback.RefreshConnectionAsync().ConfigureAwait(false);
            if (state is null || speed.Schedule is not { } schedule)
            {
                Log.Info($"connection speed: {reason} no reading (server feature off or read failed)");
                return;
            }
            await Task.Delay(Settle).ConfigureAwait(false);
            if (!ReadyForAutomaticTest()) return;
            if (!ConnectionSpeedPolicy.AutomaticTestDue(state, schedule, DateTimeOffset.UtcNow, speed.HeldUntil))
            {
                Log.Info($"connection speed: {reason} current reading proves the link");
                return;
            }
            await RunAsync(client, schedule, ConnectionSpeedTestMode.Quick, transport, manual: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Info($"connection speed: {reason} check failed ({ex.GetType().Name})");
        }
    }

    /// <summary>Test speed: the accurate test, whenever no media plays or loads (Settings hides it when metered).</summary>
    internal static async Task<ConnectionSpeedTestOutcome?> TestNowAsync()
    {
        if (Session.Client is not { } client || Volatile.Read(ref _mediaActive) > 0) return null;
        var (_, transport) = Link();
        await client.Playback.RefreshConnectionAsync().ConfigureAwait(false);
        if (client.Playback.Speed.Schedule is not { } schedule) return null;
        return await RunAsync(client, schedule, ConnectionSpeedTestMode.Accurate, transport, manual: true).ConfigureAwait(false);
    }

    private static async Task<ConnectionSpeedTestOutcome?> RunAsync(SiloClient client, ConnectionSpeedSchedule schedule,
        ConnectionSpeedTestMode mode, string transport, bool manual)
    {
        CancellationTokenSource test;
        lock (Gate)
        {
            // Test speed replaces a running automatic test.
            _test?.Cancel();
            _test = test = new CancellationTokenSource();
        }
        try
        {
            var outcome = await client.Playback.RunSpeedTestAsync(schedule, mode, test.Token).ConfigureAwait(false);
            if (outcome.RetryAfter is { } wait)
            {
                client.Playback.Speed.Hold(DateTimeOffset.UtcNow + wait);
                LastOutcome = $"rate-limited {(int)Math.Ceiling(wait.TotalSeconds)}s";
                Log.Info($"connection speed: {mode} test rate-limited for {(int)Math.Ceiling(wait.TotalSeconds)} s");
                return outcome;
            }
            if (outcome.Result is not { } result)
            {
                LastOutcome = "measured nothing";
                Log.Info($"connection speed: {mode} test measured nothing");
                return outcome;
            }
            var report = await client.Playback.ReportSpeedTestAsync(new(result.Kbps, result.Bytes, result.DurationMs,
                transport, result.LowerBound, manual), CancellationToken.None).ConfigureAwait(false);
            LastOutcome = $"{ConnectionSpeedPolicy.FormatRate(result.Kbps)}{(result.LowerBound ? " (at least)" : "")} {report}";
            Log.Info($"connection speed: {mode} test {result.Kbps} kbps lower_bound={result.LowerBound} "
                + $"bytes={result.Bytes} ms={result.DurationMs} transport={transport} report={report}");
            // The server now holds the reading; read it back so starts send it (never keep a refused one).
            if (report != ConnectionSpeedReport.Refused) await client.Playback.RefreshConnectionAsync().ConfigureAwait(false);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            LastOutcome = "cancelled";
            Log.Info($"connection speed: {mode} test cancelled");
            return null;
        }
        finally
        {
            lock (Gate)
            {
                if (ReferenceEquals(_test, test)) _test = null;
            }
            test.Dispose();
        }
    }

    private static bool ReadyForAutomaticTest() =>
        Volatile.Read(ref _mediaActive) == 0 && Windows.UI.Xaml.Application.Current is App { IsInBackground: false };

    /// <summary>Metered (or expensive/roaming/cellular) and the transport the results report.</summary>
    private static (bool Metered, string Transport) Link()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return (false, "other");
            var cost = profile.GetConnectionCost();
            var metered = cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
                || cost.Roaming || cost.OverDataLimit || profile.IsWwanConnectionProfile;
            var transport = profile.IsWlanConnectionProfile ? "wifi"
                : profile.IsWwanConnectionProfile ? "cellular"
                : profile.NetworkAdapter?.IanaInterfaceType == 6 ? "ethernet" : "other";
            return (metered, transport);
        }
        catch
        {
            // A platform that can't report the link treats it as unmetered.
            return (false, "other");
        }
    }
}
