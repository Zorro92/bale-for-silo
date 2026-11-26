using Bale.Core;
using Bale.Core.Detail;
using Bale.Core.Settings;
using Bale.Xbox.Services;

namespace Bale.Xbox;

public sealed partial class DetailPage
{
    private ClientAudioCapabilities? _audioSelectionCapabilities;
    private string? _preferredAudioLanguage;
    private SubtitleAutoContext? _subtitleAutoContext;
    private int _audioPolicyGeneration;

    private async Task LoadAutomaticAudioPolicyAsync(SiloClient client)
    {
        var generation = ++_audioPolicyGeneration;
        var profileId = client.ProfileId;
        _audioSelectionCapabilities = null;
        _preferredAudioLanguage = PlaybackAudioSelection.EffectivePreferredAudioLanguage(AppSettings.AudioLanguage, null);
        _subtitleAutoContext = null;
        try
        {
            // androidTvApp/src/androidMain/kotlin/org/siloserver/silo/tv/ui/screens/detail/TvItemDetailViewModel.kt:
            // publish capabilities with settings-language -> active-profile-language policy, off the hero's load path.
            if (profileId is not null)
            {
                // loadSubtitlePreferences: the subtitle settings as the server resolves them (what Settings › Subtitles
                // writes), read alongside the profile; the profile's own fields only when that read fails.
                var subtitles = client.SubtitleSettings.ReadPreferencesAsync();
                Bale.Core.Api.Models.Profile? profile = null;
                try { profile = (await client.GetProfilesAsync()).FirstOrDefault(item => item.Id == profileId); }
                catch (Exception ex) { Log.Error("Detail automatic audio policy", ex); }
                var resolved = await subtitles;
                if (!OwnsAudioPolicy()) return;
                _preferredAudioLanguage = PlaybackAudioSelection.EffectivePreferredAudioLanguage(AppSettings.AudioLanguage, profile?.Language);
                _subtitleAutoContext = SubtitlePreferences.AutoContextFor(resolved, profile);
            }
        }
        catch (Exception ex)
        {
            // Like the official view model, a failed profile read still allows the device/English fallback.
            Log.Error("Detail automatic audio policy", ex);
        }
        finally
        {
            if (OwnsAudioPolicy())
            {
                _audioSelectionCapabilities = XboxAudioCapabilities.Current;
                ShowPlaybackSummary();
            }
        }

        bool OwnsAudioPolicy() => _active && generation == _audioPolicyGeneration
            && ReferenceEquals(client, Session.Client) && client.ProfileId == profileId;
    }
}
