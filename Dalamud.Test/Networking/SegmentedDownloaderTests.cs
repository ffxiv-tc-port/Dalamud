#nullable enable

using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Networking.Http;

using Xunit;

namespace Dalamud.Test.Networking
{
    public class SegmentedDownloaderTests
    {
        internal static readonly SegmentedDownloadOptions FastOptions = new()
        {
            FirstRangeBytes = 8 * 1024,
            SegmentBytes = 8 * 1024,
            MinSplitBytes = 1024,
            IdleTimeout = TimeSpan.FromSeconds(10),
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
            ProgressInterval = TimeSpan.FromMilliseconds(10),
            NoSplitWithin = TimeSpan.Zero,
            ChunkBytes = 1024,
        };

        [Fact]
        public async Task DownloadsOverSeveralConnectionsIntoIdenticalBytes()
        {
            var server = Server(100 * 1024);
            server.BytesPerRead = 1024;
            server.ReadDelay = TimeSpan.FromMilliseconds(1);
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 4);

            AssertCompleted(run, server);
            Assert.InRange(run.Result.Stats.Connections, 2, 4);
            Assert.InRange(server.MaxOpenBodies, 2, 4);

            var first = server.Requests.First();
            Assert.True(first.ToSource);
            Assert.Equal(0, first.From);
            Assert.Equal(8 * 1024 - 1, first.To);
            Assert.All(server.Requests.Skip(1), r => Assert.False(r.ToSource));
            Assert.All(server.Requests, r => Assert.Equal("identity", r.AcceptEncoding));
            Assert.All(server.Requests, r => Assert.Equal(new Version(1, 1), r.HttpVersion));
            Assert.All(server.Requests, r => Assert.Contains("application/octet-stream", r.Accept));
            Assert.Equal(16, downloader.AvailableExtraConnections);
        }

        [Fact]
        public async Task ServerIgnoringRangesHandsBackTheWholeResponse()
        {
            var server = Server(50 * 1024);
            server.SupportsRanges = false;
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 8);

            Assert.Equal(SegmentedDownloadOutcome.WholeResponse, run.Result.Outcome);
            using (var response = run.Result.WholeResponse!)
                Assert.Equal(server.Content, await response.Content.ReadAsByteArrayAsync());

            Assert.Single(server.Requests);
            Assert.Equal(-1, run.Total);
            Assert.Equal(0, run.Reported);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task CompressedResponsesAreNotSplit(bool ranged)
        {
            var server = Server(50 * 1024);
            server.SupportsRanges = ranged;
            server.ContentEncoding = "gzip";
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 8);

