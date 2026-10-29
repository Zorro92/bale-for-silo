using Windows.Storage;

namespace Bale.Xbox.Services;

/// <summary>
/// Plain-text log in the app's LocalState folder (scripts/xbox-logs.sh reads it), mirrored into a
/// numbered in-memory buffer that the debug server streams live (scripts/bale-debug.sh log).
/// </summary>
internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private const int BufferLines = 2000;

    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "bale.log");
    private static readonly Queue<(long Sequence, string Line)> Recent = new();
    private static long _sequence;

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string context, Exception ex) => Write("ERROR", $"{context}: {Describe(ex)}{ex}");

    /// <summary>For API failures, the HTTP status and the server's problem text, which the exception message lacks.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        Bale.Core.Api.Models.Problem problem => $"HTTP {problem.ResponseStatusCode} {problem.Title}: {problem.Detail} ",
        Microsoft.Kiota.Abstractions.ApiException api => $"HTTP {api.ResponseStatusCode} ",
        _ => string.Empty,
    };

    /// <summary>Lines after <paramref name="since"/>, and the sequence number to ask from next.</summary>
    public static (IReadOnlyList<string> Lines, long Next) Since(long since)
    {
        lock (Gate)
        {
            var lines = Recent.Where(entry => entry.Sequence > since).Select(entry => entry.Line).ToList();
            return (lines, _sequence);
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:O} {level} {message}";
        lock (Gate)
        {
            Recent.Enqueue((++_sequence, line));
            while (Recent.Count > BufferLines)
            {
                Recent.Dequeue();
            }

            try
            {
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                {
                    File.Delete(FilePath);
                }

                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
