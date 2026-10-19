using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Dalamud.Networking.Http;

/// <summary>
/// One download of <see cref="SegmentedDownloader"/> in progress: the ranges still to fetch and the workers fetching them.
/// </summary>
/// <remarks>
/// <para>
/// The first worker reads the first request's response. Extra workers come from the downloader's shared pool, are never
/// waited for, and leave once nothing is left to take. An idle worker takes the next pending range, or else splits off
/// the back half of the largest range still being fetched.
/// </para>
/// <para>
/// A worker owns its segment from Position to End: only it advances Position, and a split only lowers End to a point
/// past Position. Every mutable field is guarded by <c>sync</c>, except the counters read after all workers stopped.
/// Progress is reported by the coordinating loop alone, so the callback never overlaps itself.
/// </para>
/// </remarks>
internal sealed class SegmentedDownloadJob
{
    // A rate measured over less than this says little: the connection may still be ramping up.
    private const long RateWindowMs = 1000;

    private readonly SegmentedDownloader owner;
    private readonly SegmentedDownloadOptions options;
    private readonly Uri sourceUrl;
    private readonly Action<HttpRequestMessage>? prepareRequest;
    private readonly Action<long> onProgress;
    private readonly CancellationToken callerToken;
    private readonly CancellationTokenSource jobCts;
    private readonly int maxConnections;
    private readonly long total;
    private readonly byte[] data;
    private readonly string? etag;
    private readonly DateTimeOffset? lastModified;
    private readonly TaskCompletionSource allWorkersDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object sync = new();
    private readonly List<Segment> pending = new();
    private readonly List<Segment> active = new();

    private Uri currentUrl;
    private int linkVersion;
    private Task<LinkRefresh>? linkRefresh;
    private int refreshesWithoutProgress;
    private long received;
    private int workers;
    private int transfers;
    private int peakTransfers;
    private int requests;
    private int retries;
    private int linkRefreshes;
    private int splits;
    private string? giveUpReason;
    private Exception? giveUpException;