            Assert.Equal(SegmentedDownloadOutcome.NotSupported, run.Result.Outcome);
            Assert.Contains("compressed", run.Result.Reason);
            Assert.Equal(0, server.OpenBodies);
        }

        [Fact]
        public async Task ErrorOnTheFirstRequestIsNotSupported()
        {
            var server = Server(50 * 1024);
            server.Reply = r => r.Index == 1 ? new FakeReply { Status = 404 } : null;
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 8);

            Assert.Equal(SegmentedDownloadOutcome.NotSupported, run.Result.Outcome);
            Assert.Contains("404", run.Result.Reason);
        }

        [Fact]
        public async Task ResponseWithoutAnyValidatorIsNotSplit()
        {
            var server = Server(50 * 1024);
            server.ETag = null;
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 8);

            Assert.Equal(SegmentedDownloadOutcome.NotSupported, run.Result.Outcome);
            Assert.Equal(0, server.OpenBodies);
        }

        [Fact]
        public async Task LastModifiedAloneIsEnoughToSplit()
        {
            var server = Server(50 * 1024);
            server.ETag = null;
            server.LastModified = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            var downloader = Downloader(server, 16);

            AssertCompleted(await Run(downloader, server, 4), server);
        }

        [Theory]
        [InlineData(CutKind.Premature)]
        [InlineData(CutKind.Reset)]
        [InlineData(CutKind.CleanEnd)]
        public async Task BrokenRangeResumesWhereItStopped(CutKind cut)
        {
            var server = Server(64 * 1024);
            server.BytesPerRead = 1000;
            var cutOnce = 0;
            server.Reply = r => r.From == 16 * 1024 && Interlocked.Exchange(ref cutOnce, 1) == 0
                                    ? new FakeReply { CutAfter = 3000, Cut = cut }
                                    : null;

            // No splits: a thief shortening the range could let it finish before the cut and hide the resume.
            var downloader = Downloader(server, 16, FastOptions with { MinSplitBytes = 1024 * 1024 });

            var run = await Run(downloader, server, 2);

            AssertCompleted(run, server);
            Assert.True(run.Result.Stats.Retries >= 1);
            Assert.Contains(server.Requests, r => r.From == (16 * 1024) + 3000);
        }

        [Fact]
        public async Task StalledRangeIsRetriedAfterTheIdleTimeout()
        {
            var server = Server(64 * 1024);
            var stallOnce = 0;
            server.Reply = r => r.From == 16 * 1024 && Interlocked.Exchange(ref stallOnce, 1) == 0
                                    ? new FakeReply { CutAfter = 0, Cut = CutKind.Stall }
                                    : null;
            var downloader = Downloader(server, 16, FastOptions with { IdleTimeout = TimeSpan.FromMilliseconds(300) });

            var run = await Run(downloader, server, 2);

            AssertCompleted(run, server);
            Assert.True(run.Result.Stats.Retries >= 1);
        }

        [Fact]
        public async Task ExpiredLinkIsReplacedOnceForAllConnections()
        {
            var server = Server(128 * 1024);
            server.BytesPerRead = 1024;
            server.ReadDelay = TimeSpan.FromMilliseconds(1);
            var expired = 0;
            server.Reply = r =>
            {
                if (!r.ToSource && Interlocked.Exchange(ref expired, 1) == 0)
                    server.ExpireLinks();
                return null;
            };
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 4);

            AssertCompleted(run, server);
            Assert.Equal(1, run.Result.Stats.LinkRefreshes);
            Assert.Equal(2, server.RequestsTo(source: true).Length);
            Assert.Contains(server.Requests, r => !r.ToSource && r.LinkVersion == 2);
        }

        [Fact]
        public async Task LinkThatKeepsBeingRejectedGivesUp()
        {
            // The first range is one read, so no data arrives anywhere once the rejections start.
            var server = Server(64 * 1024);
            server.Reply = r => r.ToSource ? null : new FakeReply { Status = 403, HeaderDelay = TimeSpan.FromMilliseconds(50) };
            var downloader = Downloader(server, 16, FastOptions with { FirstRangeBytes = 1024 });

            var run = await Run(downloader, server, 2);

            Assert.Equal(SegmentedDownloadOutcome.GaveUp, run.Result.Outcome);
            Assert.Contains("rejected", run.Result.Reason);
            Assert.Equal(1 + FastOptions.MaxRefreshesWithoutProgress, server.RequestsTo(source: true).Length);
            AssertClean(downloader, server, 16);
        }

        [Fact]
        public async Task FileChangingMidwayGivesUp()
        {
            var server = Server(64 * 1024);
            server.Reply = r => r.ToSource ? null : new FakeReply { ETag = "\"another\"" };
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 4);

            Assert.Equal(SegmentedDownloadOutcome.GaveUp, run.Result.Outcome);
            Assert.Contains("changed", run.Result.Reason);
            AssertClean(downloader, server, 16);
        }

        [Fact]
        public async Task ServerDroppingRangesMidwayGivesUp()
        {
            var server = Server(64 * 1024);
            server.Reply = r => r.ToSource ? null : new FakeReply { Whole = true };
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 4);

            Assert.Equal(SegmentedDownloadOutcome.GaveUp, run.Result.Outcome);
            Assert.Contains("range", run.Result.Reason);
            AssertClean(downloader, server, 16);
        }

        [Fact]
        public async Task RangeThatKeepsFailingGivesUpAfterMaxAttempts()
        {
            var server = Server(64 * 1024);
            server.Reply = r => r.From == 16 * 1024 ? new FakeReply { CutAfter = 0, Cut = CutKind.Reset } : null;
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 2);

            Assert.Equal(SegmentedDownloadOutcome.GaveUp, run.Result.Outcome);
            Assert.Contains($"{FastOptions.MaxAttempts} times", run.Result.Reason);
            Assert.Equal(FastOptions.MaxAttempts, server.Requests.Count(r => r.From == 16 * 1024));
            AssertClean(downloader, server, 16);
        }

        [Fact]
        public async Task ShorterRangesThanAskedForAreContinued()
        {
            var server = Server(64 * 1024);
            server.Reply = r => r.ToSource ? null : new FakeReply { ShortenTo = r.From + 2999 };
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 3);

            AssertCompleted(run, server);
            Assert.Equal(0, run.Result.Stats.Retries);
        }

        [Fact]
        public async Task SmallFileTakesOneRequest()
        {
            var server = Server(1500);
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 8);

            AssertCompleted(run, server);
            Assert.Single(server.Requests);
            Assert.Equal(1, run.Result.Stats.Connections);
            Assert.Equal(1, run.Result.Stats.Requests);
        }

        [Fact]
        public async Task FileInsideTheFirstRangeIsStillSplitWhenSlow()
        {
            var server = Server(6000);
            server.BytesPerRead = 256;
            server.ReadDelay = TimeSpan.FromMilliseconds(5);
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 4);

            AssertCompleted(run, server);
            Assert.True(run.Result.Stats.Splits >= 1);
            Assert.True(run.Result.Stats.Connections >= 2);
        }

        [Fact]
        public async Task CancellationStopsEveryConnection()
        {
            var server = Server(256 * 1024);
            server.BytesPerRead = 256;
            server.ReadDelay = TimeSpan.FromMilliseconds(10);
            var downloader = Downloader(server, 16);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(downloader, server, 8, cts.Token));

            AssertClean(downloader, server, 16);
        }

        [Fact]
        public async Task SharedPoolCapsConnectionsAcrossDownloads()
        {
            var counter = new ConcurrencyCounter();
            var a = Server(128 * 1024, "A");
            var b = Server(128 * 1024, "B");
            foreach (var server in new[] { a, b })
            {
                server.BytesPerRead = 1024;
                server.ReadDelay = TimeSpan.FromMilliseconds(2);
                server.SharedCounter = counter;
            }

            var client = new HttpClient(new Router(a, b)) { Timeout = Timeout.InfiniteTimeSpan };
            var downloader = new SegmentedDownloader(client, 2, FastOptions);

            var runs = await Task.WhenAll(Run(downloader, a, 4), Run(downloader, b, 4));

            AssertCompleted(runs[0], a);
            AssertCompleted(runs[1], b);
            Assert.InRange(counter.Max, 3, 2 + 2);
            Assert.Equal(2, downloader.AvailableExtraConnections);
        }

        [Fact]
        public async Task PerDownloadConnectionLimitHolds()
        {
            var server = Server(128 * 1024);
            server.BytesPerRead = 1024;
            server.ReadDelay = TimeSpan.FromMilliseconds(1);
            var downloader = Downloader(server, 16);

            var run = await Run(downloader, server, 3);

            AssertCompleted(run, server);
            Assert.InRange(server.MaxOpenBodies, 2, 3);
            Assert.InRange(run.Result.Stats.Connections, 2, 3);
        }

        [Fact]
        public async Task GitHubDigestIsReadFromTheAssetMetadata()
        {
            var server = Server(1000);
            var digest = SHA256.HashData(server.Content);
            server.MetadataJson = $"{{\"id\":1,\"digest\":\"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}\"}}";
            var downloader = Downloader(server, 16);

            Assert.Equal(digest, await downloader.TryGetGitHubAssetSha256Async(server.SourceUrl, default));
            Assert.Contains(server.Requests, r => r.Metadata && r.Accept.Contains("application/vnd.github+json"));

            server.MetadataJson = "{\"id\":1,\"digest\":null}";
            Assert.Null(await downloader.TryGetGitHubAssetSha256Async(server.SourceUrl, default));

            server.MetadataJson = "{\"digest\":\"sha1:0000\"}";
            Assert.Null(await downloader.TryGetGitHubAssetSha256Async(server.SourceUrl, default));

            server.MetadataJson = "not json";
            Assert.Null(await downloader.TryGetGitHubAssetSha256Async(server.SourceUrl, default));

            server.MetadataJson = null;
            Assert.Null(await downloader.TryGetGitHubAssetSha256Async(server.SourceUrl, default));

            var before = server.Requests.Count;
            Assert.Null(await downloader.TryGetGitHubAssetSha256Async(new Uri("https://example.com/plugin.zip"), default));
            Assert.Null(await downloader.TryGetGitHubAssetSha256Async(
                            new Uri("https://api.github.com/repos/o/r/releases/assets/12a"), default));
            Assert.Equal(before, server.Requests.Count);
        }

        internal static FakeRangeServer Server(int size, string name = "Test")
        {
            var content = new byte[size];
            new Random(size ^ name.GetHashCode()).NextBytes(content);
            return new FakeRangeServer(content, name);
        }

        internal static SegmentedDownloader Downloader(FakeRangeServer server, int pool, SegmentedDownloadOptions? options = null)
            => new(new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan }, pool, options ?? FastOptions);

        private static async Task<RunResult> Run(
            SegmentedDownloader downloader, FakeRangeServer server, int connections, CancellationToken cancellationToken = default)
        {
            long total = -1, reported = 0;
            var totalCalls = 0;
            var inCallback = 0;
            var overlapped = false;
            var result = await downloader.DownloadAsync(
                             server.SourceUrl,
                             connections,
                             r => r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream")),
                             t =>
                             {
                                 total = t;
                                 totalCalls++;
                             },
                             delta =>
                             {
                                 if (Interlocked.Increment(ref inCallback) != 1)
                                     overlapped = true;
                                 Thread.SpinWait(200);
                                 reported += delta;
                                 Interlocked.Decrement(ref inCallback);
                             },
                             cancellationToken);

            Assert.False(overlapped, "Progress was reported from two threads at once.");
            Assert.True(totalCalls <= 1);
            return new RunResult(result, total, reported);
        }

        private static void AssertCompleted(RunResult run, FakeRangeServer server)
        {
            Assert.True(
                run.Result.Outcome == SegmentedDownloadOutcome.Completed,
                $"{run.Result.Outcome}: {run.Result.Reason} {run.Result.Exception}");
            Assert.Equal(server.Content.LongLength, run.Total);
            Assert.Equal(server.Content.LongLength, run.Reported);
            Assert.Equal(server.Content.LongLength, run.Result.Stats.BytesReceived);
            Assert.True(server.Content.AsSpan().SequenceEqual(run.Result.Data), "The assembled file differs from the original.");
            Assert.Equal(0, server.OpenBodies);
        }

        private static void AssertClean(SegmentedDownloader downloader, FakeRangeServer server, int pool)
        {
            Assert.Equal(0, server.OpenBodies);
            Assert.Equal(pool, downloader.AvailableExtraConnections);
        }

        private sealed record RunResult(SegmentedDownloadResult Result, long Total, long Reported);

        // Sends each request to the server that owns its URL, so that two fake files share one client.
        private sealed class Router : HttpMessageHandler
        {
            private readonly FakeRangeServer[] servers;
            private readonly HttpMessageInvoker[] invokers;

            public Router(params FakeRangeServer[] servers)
            {
                this.servers = servers;
                this.invokers = servers.Select(s => new HttpMessageInvoker(s, false)).ToArray();
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                for (var i = 0; i < this.servers.Length; i++)
                {
                    var server = this.servers[i];
                    if (request.RequestUri == server.SourceUrl ||
                        (request.RequestUri!.Host == "storage.example" && request.RequestUri.AbsolutePath == "/" + server.Name))
                        return this.invokers[i].SendAsync(request, cancellationToken);
                }

                throw new InvalidOperationException("No fake server for this request.");
            }
        }
    }
}
