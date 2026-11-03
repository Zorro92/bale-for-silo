using Bale.Core.Api.Models;
using Bale.Core.Connection;
using Bale.Core.Detail;
using Bale.Core.Playback;

namespace Bale.Core.Tests;

internal static class PlaybackTestSupport
{
    public static ClientIdentity Identity { get; } = new("device", "Test Xbox", "xbox", "Bale", "0.1.0", "1", "dev", "tv");
    public sealed record Installation(string InstallationId = "installation") : IPlaybackInstallationId;

    public const string Capabilities = """
        {"installation_id":"installation","state":"available","allowed":true,"protocol_versions":[3],
         "features":["sequenced_progress_v1"],"deliveries":["original_http","hls"],"revision":"1"}
        """;
    public const string Decision = """
        {"protocol_version":3,"server_features":["playback_plan_v3","neutral_playback_v3_contract_v1"],
         "outcome":"playable","session_id":"session"}
        """;

    public static SiloClient Create(HttpMessageHandler transport, string profile = "profile", string account = "account",
        bool refresh = false)
    {
        var client = new SiloClient(new Uri("https://silo.example.com"), Identity, transport);
        client.SignIn(new TokenPair
        {
            AccessToken = "test-access", RefreshToken = refresh ? "test-refresh" : null, ExpiresIn = 3600,
            User = new Account { Id = account },
        }, profileId: profile, profileToken: "test-profile-proof");
        return client;
    }

    public static PlaybackStartRequestV3 Start(PlaybackSelection? selection = null, double? position = 0) =>
        PlaybackStartBuilder.Build("file", "profile", "attempt-id", position, selection ?? PlaybackSelection.Auto,
            ConsoleEvidence.Default, Identity);
}
