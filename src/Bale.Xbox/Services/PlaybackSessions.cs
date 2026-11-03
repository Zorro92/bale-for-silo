using Bale.Core;
using Bale.Core.Playback;
using Windows.Storage;

namespace Bale.Xbox.Services;

/// <summary>One sequenced writer for the active saved login/profile, including launch recovery.</summary>
internal static class PlaybackSessions
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static PlaybackSessionServices? _current;

    public static async Task<PlaybackSessionServices> PrepareAsync(SiloClient client)
    {
        await Gate.WaitAsync();
        try
        {
            var profile = client.ProfileId ?? throw new InvalidOperationException("Playback needs an active profile.");
            var login = AppSettings.PlaybackLoginId;
            void CheckCurrent()
            {
                if (!ReferenceEquals(Session.Client, client) || client.ProfileId != profile || AppSettings.PlaybackLoginId != login)
                    throw new InvalidOperationException("The playback viewer changed.");
            }
            CheckCurrent();
            if (_current is { } current && ReferenceEquals(current.Client, client) && current.ProfileId == profile && current.LoginId == login)
                return current;

            // shared/src/commonMain/kotlin/org/siloserver/silo/repository/SequencedPlayback.kt:
            // installation_id belongs to the server capability, not the TV's X-Silo-Device-Id.
            if (client.Account is null) await client.GetAccountAsync();
            CheckCurrent();
            var capability = await client.Playback.GetCapabilitiesAsync();
            CheckCurrent();
            var installation = new PlaybackInstallationId(capability.InstallationId);
            var journal = new FilePlaybackJournal(Path.Combine(ApplicationData.Current.LocalFolder.Path, "playback.v1.json"));
            var reporter = client.Playback.CreateProgressReporter(installation, journal, login);
            // A failed orphan stop stays journaled; it must not hold up a new start (SequencedPlayback.kt).
            try { await reporter.ReplayAsync(); }
            catch (Exception ex) { Log.Info($"Playback recovery deferred type={ex.GetType().Name}"); }
            CheckCurrent();
            return _current = new(client, profile, login, installation, reporter);
        }
        finally { Gate.Release(); }
    }

    public static async Task ReplayAsync()
    {
        try
        {
            if (Session.Client is { IsSignedIn: true, ProfileId: not null } client)
            {
                var services = await PrepareAsync(client);
                await services.Reporter.ReplayAsync();
            }
        }
        catch (Exception ex)
        {
            // Never write exception text from a media/session request: it may contain signed URLs.
            Log.Info($"Playback launch recovery deferred type={ex.GetType().Name}");
        }
    }
}

internal sealed class PlaybackInstallationId : IPlaybackInstallationId
{
    public PlaybackInstallationId(string? installationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        InstallationId = installationId;
    }

    public string InstallationId { get; }
}

internal sealed record PlaybackSessionServices(SiloClient Client, string ProfileId, string LoginId,
    IPlaybackInstallationId Installation, PlaybackProgressReporter Reporter);
