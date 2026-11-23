using System.Text;
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
#if DEBUG_SERVER
    private static readonly string PreviousPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "bale.1.log");
#endif
    private static readonly Queue<(long Sequence, string Line)> Recent = new();
    private static long _sequence;

    public static void Info(string message) => Write("INFO", message);

    /// <summary>
    /// INFO lines in order, under one lock and with one file append: the debug frame log writes about 60 a second, in
    /// batches from its drain thread. The live buffer keeps the newest <see cref="BufferLines"/> lines, as for
    /// <see cref="Info"/>; the file follows the same size limit (<see cref="Append"/>).
    /// </summary>
    public static void Lines(IReadOnlyList<string> messages)
    {
        if (messages.Count == 0) return;
#if DEBUG_SERVER
        foreach (var message in messages) DebugNavigationHistory.PlayerEvent(message);
#endif
        var prefix = $"{DateTimeOffset.Now:O} INFO ";
        var text = new StringBuilder(messages.Count * 128);
        lock (Gate)
        {
            foreach (var message in messages)
            {
                var line = prefix + message;
                Recent.Enqueue((++_sequence, line));
                text.Append(line).Append(Environment.NewLine);
            }
            while (Recent.Count > BufferLines)
            {
                Recent.Dequeue();
            }

            Append(text.ToString());
        }
    }

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
#if DEBUG_SERVER
        DebugNavigationHistory.PlayerEvent(message);
#endif
        var line = $"{DateTimeOffset.Now:O} {level} {message}";
        lock (Gate)
        {
            Recent.Enqueue((++_sequence, line));
            while (Recent.Count > BufferLines)
            {
                Recent.Dequeue();
            }

            Append(line + Environment.NewLine);
        }
    }

    /// <summary>
    /// Appends to bale.log (under <see cref="Gate"/>). Past <see cref="MaxBytes"/> release builds start the file again; debug
    /// builds move it to bale.1.log first (replacing the one before) and start the new file with a line saying so, so a
    /// debug frame log that crosses the limit loses nothing (scripts/xbox-logs.sh N bale.1.log reads the older part).
    /// </summary>
    private static void Append(string text)
    {
        try
        {
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
            {
#if DEBUG_SERVER
                try
                {
                    File.Move(FilePath, PreviousPath, overwrite: true);
                    text = $"{DateTimeOffset.Now:O} INFO log: bale.log passed {MaxBytes / 1024} KiB; earlier lines are in bale.1.log"
                        + Environment.NewLine + text;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A file in use (Device Portal reading it): append past the limit and move it on a later write.
                }
#else
                File.Delete(FilePath);
#endif
            }

            File.AppendAllText(FilePath, text);
        }
        catch (IOException)
        {
            // Logging must never take the app down.
        }
        catch (UnauthorizedAccessException)
        {
            // Nor a locked or read-only file.
        }
    }
}
