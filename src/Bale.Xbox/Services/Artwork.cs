using System.Security.Cryptography;
using System.Text;
using Windows.Storage;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Web.Http;
using Windows.Web.Http.Filters;
using HttpClient = Windows.Web.Http.HttpClient;

namespace Bale.Xbox.Services;

/// <summary>
/// Every poster, still, logo, photo and backdrop, loaded the way Silo's apps load artwork
/// (android-shared SiloImageLoader, SiloOkHttp.imageClient):
/// <list type="bullet">
/// <item>A disk cache that survives restarts, capped at 512 MB (the official size). Files are keyed
/// by host and path without the query: artwork URLs carry a signature that changes, the image
/// behind a path doesn't (each revision has its own path).</item>
/// <item>At most 16 downloads at once (the official per-host limit); one download per URL however
/// many cards ask for it.</item>
/// <item>Dropped connections are retried, as OkHttp does; a 429 waits for its Retry-After.</item>
/// </list>
/// Images are decoded at the size they're drawn.
/// </summary>
internal static class Artwork
{
    private const long CacheLimit = 512L * 1024 * 1024;
    private const long TrimTo = 400L * 1024 * 1024;
    private const int Attempts = 3;

    private static readonly SemaphoreSlim Downloads = new(16);
    private static readonly Dictionary<string, Task<string?>> InFlight = new();
    private static readonly object Gate = new();
    private static readonly string Folder = Path.Combine(ApplicationData.Current.LocalCacheFolder.Path, "art");
    private static readonly HttpClient Client = new(new HttpBaseProtocolFilter
    {
        // The disk cache here is the cache; don't keep a second copy in WinINet.
        CacheControl = { WriteBehavior = HttpCacheWriteBehavior.NoCache },
    });

    /// <summary>A bitmap that fills in from the cache, or once downloaded.</summary>
    public static BitmapImage Load(Uri uri, int? width = null, int? height = null)
    {
        var image = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
        if (width is { } w)
        {
            image.DecodePixelWidth = w;
        }
        else if (height is { } h)
        {
            image.DecodePixelHeight = h;
        }

        _ = FillAsync(image, uri);
        return image;
    }

    /// <summary>Fetch into the cache ahead of time (Prefetch).</summary>
    public static void Warm(Uri? uri)
    {
        if (uri is not null && IsRemote(uri))
        {
            _ = FileAsync(uri);
        }
    }

    /// <summary>The cached file as an ms-appdata URI for Composition surfaces, else the original URI.</summary>
    public static async Task<Uri> LocalUriAsync(Uri uri) =>
        IsRemote(uri) && await FileAsync(uri) is { } path
            ? new Uri("ms-appdata:///localcache/art/" + Path.GetFileName(path))
            : uri;

    /// <summary>The image's bytes, from the cache when possible (AmbientTint samples these).</summary>
    public static async Task<byte[]?> ReadAsync(Uri uri)
    {
        if (await FileAsync(uri) is not { } path)
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Keep the cache under its cap, oldest-used files first. Runs once at launch, off the UI thread.</summary>
    public static void TrimInBackground() => _ = Task.Run(() =>
    {
        try
        {
            var directory = new DirectoryInfo(Folder);
            if (!directory.Exists)
            {
                return;
            }

            var files = directory.GetFiles().OrderBy(file => file.LastWriteTimeUtc).ToList();
            var total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                if (total <= (total > CacheLimit ? TrimTo : CacheLimit))
                {
                    break;
                }

                total -= file.Length;
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Artwork cache trim", ex);
        }
    });

    private static async Task FillAsync(BitmapImage image, Uri uri)
    {
        try
        {
            if (IsRemote(uri) && await FileAsync(uri) is { } path)
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                using var stream = await file.OpenReadAsync();
                await image.SetSourceAsync(stream);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Artwork cache read failed ({uri.Host}{uri.AbsolutePath}): {ex.Message}");
        }

        // Not cacheable, or the cache failed: let XAML load it directly.
        image.UriSource = uri;
    }

    private static bool IsRemote(Uri uri) => uri.Scheme is "https" or "http";

    /// <summary>The cached file's path, downloading it first if needed; null when it can't be had.</summary>
    private static Task<string?> FileAsync(Uri uri)
    {
        var path = Path.Combine(Folder, Key(uri));
        if (File.Exists(path))
        {
            Touch(path);
            return Task.FromResult<string?>(path);
        }

        lock (Gate)
        {
            if (!InFlight.TryGetValue(path, out var download))
            {
                download = DownloadAsync(uri, path);
                InFlight[path] = download;
                _ = download.ContinueWith(_ =>
                {
                    lock (Gate)
                    {
                        InFlight.Remove(path);
                    }
                }, TaskScheduler.Default);
            }

            return download;
        }
    }

    private static async Task<string?> DownloadAsync(Uri uri, string path)
    {
        await Downloads.WaitAsync().ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                TimeSpan wait;
                try
                {
                    using var response = await Client.GetAsync(uri).AsTask().ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        var buffer = await response.Content.ReadAsBufferAsync().AsTask().ConfigureAwait(false);
                        Directory.CreateDirectory(Folder);
                        var temp = path + ".part";
                        await File.WriteAllBytesAsync(temp, System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buffer)).ConfigureAwait(false);
                        File.Move(temp, path, overwrite: true);
                        return path;
                    }

                    // Only throttling and server trouble are worth another try; a 403 or 404 isn't.
                    if (response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode < 500 || attempt >= Attempts)
                    {
                        Log.Info($"Artwork {(int)response.StatusCode} ({uri.Host}{uri.AbsolutePath})");
                        return null;
                    }

                    wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(700 * attempt);
                }
                catch (Exception ex) when (attempt < Attempts)
                {
                    // A dropped connection: the image host sheds the odd one when a screen asks for many at once.
                    _ = ex;
                    wait = TimeSpan.FromMilliseconds(700 * attempt);
                }

                await Task.Delay(wait < TimeSpan.FromSeconds(10) ? wait : TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Artwork failed ({uri.Host}{uri.AbsolutePath}): {ex.Message}");
            return null;
        }
        finally
        {
            Downloads.Release();
        }
    }

    private static string Key(Uri uri)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(uri.Host + uri.AbsolutePath));
        return Convert.ToHexString(hash, 0, 16);
    }

    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
    }
}
