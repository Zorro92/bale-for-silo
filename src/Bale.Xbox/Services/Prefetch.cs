using Windows.Web.Http;

namespace Bale.Xbox.Services;

/// <summary>
/// Warms the system HTTP cache with artwork the user is about to see, as Silo's TV feed does
/// (TvSkylineSectionFeed: once focus settles, the backdrop and logo of the two cards either side).
/// XAML images and Composition surfaces load through the same WinINet cache, so a later load of the
/// same URL is served locally (the image host allows an hour: cache-control max-age=3600).
/// </summary>
internal static class Prefetch
{
    private const int Remembered = 400;

    private static readonly HttpClient Client = new();
    private static readonly SemaphoreSlim Slots = new(3);
    private static readonly HashSet<Uri> Seen = new();
    private static readonly Queue<Uri> Order = new();

    public static void Warm(Uri? uri)
    {
        if (uri is null || !Seen.Add(uri))
        {
            return;
        }

        Order.Enqueue(uri);
        if (Order.Count > Remembered)
        {
            Seen.Remove(Order.Dequeue());
        }

        _ = FetchAsync(uri);
    }

    private static async Task FetchAsync(Uri uri)
    {
        await Slots.WaitAsync();
        try
        {
            using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseContentRead);
        }
        catch (Exception)
        {
            // A miss only costs the normal load later; let it be retried then.
            Seen.Remove(uri);
        }
        finally
        {
            Slots.Release();
        }
    }
}
