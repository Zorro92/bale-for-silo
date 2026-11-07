using Bale.Core.Playback;

/// <summary>Saves bytes of an original_http stream (its head, or one range) to a private local file for offline probes.</summary>
internal static class PlayPlanHeadFile
{
    internal static Task<long> SaveAsync(PlaybackMedia media, string path, long bytes, CancellationToken cancellationToken)
        => SaveAsync(media, path, 0, bytes, cancellationToken);

    internal static async Task<long> SaveAsync(PlaybackMedia media, string path, long offset, long bytes, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Head files require Unix permissions.");
        // The response carries auth-gated media; never print its URL, headers or failure text.
        using var response = await media.SendAsync(media.Uri, offset, bytes, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("The media request failed.", null, response.StatusCode);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(Path.GetFullPath(path), new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        await source.CopyToAsync(destination, 1 << 20, cancellationToken);
        return destination.Length;
    }
}
