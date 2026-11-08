namespace Bale.Core.Tests;

public class RangeRandomAccessStreamTests
{
    [Fact]
    public void AdapterPoolsTemporaryStorageAndReturnsTheCallersBufferInEveryBuild()
    {
        var source = Read();
        Assert.DoesNotContain("#if DEBUG_SERVER", source);
        Assert.Contains("internal sealed partial class RangeRandomAccessStream : IRandomAccessStream", source);
        Assert.Contains("checked((int)Math.Min(count, buffer.Capacity))", source);
        Assert.Contains("AsyncInfo.Run<IBuffer, uint>", source);
        Assert.Contains("ArrayPool<byte>.Shared.Rent(length)", source);
        Assert.Contains("StreamFill.ReadAsync(_stream, array.AsMemory(0, length), count,", source);
        Assert.Contains("options == InputStreamOptions.Partial, ct).ConfigureAwait(false)", source);
        Assert.Contains("array.CopyTo(0, buffer, 0, read);", source);
        Assert.Contains("buffer.Length = (uint)read;", source);
        Assert.Contains("return buffer;", source);
        Assert.Contains("finally { ArrayPool<byte>.Shared.Return(array); }", source);
        Assert.DoesNotContain(".AsBuffer(", source);
        Assert.DoesNotContain("new byte[", source);
    }

    [Fact]
    public void PositionIsAuthoritativeAndDisposalDoesNotCloseTheOwnedStream()
    {
        var source = Read();
        Assert.Contains("return (ulong)_stream.Position;", source);
        Assert.Contains("return (ulong)_stream.Length;", source);
        Assert.Contains("_stream.Position = (long)position;", source);
        Assert.Contains("ArgumentOutOfRangeException.ThrowIfGreaterThan(position, (ulong)long.MaxValue)", source);
        Assert.Contains("Seek(position);\n        return this;", source);
        Assert.Contains("public void Dispose() => _disposed = true;", source);
        Assert.Contains("ObjectDisposedException.ThrowIf(_disposed, this)", source);
        Assert.DoesNotContain("_stream.Dispose", source);
        Assert.DoesNotContain("_position", source);
        Assert.Contains("public bool CanRead { get { ThrowIfDisposed(); return true; } }", source);
        Assert.Contains("public bool CanWrite { get { ThrowIfDisposed(); return false; } }", source);
        foreach (var member in new[] { "GetOutputStreamAt(ulong position)", "CloneStream()", "WriteAsync(IBuffer buffer)", "FlushAsync()" })
            Assert.Contains(member + " { ThrowIfDisposed(); throw new NotSupportedException(); }", source);
    }

    private static string Read() => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RangeRandomAccessStream.cs.txt"));
}
