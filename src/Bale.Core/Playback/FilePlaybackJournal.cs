using System.Text.Json;

namespace Bale.Core.Playback;

/// <summary>
/// Durable PlaybackJournalStore from shared/src/commonMain/kotlin/org/siloserver/silo/repository/SequencedPlayback.kt.
/// The platform supplies a path inside its private application data directory.
/// </summary>
public sealed class FilePlaybackJournal(string path) : IPlaybackJournal
{
    public async Task<IReadOnlyList<PlaybackJournalEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync(stream, PlaybackJson.Default.PlaybackJournalEntryArray, cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidDataException("The playback journal is empty.");
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    public async Task WriteAsync(IReadOnlyList<PlaybackJournalEntry> entries, CancellationToken cancellationToken = default)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, entries.ToArray(), PlaybackJson.Default.PlaybackJournalEntryArray,
                    cancellationToken).ConfigureAwait(false);
                // Never acknowledge a pending mutation until its complete snapshot is on disk.
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
