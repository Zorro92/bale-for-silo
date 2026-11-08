namespace Bale.Core.Playback;

/// <summary>Fills a bounded stream read, or returns the first available bytes for a partial read.</summary>
public static class StreamFill
{
    public static async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer, uint count, bool partial,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var length = (int)Math.Min(count, (uint)buffer.Length);
        var total = 0;
        while (total < length)
        {
            var read = await stream.ReadAsync(buffer.Slice(total, length - total), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (partial) break;
        }
        return total;
    }
}
