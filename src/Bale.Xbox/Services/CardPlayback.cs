using Bale.Core;
using Bale.Core.Browse;
using Bale.Core.Detail;
using Bale.Core.Settings;

namespace Bale.Xbox.Services;

/// <summary>
/// Continue Watching's Play/Resume starts the card's own playback without a Detail page (TvHomeScreen.kt's onPlayItem,
/// TvVideoPlaybackStarter.kt; tvOS SectionRow.playItem). The tracks are Auto, as a Detail page would start them.
/// </summary>
internal static class CardPlayback
{
    /// <summary>The short toast for a failed Detail load (DetailPage.Trailers.cs and DetailPage.Player.cs say the same).</summary>
    public const string LoadFailed = "Couldn't load playback details. Try again.";

    /// <summary>Null when the target is a series or season: a container never plays.</summary>
    public static async Task<PlayerRequest?> RequestAsync(SiloClient client, MediaCard card)
    {
        var target = CardMenu.PlayTarget(card);
        // Detail reads are cached for five minutes, and the player resumes from this leaf's saved position: read it fresh.
        client.Detail.Forget(target);
        var detail = await client.Detail.GetAsync(target);
        if (detail.PlaysEpisodes) return null;
        return new PlayerRequest(detail, await AutoSelectionAsync(client, detail));
    }

    /// <summary>
    /// DetailPage.AutomaticAudio.cs's policy for a start no Detail page chose: the settings language, then the active
    /// profile's language; the subtitle settings as the server resolves them. The same reads PlayerPage.UpNext.cs makes for
    /// the next episode. A failed profile read still starts, on the device/English fallback.
    /// </summary>
    private static async Task<PlaybackSelection> AutoSelectionAsync(SiloClient client, ItemDetail detail)
    {
        string? language = null;
        SubtitleAutoContext? subtitles = null;
        if (client.ProfileId is { } profileId)
        {
            var preferences = client.SubtitleSettings.ReadPreferencesAsync();
            Bale.Core.Api.Models.Profile? profile = null;
            try { profile = (await client.GetProfilesAsync()).FirstOrDefault(item => item.Id == profileId); }
            catch (Exception ex) { Log.Error("Continue Watching track policy", ex); }
            language = profile?.Language;
            subtitles = SubtitlePreferences.AutoContextFor(await preferences, profile);
        }

        return PlaybackLaunchTracks.ForAuto(detail, PlaybackAudioSelection.EffectivePreferredAudioLanguage(AppSettings.AudioLanguage, language),
            XboxAudioCapabilities.Current, subtitles);
    }
}