    /// <summary>
    /// Initializes a new instance of the <see cref="SegmentedDownloadJob"/> class.
    /// </summary>
    /// <param name="owner">The downloader, for its client, options and shared connection pool.</param>
    /// <param name="sourceUrl">The URL the caller asked for, asked again for a new link when the current one is rejected.</param>
    /// <param name="currentUrl">Where the first request ended up after redirects.</param>
    /// <param name="prepareRequest">Adds the caller's headers to every request.</param>
    /// <param name="maxConnections">The most requests in flight at once, the first one included.</param>
    /// <param name="total">The file size.</param>
    /// <param name="etag">The strong entity tag of the first response, if it had one.</param>
    /// <param name="lastModified">The Last-Modified of the first response; only compared when there is no tag.</param>
    /// <param name="onProgress">Receives newly received byte counts, one call at a time.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    public SegmentedDownloadJob(
        SegmentedDownloader owner,
        Uri sourceUrl,
        Uri currentUrl,
        Action<HttpRequestMessage>? prepareRequest,
        int maxConnections,
        long total,
        string? etag,
        DateTimeOffset? lastModified,
        Action<long> onProgress,
        CancellationToken cancellationToken)
    {
        this.owner = owner;
        this.options = owner.Options;
        this.sourceUrl = sourceUrl;
        this.currentUrl = currentUrl;
        this.prepareRequest = prepareRequest;
        this.maxConnections = maxConnections;
        this.total = total;
        this.etag = etag;
        this.lastModified = lastModified;
        this.onProgress = onProgress;
        this.callerToken = cancellationToken;
        this.data = new byte[total];
        this.jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    private enum StepKind
    {
        SegmentDone,
        RequestAgain,
        Retry,
        RefreshLink,
        GiveUp,
    }

    private enum LinkRefresh
    {
        Refreshed,
        Failed,
        Unusable,
        LimitReached,
    }

    /// <summary>
    /// Downloads the rest of the file, starting with the first request's response.
    /// </summary>
    /// <param name="firstResponse">The validated first range response. This method takes ownership of it.</param>
    /// <param name="firstTo">The last byte that response covers.</param>
    /// <returns>The file, or why the download gave up. Throws <see cref="OperationCanceledException"/> when the caller cancelled.</returns>
    public async Task<SegmentedDownloadResult> RunAsync(HttpResponseMessage firstResponse, long firstTo)
    {
        var first = new Segment(0, Math.Min(this.total, firstTo + 1));
        lock (this.sync)
        {
            this.active.Add(first);
            for (var start = first.End; start < this.total; start += this.options.SegmentBytes)
                this.pending.Add(new Segment(start, Math.Min(this.total, start + this.options.SegmentBytes)));

            this.workers = 1;
            this.requests = 1;
        }

        _ = Task.Run(() => this.RunWorkerAsync(first, firstResponse, false));

        try
        {
            await this.CoordinateAsync().ConfigureAwait(false);
        }
        finally
        {
            this.jobCts.Dispose();
        }

        var stats = new SegmentedDownloadStats(
            this.peakTransfers, this.requests, this.retries, this.linkRefreshes, this.splits, this.received);
        if (this.giveUpReason is null && this.received == this.total)
            return SegmentedDownloadResult.Completed(this.data, stats);

        this.callerToken.ThrowIfCancellationRequested();
        return SegmentedDownloadResult.GaveUp(
            this.giveUpReason ?? $"only {this.received} of {this.total} bytes arrived", this.giveUpException, stats);
    }

    private static bool IsTransient(Exception ex) => ex is HttpRequestException or IOException;

    // Returns only once every worker has stopped, whatever happens here.
    private async Task CoordinateAsync()
    {
        var reported = 0L;
        try
        {
            this.SpawnExtraWorkers();
            while (!this.allWorkersDone.Task.IsCompleted)
            {
                await Task.WhenAny(this.allWorkersDone.Task, Task.Delay(this.options.ProgressInterval))
                          .ConfigureAwait(false);
                reported = this.ReportProgress(reported);
                this.SpawnExtraWorkers();
            }

            // The last worker may have finished after the report above; everything it stored is visible now.
            this.ReportProgress(reported);
        }
        catch (Exception ex)
        {
            this.GiveUp("reporting progress failed", ex);
            await this.allWorkersDone.Task.ConfigureAwait(false);
        }
    }

    private long ReportProgress(long reported)
    {
        var now = Interlocked.Read(ref this.received);
        if (now <= reported)
            return reported;

        this.onProgress(now - reported);
        return now;
    }

    private void SpawnExtraWorkers()
    {
        while (true)
        {
            lock (this.sync)
            {
                if (this.allWorkersDone.Task.IsCompleted || this.giveUpReason is not null ||
                    this.jobCts.IsCancellationRequested || this.workers >= this.maxConnections)
                {
                    return;
                }

                if (this.pending.Count == 0 && this.FindSplittable(Environment.TickCount64) is null)
                    return;

                if (!this.owner.TryTakeExtraConnection())
                    return;

                this.workers++;
            }

            _ = Task.Run(() => this.RunWorkerAsync(null, null, true));
        }
    }

    private async Task RunWorkerAsync(Segment? segment, HttpResponseMessage? response, bool holdsExtraConnection)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(this.options.ChunkBytes);
        try
        {
            while (true)
            {
                segment ??= this.TakeSegment();
                if (segment is null)
                    return;

                var step = await this.TransferAsync(segment, response, chunk).ConfigureAwait(false);
                response = null;

                switch (step.Kind)
                {
                    case StepKind.SegmentDone:
                        segment = null;
                        break;

                    case StepKind.RequestAgain:
                        break;

                    case StepKind.Retry:
                        if (!await this.WaitBeforeRetryAsync(segment, step).ConfigureAwait(false))
                            return;
                        break;

                    case StepKind.RefreshLink:
                        var refresh = await this.RefreshLinkAsync(step.LinkVersion).ConfigureAwait(false);
                        if (refresh == LinkRefresh.Failed)
                        {
                            var failed = new Step(StepKind.Retry, $"getting a new download link failed after {step.Reason}");
                            if (!await this.WaitBeforeRetryAsync(segment, failed).ConfigureAwait(false))
                                return;
                        }
                        else if (refresh != LinkRefresh.Refreshed)
                        {
                            this.GiveUp(
                                refresh == LinkRefresh.LimitReached
                                    ? $"the download link kept being rejected ({step.Reason})"
                                    : "the new download link no longer serves the same file in ranges");
                            return;
                        }

                        break;

                    default:
                        this.GiveUp(step.Reason ?? "unknown error", step.Exception);
                        return;
                }
            }
        }
        catch (Exception ex)
        {
            if (!this.jobCts.IsCancellationRequested)
                this.GiveUp("unexpected error", ex);
        }
        finally
        {
            // The count must drop whatever happens above: the download only returns once it reaches zero.
            try
            {
                response?.Dispose();
                ArrayPool<byte>.Shared.Return(chunk);
                if (holdsExtraConnection)
                    this.owner.ReturnExtraConnection();
            }
            finally
            {
                lock (this.sync)
                {
                    if (--this.workers == 0)
                        this.allWorkersDone.TrySetResult();
                }
            }
        }
    }

    private Segment? TakeSegment()
    {
        lock (this.sync)
        {
            if (this.giveUpReason is not null || this.jobCts.IsCancellationRequested)
                return null;

            if (this.pending.Count > 0)
            {
                var next = this.pending[0];
                this.pending.RemoveAt(0);
                this.active.Add(next);
                return next;
            }

            var victim = this.FindSplittable(Environment.TickCount64);
            if (victim is null)
                return null;

            var middle = victim.Position + ((victim.End - victim.Position) / 2);
            var stolen = new Segment(middle, victim.End);
            victim.End = middle;
            this.active.Add(stolen);
            this.splits++;
            return stolen;
        }
    }

    // Caller holds sync.
    private Segment? FindSplittable(long now)
    {
        Segment? best = null;
        var bestLeft = (2 * this.options.MinSplitBytes) - 1;
        foreach (var segment in this.active)
        {
            var left = segment.End - segment.Position;
            if (left > bestLeft && !segment.FinishesWithin(now, this.options.NoSplitWithin))
            {
                best = segment;
                bestLeft = left;
            }
        }

        return best;
    }

    private async Task<Step> TransferAsync(Segment segment, HttpResponseMessage? response, byte[] chunk)
    {
        long from, to;
        Uri url;
        int version;
        lock (this.sync)
        {
            from = segment.Position;
            to = segment.End - 1;
            url = this.currentUrl;
            version = this.linkVersion;
            if (from > to)
            {
                this.active.Remove(segment);
                response?.Dispose();
                return new Step(StepKind.SegmentDone);
            }

            this.transfers++;
            this.peakTransfers = Math.Max(this.peakTransfers, this.transfers);
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(this.jobCts.Token);
        attemptCts.CancelAfter(this.options.IdleTimeout);
        try
        {
            if (response is null)
            {
                using var request = this.owner.CreateRangeRequest(url, from, to, this.prepareRequest);
                Interlocked.Increment(ref this.requests);
                response = await this.owner.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token)
                                     .ConfigureAwait(false);
            }

            if (this.CheckResponse(response, from, version) is { } rejected)
                return rejected;

            var serverEnd = response.Content.Headers.ContentRange!.To!.Value + 1;
            attemptCts.CancelAfter(this.options.IdleTimeout);
            var body = await response.Content.ReadAsStreamAsync(attemptCts.Token).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                lock (this.sync)
                {
                    segment.RateStartTicks = Environment.TickCount64;
                    segment.RateBytes = 0;
                }

                var position = from;
                while (position < serverEnd)
                {
                    var wanted = (int)Math.Min(chunk.Length, serverEnd - position);
                    var read = await body.ReadAsync(chunk.AsMemory(0, wanted), attemptCts.Token).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    attemptCts.CancelAfter(this.options.IdleTimeout);
                    if (this.Store(segment, position, chunk, read))
                        return new Step(StepKind.SegmentDone);

                    position += read;
                }

                // A server may send less than was asked for; only a body that stops short of its own range is broken.
                return position >= serverEnd
                           ? new Step(StepKind.RequestAgain)
                           : new Step(StepKind.Retry, "the connection closed before the range was complete");
            }
        }
        catch (OperationCanceledException) when (!this.jobCts.IsCancellationRequested)
        {
            return new Step(StepKind.Retry, $"nothing arrived for {this.options.IdleTimeout.TotalSeconds:0.#} seconds");
        }
        catch (Exception ex) when (IsTransient(ex) && !this.jobCts.IsCancellationRequested)
        {
            return new Step(StepKind.Retry, ex.GetType().Name, ex);
        }
        finally
        {
            response?.Dispose();
            lock (this.sync)
                this.transfers--;
        }
    }

    // Returns whether the segment is complete.
    private bool Store(Segment segment, long position, byte[] chunk, int read)
    {
        long destination;
        int useful;
        bool done;
        lock (this.sync)
        {
            if (position != segment.Position)
                throw new InvalidOperationException("A segment was written by two workers.");

            useful = (int)Math.Min(read, segment.End - segment.Position);
            destination = segment.Position;
            segment.Position += useful;
            segment.RateBytes += useful;
            if (useful > 0)
            {
                segment.Failures = 0;
                this.refreshesWithoutProgress = 0;
            }

            done = segment.Position >= segment.End;
            if (done)
                this.active.Remove(segment);
        }

        // The reserved bytes are this worker's alone, so the copy needs no lock.
        Buffer.BlockCopy(chunk, 0, this.data, checked((int)destination), useful);
        Interlocked.Add(ref this.received, useful);
        return done;
    }

    private Step? CheckResponse(HttpResponseMessage response, long from, int version)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (!SegmentedDownloader.IsCompleteByteRange(range) || range!.From != from || range.To is not { } to || to < from)
                return new Step(StepKind.GiveUp, "the server answered with a different range");

            if (range.Length != this.total || !this.IsSameFile(response))
                return new Step(StepKind.GiveUp, "the file changed during the download");

            if (SegmentedDownloader.IsEncoded(response))
                return new Step(StepKind.GiveUp, "the server compressed a range");

            if (response.Content.Headers.ContentLength is { } length && length != to - from + 1)
                return new Step(StepKind.GiveUp, "Content-Length does not match Content-Range");

            return null;
        }

        return status switch
        {
            200 => new Step(StepKind.GiveUp, "the server stopped honouring range requests"),
            401 or 403 or 404 or 410 => new Step(StepKind.RefreshLink, $"HTTP {status}", LinkVersion: version),
            408 or 429 or >= 500 => new Step(StepKind.Retry, $"HTTP {status}"),
            _ => new Step(StepKind.GiveUp, $"HTTP {status}"),
        };
    }

    private bool IsSameFile(HttpResponseMessage response)
        => this.etag is not null
               ? SegmentedDownloader.GetStrongETag(response) == this.etag
               : response.Content.Headers.LastModified == this.lastModified;

    // Returns false when the download has given up instead.
    private async Task<bool> WaitBeforeRetryAsync(Segment segment, Step step)
    {
        int failures;
        lock (this.sync)
        {
            segment.RateStartTicks = 0;
            failures = ++segment.Failures;
            if (failures < this.options.MaxAttempts)
                this.retries++;
        }

        if (failures >= this.options.MaxAttempts)
        {
            this.GiveUp($"one range failed {failures} times in a row, last because {step.Reason}", step.Exception);
            return false;
        }

        var delay = TimeSpan.FromTicks(this.options.RetryBaseDelay.Ticks << Math.Min(failures - 1, 10));
        await Task.Delay(delay, this.jobCts.Token).ConfigureAwait(false);
        return true;
    }

    private async Task<LinkRefresh> RefreshLinkAsync(int seenVersion)
    {
        Task<LinkRefresh> refresh;
        lock (this.sync)
        {
            if (this.linkVersion != seenVersion)
                return LinkRefresh.Refreshed;

            if (this.linkRefresh is null)
            {
                if (this.refreshesWithoutProgress >= this.options.MaxRefreshesWithoutProgress)
                    return LinkRefresh.LimitReached;

                this.refreshesWithoutProgress++;
                this.linkRefreshes++;
                this.linkRefresh = Task.Run(this.FetchNewLinkAsync);
            }

            refresh = this.linkRefresh;
        }

        return await refresh.ConfigureAwait(false);
    }

    // Asks the source URL for one byte: the answer confirms the file is unchanged and reveals where it redirects now.
    private async Task<LinkRefresh> FetchNewLinkAsync()
    {
        var result = LinkRefresh.Failed;
        Uri? newUrl = null;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.jobCts.Token);
            cts.CancelAfter(this.options.IdleTimeout);
            using var request = this.owner.CreateRangeRequest(this.sourceUrl, 0, 0, this.prepareRequest);
            Interlocked.Increment(ref this.requests);
            using var response = await this.owner.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                                           .ConfigureAwait(false);
            newUrl = response.RequestMessage?.RequestUri ?? this.sourceUrl;
            result = this.CheckNewLink(response);
        }
        catch (OperationCanceledException) when (!this.jobCts.IsCancellationRequested)
        {
            result = LinkRefresh.Failed;
        }
        catch (Exception ex) when (IsTransient(ex) && !this.jobCts.IsCancellationRequested)
        {
            result = LinkRefresh.Failed;
        }
        finally
        {
            lock (this.sync)
            {
                if (result == LinkRefresh.Refreshed)
                {
                    this.currentUrl = newUrl!;
                    this.linkVersion++;
                }

                this.linkRefresh = null;
            }
        }

        return result;
    }

    private LinkRefresh CheckNewLink(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.OK)
            return LinkRefresh.Unusable;

        if (response.StatusCode != HttpStatusCode.PartialContent)
            return LinkRefresh.Failed;

        var range = response.Content.Headers.ContentRange;
        if (!SegmentedDownloader.IsCompleteByteRange(range) || range!.From != 0 || range.Length != this.total ||
            !this.IsSameFile(response) || SegmentedDownloader.IsEncoded(response))
        {
            return LinkRefresh.Unusable;
        }

        return LinkRefresh.Refreshed;
    }

    private void GiveUp(string reason, Exception? exception = null)
    {
        lock (this.sync)
        {
            if (this.giveUpReason is not null)
                return;

            this.giveUpReason = reason;
            this.giveUpException = exception;
        }

        try
        {
            this.jobCts.Cancel();
        }
        catch (AggregateException)
        {
            // A cancellation callback failed; the workers still see the token as cancelled.
        }
    }

    private readonly record struct Step(StepKind Kind, string? Reason = null, Exception? Exception = null, int LinkVersion = 0);

    private sealed class Segment
    {
        public Segment(long start, long end)
        {
            this.Position = start;
            this.End = end;
        }

        public long Position { get; set; }

        public long End { get; set; }

        public int Failures { get; set; }

        // When the body of the current request started arriving; 0 while no rate is being measured.
        public long RateStartTicks { get; set; }

        public long RateBytes { get; set; }

        public bool FinishesWithin(long now, TimeSpan window)
        {
            var elapsed = now - this.RateStartTicks;
            if (this.RateStartTicks == 0 || elapsed < RateWindowMs || this.RateBytes <= 0)
                return false;

            return (this.End - this.Position) * elapsed / this.RateBytes < window.TotalMilliseconds;
        }
    }
}
