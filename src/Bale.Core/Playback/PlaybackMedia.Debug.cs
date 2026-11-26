#if DEBUG_SERVER
using Bale.Core.Api.Models;

namespace Bale.Core.Playback;

public sealed partial class PlaybackMedia
{
    /// <summary>The Rust HTTP spike (bale-debug.sh fetchbench): the plan's stream headers this transport adds. Never log them.</summary>
    public IReadOnlyDictionary<string, string> DebugStreamHeaders => _headers;

    // Separate from SiloClient's auth/refresh pipeline, even for a URL on the Silo origin.
    private static readonly HttpClient DebugFileHttp = new(new HttpClientHandler
    {
        UseCookies = false,
        UseDefaultCredentials = false,
    });

    private PlaybackMedia(Uri uri, PlaybackPlan plan)
        : this(DebugFileHttp, uri, new(null, "", new()), plan, uri,
            (_, _, _, _) => Task.FromResult<IReadOnlyList<KeyValuePair<string, string>>>([]))
    {
        _scope = null;
    }

    /// <summary>Development file playback; the normal range readers, with no Silo request owner or headers.</summary>
    public static PlaybackMedia OpenDebugFile(Uri uri, PlaybackPlan plan)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || plan.Delivery != "original_http"
            || plan.Stream?.Protocol != "http" || plan.Stream.Headers is not null
            || uri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Debug playback requires an unauthenticated HTTP file.");
        return new(uri, plan);
    }
}
#endif
