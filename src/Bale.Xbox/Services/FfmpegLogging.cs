using FFmpegInteropX;

namespace Bale.Xbox.Services;

internal static class FfmpegLogging
{
    // Install once before opening a source, rather than swapping the provider under native workers.
    static FfmpegLogging()
    {
#if DEBUG_SERVER
        FFmpegInteropLogging.SetLogLevel(LogLevel.Info);
        FFmpegInteropLogging.SetLogProvider(new DebugFfmpegLogProvider());
#else
        // Unfiltered FFmpeg output can disclose media URLs and credentials.
        FFmpegInteropLogging.SetLogProvider(null);
#endif
    }

    internal static void Configure() { }
}

#if DEBUG_SERVER
// Partial lets CsWinRT generate the native-thread callback's Native AOT-safe WinRT vtable.
internal sealed partial class DebugFfmpegLogProvider : ILogProvider
{
    public void Log(LogLevel level, string message)
    {
        try
        {
            if (DebugFfmpegLogFilter.Message(message) is { } safe) Bale.Xbox.Services.Log.Info(safe);
        }
        catch
        {
            // Never propagate a logging/storage failure through an FFmpeg native-thread callback.
        }
    }
}
#endif
