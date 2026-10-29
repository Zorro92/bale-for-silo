using Windows.Storage;

namespace Bale.Xbox.Services;

/// <summary>
/// Plain-text log in the app's LocalState folder. Read it from Linux with scripts/xbox-logs.sh.
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "bale.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    File.Delete(FilePath);
                }

                File.AppendAllText(FilePath, $"{DateTimeOffset.Now:O} {level} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
    }
}
