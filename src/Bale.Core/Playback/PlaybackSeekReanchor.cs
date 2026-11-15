using Bale.Core.Api.Models;
using Bale.Core.Native;

namespace Bale.Core.Playback;

/// <summary>
/// PlaybackSessionManager.kt seekReanchorMismatch/hasSameSeekReanchorBaseRoute. Implemented in Rust
/// (native/bale-core/src/reanchor.rs), which compares the plans' route identity as Kiota-written JSON,
/// not rotating stream grants, headers, timeline or expiration. Plans never get logged.
/// </summary>
public static class PlaybackSeekReanchor
{
    public const string Feature = "seek_reanchor_v1";

    public static bool Matches(PlaybackPlan current, PlaybackDecision decision, string sessionId, string fileId) =>
        BaleCoreNative.Call("reanchor.matches", writer =>
        {
            writer.Server("current", current);
            writer.Server("next", decision.PlaybackPlan);
            // Still C#: it reads the whole decision.
            writer.WriteBoolean("playable", PlaybackMessages.ValidatePlayable(decision).IsPlayable);
            if (decision.ServerFeatures is { } features) writer.Strings("server_features", features);
            else writer.WriteNull("server_features");
            writer.Text("decision_session_id", decision.SessionId);
            writer.WriteString("session_id", sessionId);
            writer.WriteString("file_id", fileId);
        }, BaleCoreJson.Default.Boolean);
}
