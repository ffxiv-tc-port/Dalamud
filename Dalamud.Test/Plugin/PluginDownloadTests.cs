#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;
using Dalamud.Networking.Http;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Internal.Types.Manifest;
using Dalamud.Test.Networking;
using Newtonsoft.Json;

using Xunit;

namespace Dalamud.Test.Plugin
{
    // PluginManager and HappyHttpClient are created without running their constructors, which need the service
    // container; only the fields the download path reads are filled in.
    public class PluginDownloadTests
    {
        private static readonly PluginManager.DownloadLimits Limits =
            new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), 16 * 1024);

        [Fact]
        public async Task SegmentedDownloadReturnsTheFileAndCountsItOnce()
        {
            var segmented = Slow(SegmentedDownloaderTests.Server(96 * 1024));
            var plain = new FakeRangeServer(segmented.Content);
            var harness = new Harness(segmented, plain, 4);

            var bytes = await harness.DownloadAsync(Limits);

            Assert.Equal(segmented.Content, bytes);
            Assert.Empty(plain.Requests);
            Assert.True(segmented.MaxOpenBodies >= 2);
            harness.AssertProgressNeverOvershot(segmented.Content.Length);
        }

        [Fact]
        public async Task FileChangingMidwayFallsBackToOneConnection()
        {
            var segmented = Slow(SegmentedDownloaderTests.Server(96 * 1024));
            // The change shows up half way, so the plain download starts over with bytes already counted.
            segmented.Reply = r => r.From >= 48 * 1024
                                       ? new FakeReply { ETag = "\"another\"", HeaderDelay = TimeSpan.FromMilliseconds(30) }
                                       : null;
            var plain = Slow(new FakeRangeServer(segmented.Content));
            var harness = new Harness(segmented, plain, 4);

            var bytes = await harness.DownloadAsync(Limits);

            Assert.Equal(segmented.Content, bytes);
            var request = Assert.Single(plain.Requests);
            Assert.Null(request.From);
            harness.AssertProgressNeverOvershot(segmented.Content.Length);
        }

        [Fact]
        public async Task ServerWithoutValidatorFallsBackToOneConnection()
        {
            var segmented = SegmentedDownloaderTests.Server(40 * 1024);
            segmented.ETag = null;
            var plain = new FakeRangeServer(segmented.Content);
            var harness = new Harness(segmented, plain, 8);

            Assert.Equal(segmented.Content, await harness.DownloadAsync(Limits));
            Assert.Single(plain.Requests);
            Assert.Single(segmented.RequestsTo(source: true));
        }

        [Fact]
        public async Task OneConnectionKeepsThePlainRequest()
        {
            var segmented = SegmentedDownloaderTests.Server(40 * 1024);
            var plain = new FakeRangeServer(segmented.Content);
            var harness = new Harness(segmented, plain, 1);

            Assert.Equal(segmented.Content, await harness.DownloadAsync(Limits));
            Assert.Empty(segmented.Requests);
            var request = Assert.Single(plain.Requests);
            Assert.Null(request.From);
            Assert.Contains("application/zip", request.Accept);
            Assert.Contains("application/octet-stream", request.Accept);
        }

        [Fact]
        public async Task WholeFileResponseIsReadWithoutAnotherRequest()
        {
            var segmented = SegmentedDownloaderTests.Server(40 * 1024);
            segmented.SupportsRanges = false;
            var plain = new FakeRangeServer(segmented.Content);
            var harness = new Harness(segmented, plain, 8);

            Assert.Equal(segmented.Content, await harness.DownloadAsync(Limits));
            Assert.Single(segmented.RequestsTo(source: true));
            Assert.Empty(plain.Requests);
        }

        [Fact]
        public async Task NoProgressOnAnyConnectionIsTheUsualStallTimeout()
        {
            var segmented = SegmentedDownloaderTests.Server(64 * 1024);
            segmented.Reply = r => r.From >= 8 * 1024 ? new FakeReply { CutAfter = 0, Cut = CutKind.Stall } : null;
            var plain = new FakeRangeServer(segmented.Content) { Reply = _ => new FakeReply { CutAfter = 0, Cut = CutKind.Stall } };
            var options = SegmentedDownloaderTests.FastOptions with
            {
                IdleTimeout = TimeSpan.FromMilliseconds(300),
                MaxAttempts = 1000,
            };
            var harness = new Harness(segmented, plain, 4, options);

            var ex = await Assert.ThrowsAsync<TimeoutException>(
                         () => harness.DownloadAsync(new PluginManager.DownloadLimits(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1), 16 * 1024)));

            Assert.Equal("Download of Test stalled for 1 seconds after 8192 bytes.", ex.Message);
            Assert.Empty(plain.Requests);
            Assert.Equal(0, segmented.OpenBodies);
        }

        [Fact]
        public async Task MatchingGitHubDigestKeepsTheSegmentedResult()
        {
            var segmented = Slow(SegmentedDownloaderTests.Server(64 * 1024));
            segmented.MetadataJson = Metadata(SHA256.HashData(segmented.Content));
            var plain = new FakeRangeServer(segmented.Content);
            var harness = new Harness(segmented, plain, 4);

            Assert.Equal(segmented.Content, await harness.DownloadAsync(Limits));
            Assert.Empty(plain.Requests);
            Assert.Contains(segmented.Requests, r => r.Metadata);
        }

        [Fact]
        public async Task WrongGitHubDigestFallsBackToOneConnection()
        {
            var segmented = Slow(SegmentedDownloaderTests.Server(64 * 1024));
            segmented.MetadataJson = Metadata(new byte[32]);
            var plain = Slow(new FakeRangeServer(segmented.Content));
            var harness = new Harness(segmented, plain, 4);

            Assert.Equal(segmented.Content, await harness.DownloadAsync(Limits));
            Assert.Single(plain.Requests);
            harness.AssertProgressNeverOvershot(segmented.Content.Length);
        }

        [Fact]
        public void ConnectionSettingDefaultsToEightForOldConfigurationFiles()
        {
            Assert.Equal(8, new DalamudConfiguration().PluginDownloadConnections);
            Assert.Equal(8, JsonConvert.DeserializeObject<DalamudConfiguration>("{}")!.PluginDownloadConnections);
            Assert.Equal(1, JsonConvert.DeserializeObject<DalamudConfiguration>("{\"PluginDownloadConnections\":1}")!.PluginDownloadConnections);
        }

        private static FakeRangeServer Slow(FakeRangeServer server)
        {
            server.BytesPerRead = 1024;
            server.ReadDelay = TimeSpan.FromMilliseconds(1);
            return server;
        }

        private static string Metadata(byte[] sha256)
            => $"{{\"id\":1,\"digest\":\"sha256:{Convert.ToHexString(sha256).ToLowerInvariant()}\"}}";

        private sealed class Harness
        {
            private readonly PluginManager manager;
            private readonly RemotePluginManifest manifest;
            private readonly ConcurrentQueue<(long Received, long Total)> samples = new();
            private PluginDownloadProgress? progress;

            public Harness(FakeRangeServer segmented, FakeRangeServer plain, int connections, SegmentedDownloadOptions? options = null)
            {
                var configuration = (DalamudConfiguration)RuntimeHelpers.GetUninitializedObject(typeof(DalamudConfiguration));
                configuration.PluginDownloadConnections = connections;

                var http = (HappyHttpClient)RuntimeHelpers.GetUninitializedObject(typeof(HappyHttpClient));
                SetField(http, "<SharedLargeFileHttpClient>k__BackingField", new HttpClient(plain) { Timeout = Timeout.InfiniteTimeSpan });
                SetField(
                    http,
                    "<PluginDownloader>k__BackingField",
                    new SegmentedDownloader(
                        new HttpClient(segmented) { Timeout = Timeout.InfiniteTimeSpan },
                        16,
                        options ?? SegmentedDownloaderTests.FastOptions));

                this.manager = (PluginManager)RuntimeHelpers.GetUninitializedObject(typeof(PluginManager));
                SetField(this.manager, "configuration", configuration);
                SetField(this.manager, "happyHttpClient", http);
                SetField(this.manager, "activeDownloadsLock", new object());
                SetField(this.manager, "activeDownloads", Array.Empty<PluginDownloadProgress>());

                this.manifest = new RemotePluginManifest
                {
                    InternalName = "Test",
                    Name = "Test",
                    DownloadLinkInstall = segmented.SourceUrl.ToString(),
                };
            }

            public async Task<byte[]> DownloadAsync(PluginManager.DownloadLimits limits)
            {
                using var stop = new CancellationTokenSource();
                var sampler = Task.Factory.StartNew(
                    () =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            foreach (var download in this.manager.ActiveDownloads)
                            {
                                this.progress ??= download;
                                this.samples.Enqueue((download.BytesReceived, download.TotalBytes));
                            }

                            Thread.Yield();
                        }
                    },
                    TaskCreationOptions.LongRunning);

                try
                {
                    var method = typeof(PluginManager).GetMethod("DownloadPluginAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var stream = await (Task<Stream>)method.Invoke(this.manager, [this.manifest, false, limits])!;
                    await using (stream)
                    {
                        using var copy = new MemoryStream();
                        await stream.CopyToAsync(copy);
                        return copy.ToArray();
                    }
                }
                finally
                {
                    stop.Cancel();
                    await sampler;
                    Assert.Empty(this.manager.ActiveDownloads);
                }
            }

            // The final count is read from the progress object itself: a restart that was not reset shows up there.
            public void AssertProgressNeverOvershot(long size)
            {
                Assert.NotNull(this.progress);
                Assert.Equal(size, this.progress!.TotalBytes);
                Assert.Equal(size, this.progress.BytesReceived);
                Assert.All(this.samples, s => Assert.InRange(s.Received, 0, s.Total > 0 ? s.Total : size));
            }

            private static void SetField(object target, string name, object value)
                => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
        }
    }
}
