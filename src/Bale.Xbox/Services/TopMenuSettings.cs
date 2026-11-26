using Bale.Core;
using Bale.Core.Settings;
using Windows.Storage;

namespace Bale.Xbox.Services;

/// <summary>
/// Settings › General › Top Menu › Show Audiobooks (TvSettingsScreen.kt:1124-1137): device-local, per server, account and
/// profile, never written to the server (<see cref="ShowAudiobooksSetting"/>, TvLibraryScopeStore.kt getShowAudiobooksTab).
/// Off until the viewer turns it on. UI thread.
/// </summary>
internal static class TopMenuSettings
{
    /// <summary>Whether the signed-in profile shows audiobooks; off with no profile or an unreadable store.</summary>
    public static bool ShowAudiobooks(SiloClient? client)
    {
        if (client?.ProfileId is not { } profileId || AppSettings.ProfileScope(profileId) is not { } scope) return ShowAudiobooksSetting.Default;
        try
        {
            return ShowAudiobooksSetting.FromStored(ApplicationData.Current.LocalSettings.Values[ShowAudiobooksSetting.StorageKey(client.Server, scope)]);
        }
        catch (Exception error)
        {
            Log.Error("Read Show Audiobooks", error);
            return ShowAudiobooksSetting.Default;
        }
    }

    /// <summary>Settings' switch alone writes it. False when the write failed or another viewer took over meanwhile.</summary>
    public static bool SetShowAudiobooks(SiloClient client, string profileId, bool show)
    {
        if (!ReferenceEquals(client, Session.Client) || client.ProfileId != profileId
            || AppSettings.ProfileScope(profileId) is not { } scope) return false;
        try
        {
            ApplicationData.Current.LocalSettings.Values[ShowAudiobooksSetting.StorageKey(client.Server, scope)] = show;
            return true;
        }
        catch (Exception error)
        {
            Log.Error("Save Show Audiobooks", error);
            return false;
        }
    }
}
