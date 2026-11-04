using System.Text;

internal static class PlayPlanUrlFile
{
    internal static async Task WriteAsync(string path, Uri url, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("URL files require Unix permissions.");
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".bale-play-url-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Create privately, then rename atomically. Existing files/symlinks are replaced, never opened.
            await using (var stream = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(url.AbsoluteUri), cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}
