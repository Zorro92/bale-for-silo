using Bale.Core.Playback;

/// <summary>Saves the first bytes of an original_http stream to a private local file for offline decoder probes.</summary>
internal static class PlayPlanHeadFile
{
    internal static async Task<long> SaveAsync(PlaybackMedia media, string path, long bytes, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Head files require Unix permissions.");
        // The response carries auth-gated media; never print its URL, headers or failure text.
        using var response = await media.SendAsync(media.Uri, 0, bytes, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
