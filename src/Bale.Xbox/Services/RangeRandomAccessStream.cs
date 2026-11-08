using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Bale.Core.Playback;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace Bale.Xbox.Services;

/// <summary>Read-only, non-owning adapter that fills FFmpeg's native buffer without allocating a pinned replacement.</summary>
internal sealed partial class RangeRandomAccessStream : IRandomAccessStream
{
    private readonly Stream _stream;
    private volatile bool _disposed;

    internal RangeRandomAccessStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        _stream = stream;
    }

    public bool CanRead { get { ThrowIfDisposed(); return true; } }
    public bool CanWrite { get { ThrowIfDisposed(); return false; } }
    public ulong Position { get { ThrowIfDisposed(); return (ulong)_stream.Position; } }
    public ulong Size
    {
        get { ThrowIfDisposed(); return (ulong)_stream.Length; }
        set { ThrowIfDisposed(); throw new NotSupportedException(); }
    }

    public IAsyncOperationWithProgress<IBuffer, uint> ReadAsync(IBuffer buffer, uint count, InputStreamOptions options)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(buffer);
        if (options is not (InputStreamOptions.None or InputStreamOptions.Partial or InputStreamOptions.ReadAhead))
            throw new ArgumentOutOfRangeException(nameof(options));
        var length = checked((int)Math.Min(count, buffer.Capacity));
        return AsyncInfo.Run<IBuffer, uint>(async (ct, progress) =>
        {
            ThrowIfDisposed();
            buffer.Length = 0;
            var array = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                var read = await StreamFill.ReadAsync(_stream, array.AsMemory(0, length), count,
                    options == InputStreamOptions.Partial, ct).ConfigureAwait(false);
                // CsWinRT copies directly into IBufferByteAccess memory for native buffers, without a new managed buffer.
                array.CopyTo(0, buffer, 0, read);
                buffer.Length = (uint)read;
                progress.Report((uint)read);
                return buffer;
            }
            finally { ArrayPool<byte>.Shared.Return(array); }
        });
    }

    public void Seek(ulong position)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, (ulong)long.MaxValue);
        // Keep the underlying position authoritative; a seek between reads must not be overwritten by adapter state.
        _stream.Position = (long)position;
    }

    public IInputStream GetInputStreamAt(ulong position)
    {
        Seek(position);
        return this;
    }

    public IOutputStream GetOutputStreamAt(ulong position) { ThrowIfDisposed(); throw new NotSupportedException(); }
    public IRandomAccessStream CloneStream() { ThrowIfDisposed(); throw new NotSupportedException(); }
    public IAsyncOperationWithProgress<uint, uint> WriteAsync(IBuffer buffer) { ThrowIfDisposed(); throw new NotSupportedException(); }
    public IAsyncOperation<bool> FlushAsync() { ThrowIfDisposed(); throw new NotSupportedException(); }

    // Mount owns the authenticated stream and disposes it separately, after disposing this adapter.
    public void Dispose() => _disposed = true;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
