using System.Net;
using System.Net.Http.Headers;
using Bale.Core.Api.Models;

namespace Bale.Core.Tests;

internal static class PlaybackMediaTestSupport
{
    public static byte[] Data { get; } = Enumerable.Range(0, 3 * 1024 * 1024).Select(i => (byte)(i % 251)).ToArray();

    public static PlaybackPlan Plan(string? url = "/api/v2/stream/session/file.mp4") => new()
    {
        Stream = new StreamV3 { Url = url, MimeType = "video/mp4", Container = "mp4", Protocol = "http" },
        Source = new PlaybackSource { DurationSeconds = 1800 },
        Delivery = "original_http",
        Timeline = new TimelineV3 { SourceStartSeconds = 120, PlayerStartSeconds = 0, TimelineOffsetSeconds = 120 },
    };

    internal sealed class RangeHandler : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<MediaBody> Bodies { get; } = [];
        public bool IgnoreRanges { get; init; }
        public Func<int, int?>? FailAfterBytes { get; init; }
        public Func<Exception> ReadError { get; init; } = () => new IOException("Simulated dropped media connection.");
        public Action<HttpResponseMessage, int>? ConfigureResponse { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var range = IgnoreRanges ? null : request.Headers.Range?.Ranges.Single();
            var start = checked((int)(range?.From ?? 0));
            var end = checked((int)(range?.To ?? (Data.Length - 1)));
            var body = new MediaBody(start, end + 1, FailAfterBytes?.Invoke(Requests.Count), ReadError);
            Bodies.Add(body);
            var response = new HttpResponseMessage(range is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(body), RequestMessage = request,
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            response.Content.Headers.ContentLength = end - start + 1;
            if (range is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Data.Length);
            ConfigureResponse?.Invoke(response, Requests.Count);
            return Task.FromResult(response);
        }
    }

    internal sealed class MediaBody(int start, int end, int? failAfter, Func<Exception> readError) : Stream
    {
        private readonly int _start = start;
        private int _position = start;
        public bool IsDisposed { get; private set; }
        public override bool CanRead => !IsDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => end - _start;
        public override long Position { get => _position - _start; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (failAfter is { } limit && _position - _start >= limit) throw readError();
            var count = Math.Min(buffer.Length, end - _position);
            if (failAfter is { } maximum) count = Math.Min(count, maximum - (_position - _start));
            Data.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
