using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Bale.Core.Playback;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Bale.Xbox.Services;

/// <summary>
/// Non-owning IInputStream over a forward-only progressive remux for
/// FFmpegMediaSource.CreateFromInputStreamAsync (FFmpeg's non-seekable AVIO: no seek callback).
/// Its operations never fault: the wrapper's InputStreamRead calls ReadAsync(...).get() inside
/// FFmpeg's C read callback without a try/catch, so a failure, disposal or cancellation returns an
/// empty buffer (end of data). ProgressiveHttpStream.Faulted tells the player why it ended.
/// </summary>
internal sealed partial class ForwardInputStream : IInputStream
{
    private readonly ProgressiveHttpStream _stream;
    private volatile bool _disposed;

    internal ForwardInputStream(ProgressiveHttpStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return AsyncInfo.Run<IBuffer, uint>(async (ct, progress) =>
        {
            byte[]? array = null;
            try
            {
                buffer.Length = 0;
                var length = (int)Math.Min(count, buffer.Capacity);
                if (_disposed || length == 0) return buffer;
                array = ArrayPool<byte>.Shared.Rent(length);
                var read = await StreamFill.ReadAsync(_stream, array.AsMemory(0, length), (uint)length,
                    options == InputStreamOptions.Partial, ct).ConfigureAwait(false);
                if (read > 0 && !_disposed)
                {
                    array.CopyTo(0, buffer, 0, read);
                    buffer.Length = (uint)read;
                    progress.Report((uint)read);
                }
                return buffer;
            }
            catch (Exception)
            {
                // StreamFill/ProgressiveHttpStream do not throw for transport failures; this also
                // covers buffer projection failures. Never let one reach FFmpeg's read callback.
                try { buffer.Length = 0; } catch (Exception) { }
                return buffer;
            }
            finally
            {
                if (array is not null) ArrayPool<byte>.Shared.Return(array);
            }
        });
    }

    // Mount owns the authenticated stream and disposes it after the FFmpeg source.
    public void Dispose() => _disposed = true;
}
