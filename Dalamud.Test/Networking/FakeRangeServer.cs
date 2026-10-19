#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Dalamud.Test.Networking
{
    /// <summary>How a <see cref="FakeReply.CutAfter"/> body stops.</summary>
    public enum CutKind
    {
        /// <summary>The connection is reset: an <see cref="IOException"/> wrapping a socket error.</summary>
        Reset,

        /// <summary>The body ends before its Content-Length, the way SocketsHttpHandler reports it.</summary>
        Premature,

        /// <summary>The stream just ends early without an error.</summary>
        CleanEnd,

        /// <summary>Nothing more arrives until the request is cancelled.</summary>
        Stall,
    }

    /// <summary>
    /// An in-process stand-in for GitHub's release-asset API and the storage it redirects to. Requests to
    /// <see cref="SourceUrl"/> are "redirected" the way SocketsHttpHandler does it, by rewriting the request URI, and
    /// storage URLs carry a link version that <see cref="ExpireLinks"/> invalidates (403 afterwards).
    /// </summary>
    internal sealed class FakeRangeServer : HttpMessageHandler
    {
        private readonly object sync = new();
        private int linkVersion = 1;
        private int requestCount;
        private int open;
        private int maxOpen;

        public FakeRangeServer(byte[] content, string name = "Test")
        {
            this.Content = content;
            this.Name = name;
            this.SourceUrl = new Uri($"https://api.github.com/repos/ffxiv-tc-port/{name}/releases/assets/1");
        }

        public byte[] Content { get; }

        public string Name { get; }

        public Uri SourceUrl { get; }

        public string? ETag { get; set; } = "\"0x8DD0123456789AB\"";

        public DateTimeOffset? LastModified { get; set; }

        public bool Redirect { get; set; } = true;

        public bool SupportsRanges { get; set; } = true;

        public string? ContentEncoding { get; set; }

        public int BytesPerRead { get; set; } = 4096;

        public TimeSpan ReadDelay { get; set; } = TimeSpan.Zero;

        /// <summary>Gets or sets the body answered to asset metadata requests; 404 when null.</summary>
        public string? MetadataJson { get; set; }

        /// <summary>Gets or sets a hook deciding how to misbehave for one request; null answers normally.</summary>
        public Func<FakeRequest, FakeReply?>? Reply { get; set; }

        /// <summary>Gets or sets a counter shared with other servers, to see open bodies across all of them.</summary>
        public ConcurrencyCounter? SharedCounter { get; set; }

        public ConcurrentQueue<FakeRequest> Requests { get; } = new();

        public int OpenBodies => Volatile.Read(ref this.open);

        public int MaxOpenBodies => Volatile.Read(ref this.maxOpen);

        public int CurrentLinkVersion => Volatile.Read(ref this.linkVersion);

        public void ExpireLinks() => Interlocked.Increment(ref this.linkVersion);

        public FakeRequest[] RequestsTo(bool source) => this.Requests.Where(r => r.ToSource == source && !r.Metadata).ToArray();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref this.requestCount);
            var toSource = request.RequestUri == this.SourceUrl;
            var metadata = toSource && request.Headers.Accept.Any(a => a.MediaType == "application/vnd.github+json");
            var range = request.Headers.Range?.Ranges.FirstOrDefault();

            int version;
            if (toSource && !metadata)
            {
                version = this.CurrentLinkVersion;
                if (this.Redirect)
                    request.RequestUri = new Uri($"https://storage.example/{this.Name}?v={version}&sig=secret");
            }
            else
            {
                var query = request.RequestUri!.Query;
                version = query.StartsWith("?v=") ? int.Parse(query.AsSpan(3, query.IndexOf('&') - 3)) : 0;
            }

            var info = new FakeRequest(
                index,
                toSource,
                metadata,
                range?.From,
                range?.To,
                version,
                request.Headers.AcceptEncoding.ToString(),
                request.Version,
                request.Headers.Accept.ToString());
            this.Requests.Enqueue(info);

            var reply = this.Reply?.Invoke(info) ?? new FakeReply();
            if (reply.HeaderDelay > TimeSpan.Zero)
                await Task.Delay(reply.HeaderDelay, cancellationToken);

            if (metadata)
            {
                return this.MetadataJson is null
                           ? Empty(request, HttpStatusCode.NotFound)
                           : new HttpResponseMessage(HttpStatusCode.OK)
                           {
                               RequestMessage = request,
                               Content = new StringContent(this.MetadataJson, Encoding.UTF8, "application/json"),
                           };
            }

            if (reply.Status is { } status)
                return Empty(request, (HttpStatusCode)status);

            if (!toSource && version != this.CurrentLinkVersion)
                return Empty(request, HttpStatusCode.Forbidden);

            var total = this.Content.LongLength;
            long from = 0, to = total - 1;
            var partial = range is not null && this.SupportsRanges && !reply.Whole;
            if (partial)
            {
                from = range!.From ?? 0;
                to = Math.Min(range.To ?? (total - 1), total - 1);
                if (reply.ShortenTo is { } shorter)
                    to = Math.Min(to, shorter);
                if (from > to)
                    return Empty(request, HttpStatusCode.RequestedRangeNotSatisfiable);
            }

            var count = to - from + 1;
            var body = new FakeBody(this, from, count, reply);
            var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StreamContent(body),
            };
            response.Content.Headers.ContentLength = count;
            if (partial)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);

            var etag = reply.ETag ?? this.ETag;
            if (etag is not null)
                response.Headers.ETag = new EntityTagHeaderValue(etag);

            if (this.LastModified is { } lastModified)
                response.Content.Headers.LastModified = lastModified;

            var encoding = reply.ContentEncoding ?? this.ContentEncoding;
            if (encoding is not null)
                response.Content.Headers.ContentEncoding.Add(encoding);

            this.Opened();
            return response;
        }

        private static HttpResponseMessage Empty(HttpRequestMessage request, HttpStatusCode status)
            => new(status) { RequestMessage = request, Content = new ByteArrayContent([]) };

        private void Opened()
        {
            var now = Interlocked.Increment(ref this.open);
            lock (this.sync)
                this.maxOpen = Math.Max(this.maxOpen, now);
            this.SharedCounter?.Enter();
        }

        private void Closed()
        {
            Interlocked.Decrement(ref this.open);
            this.SharedCounter?.Exit();
        }

        private sealed class FakeBody : Stream
        {
            private readonly FakeRangeServer server;
            private readonly long offset;
            private readonly long count;
            private readonly FakeReply reply;
            private long delivered;
            private int disposed;

            public FakeBody(FakeRangeServer server, long offset, long count, FakeReply reply)
            {
                this.server = server;
                this.offset = offset;
                this.count = count;
                this.reply = reply;
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (this.reply.CutAfter is { } cut && this.delivered >= cut)
                {
                    switch (this.reply.Cut)
                    {
                        case CutKind.Reset:
                            throw new IOException("reset by peer", new SocketException((int)SocketError.ConnectionReset));
                        case CutKind.Premature:
                            throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
                        case CutKind.CleanEnd:
                            return 0;
                        default:
                            await Task.Delay(Timeout.Infinite, cancellationToken);
                            return 0;
                    }
                }

                var remaining = this.count - this.delivered;
                if (remaining <= 0 || buffer.Length == 0)
                    return 0;

                if (this.server.ReadDelay > TimeSpan.Zero)
                    await Task.Delay(this.server.ReadDelay, cancellationToken);

                var n = (int)Math.Min(Math.Min(buffer.Length, this.server.BytesPerRead), remaining);
                if (this.reply.CutAfter is { } limit)
                    n = (int)Math.Min(n, limit - this.delivered);

                this.server.Content.AsMemory((int)(this.offset + this.delivered), n).CopyTo(buffer);
                this.delivered += n;
                return n;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
                => this.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count)
                => this.ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (Interlocked.Exchange(ref this.disposed, 1) == 0)
                    this.server.Closed();
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>One request the fake server saw.</summary>
    internal sealed record FakeRequest(
        int Index,
        bool ToSource,
        bool Metadata,
        long? From,
        long? To,
        int LinkVersion,
        string AcceptEncoding,
        Version HttpVersion,
        string Accept);

    /// <summary>How the fake server misbehaves for one request.</summary>
    internal sealed record FakeReply
    {
        /// <summary>Gets the status to answer with an empty body instead of the file.</summary>
        public int? Status { get; init; }

        /// <summary>Gets a value indicating whether to send the whole file (200) whatever the range.</summary>
        public bool Whole { get; init; }

        /// <summary>Gets the last byte of a shorter range to answer with.</summary>
        public long? ShortenTo { get; init; }

        /// <summary>Gets the number of body bytes after which the body stops, the way <see cref="Cut"/> says.</summary>
        public long? CutAfter { get; init; }

        public CutKind Cut { get; init; }

        public string? ETag { get; init; }

        public string? ContentEncoding { get; init; }

        public TimeSpan HeaderDelay { get; init; }
    }

    /// <summary>Counts bodies open at once across several servers.</summary>
    internal sealed class ConcurrencyCounter
    {
        private int current;
        private int max;

        public int Max => Volatile.Read(ref this.max);

        public void Enter()
        {
            var now = Interlocked.Increment(ref this.current);
            int seen;
            while (now > (seen = Volatile.Read(ref this.max)) && Interlocked.CompareExchange(ref this.max, now, seen) != seen)
            {
            }
        }

        public void Exit() => Interlocked.Decrement(ref this.current);
    }
}
