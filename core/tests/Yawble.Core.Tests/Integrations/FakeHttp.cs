using System.Net;
using System.Text;

namespace Yawble.Core.Tests.Integrations;

/// <summary>An HttpMessageHandler that serves a dictionary of URL to (status, content type, body), records every request and answers 404 to anything else. It never touches the network.</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    public List<HttpRequestMessage> Requests { get; } = [];
    public IEnumerable<string> Urls => Requests.Select(r => r.RequestUri!.AbsoluteUri);

    public FakeHttp Add(string url, string body, string contentType = "application/json", HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[url] = () => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };
        return this;
    }

    /// <summary>A response built from the request (to echo what was sent, or redirect).</summary>
    public FakeHttp Add(string url, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes[url] = () => respond(Requests[^1]);
        return this;
    }

    /// <summary>A body of <paramref name="length"/> bytes produced on demand (never held in memory); BytesServed counts what was actually read.</summary>
    public FakeHttp AddGenerated(string url, long length, string contentType = "application/json", long? declaredLength = null)
    {
        _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new GeneratedContent(length, contentType, n => BytesServed += n, declaredLength) };
        return this;
    }

    /// <summary>A response whose body read fails half way, as a dropped connection does.</summary>
    public FakeHttp AddDropped(string url)
    {
        _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new DroppedContent() };
        return this;
    }

    public long BytesServed { get; private set; }

    private readonly Dictionary<string, TimeSpan> _delays = new(StringComparer.Ordinal);

    /// <summary>The URL answers only after <paramref name="delay"/> (the caller's cancellation stops the wait), to test timeouts.</summary>
    public FakeHttp AddDelayed(string url, TimeSpan delay, string body = "{}")
    {
        _delays[url] = delay;
        return Add(url, body);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_delays.TryGetValue(request.RequestUri!.AbsoluteUri, out var delay)) await Task.Delay(delay, cancellationToken);
        var response = _routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var make) ? make() : new HttpResponseMessage(HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return response;
    }

    private sealed class DroppedContent : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => throw new IOException("connection reset");
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException("connection reset");
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class GeneratedContent : HttpContent
    {
        private readonly long _length;
        private readonly Action<int> _served;
        private readonly long? _declared;

        public GeneratedContent(long length, string contentType, Action<int> served, long? declared)
        {
            _declared = declared;
            _length = length;
            _served = served;
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        }

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Gen(_length, _served));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => new Gen(_length, _served).CopyToAsync(stream);
        protected override bool TryComputeLength(out long length) { length = _declared ?? 0; return _declared is not null; }

        private sealed class Gen(long length, Action<int> served) : Stream
        {
            private long _pos;
            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
            public override int Read(Span<byte> buffer)
            {
                var n = (int)Math.Min(buffer.Length, length - _pos);
                buffer[..n].Fill((byte)'a');
                _pos += n;
                served(n);
                return n;
            }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _pos; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
