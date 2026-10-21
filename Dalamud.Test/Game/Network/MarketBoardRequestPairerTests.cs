#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;

using Dalamud.Game.Network.Internal.MarketBoardUploaders;
using Dalamud.Game.Network.Structures;

using Xunit;

namespace Dalamud.Test.Game.Network
{
    // Packets are synthesized byte for byte and parsed by the production Read methods. Every scenario is fed to the
    // new pairer and to a copy of the pairing pipeline it replaced, so each test shows what the old one uploaded.
    public class MarketBoardRequestPairerTests
    {
        private const uint Refused = 0x70000002;
        private const uint A = 5731;
        private const uint B = 5736;
        private const uint C = 5751;

        [Fact]
        public void NormalQueryIsUploadedOnceByBoth()
        {
            var f = new Feed();
            f.Start(0, 15);
            f.History(A, searching: A);
            f.Page(A, 10);
            f.Page(A, 5);

            var got = Assert.Single(f.New);
            Assert.Equal(A, got.CatalogId);
            Assert.Equal(15, got.Listings.Count);
            Assert.All(got.Listings, l => Assert.Equal(A, l.CatalogId));
            Assert.Equal(2, got.Sales.Count);

            var old = Assert.Single(f.LegacyUploads);
            Assert.Equal(A, old.CatalogId);
            Assert.Empty(f.Lines);
        }

        [Fact]
        public void MissingHistoryOldLabelsListingsWithTheNextItem()
        {
            var f = new Feed();
            f.Start(0, 5);
            f.Page(A, 5);
            f.Start(0, 3);
            f.Page(B, 3);
            f.History(B, searching: B);

            Assert.Contains(f.LegacyUploads, u => u.CatalogId == B && u.Listings.All(l => l.CatalogId == A));

            var got = Assert.Single(f.New);
            Assert.Equal(B, got.CatalogId);
            Assert.All(got.Listings, l => Assert.Equal(B, l.CatalogId));
            Assert.Contains(f.Lines, l => l.Contains("reason=Superseded item=5731"));
        }

        [Fact]
        public void MissingPageOldMixesTheNextItemIntoTheListings()
        {
            var f = new Feed();
            f.Start(0, 20);
            f.History(A, searching: A);
            f.Page(A, 10);
            f.Start(0, 20);
            f.History(B, searching: B);
            f.Page(B, 10);
            f.Page(B, 10);

            Assert.Contains(
                f.LegacyUploads,
                u => u.CatalogId == A && u.Listings.Any(l => l.CatalogId == B) && u.Listings.Count == 20);

            var got = Assert.Single(f.New);
            Assert.Equal(B, got.CatalogId);
            Assert.All(got.Listings, l => Assert.Equal(B, l.CatalogId));
        }

        [Fact]
        public void RefusedQueryLateHistoryOldLabelsTheNextQueryWithIt()
        {
            var f = new Feed();
            f.Start(Refused, 0);
            f.Start(0, 3);
            f.History(B, searching: C);
            f.Page(C, 3);
            f.History(C, searching: C);

            Assert.Contains(f.LegacyUploads, u => u.CatalogId == B && u.Listings.All(l => l.CatalogId == C));

            var got = Assert.Single(f.New);
            Assert.Equal(C, got.CatalogId);
            Assert.Contains(f.Lines, l => l.Contains("reason=HistoryNotSearchedItem item=5736"));
        }

        [Fact]
        public void RefusedStartLetsTheUnfinishedQueryFinish()
        {
            var f = new Feed();
            f.Start(0, 20);
            f.History(A, searching: A);
            f.Page(A, 10);
            f.Start(Refused, 0);
            f.Page(A, 10);

            var got = Assert.Single(f.New);
            Assert.Equal(A, got.CatalogId);
            Assert.Equal(20, got.Listings.Count);
            Assert.All(got.Listings, l => Assert.Equal(A, l.CatalogId));
            Assert.Contains(f.LegacyUploads, u => u.CatalogId == A && u.Listings.Count == 20);
            Assert.Contains(f.Lines, l => l.Contains("refused keep item=5731") && l.Contains("pages=1/2"));
            Assert.DoesNotContain(f.Lines, l => l.Contains("reason="));
            Assert.Contains("refusedKept=1", f.Pairer.BuildSummary());
        }

        [Fact]
        public void AcceptedStartAfterARefusalDropsTheHalfQuery()
        {
            var f = new Feed();
            f.Start(0, 20);
            f.History(A, searching: A);
            f.Page(A, 10);
            var seqA = f.CurrentSeq;
            f.Start(Refused, 0);
            f.Start(0, 3);
            f.Page(A, 10, seqA);
            f.History(C, searching: C);
            f.Page(C, 3);

            var got = Assert.Single(f.New);
            Assert.Equal(C, got.CatalogId);
            Assert.Equal(3, got.Listings.Count);
            Assert.All(got.Listings, l => Assert.Equal(C, l.CatalogId));
            Assert.Contains(f.Lines, l => l.Contains("reason=Superseded item=5731") && l.Contains("next=accepted"));
            Assert.Contains(f.Lines, l => l.Contains("reason=StalePage item=5731"));
        }

        [Fact]
        public void LateHistoryAfterARefusalIsIgnoredWhenNotTheSearchedItem()
        {
            var f = new Feed();
            f.Start(0, 20);
            f.History(A, searching: A);
            f.Page(A, 10);
            f.Start(Refused, 0);
            f.History(B, searching: A);
            f.Page(A, 10);

            var got = Assert.Single(f.New);
            Assert.Equal(A, got.CatalogId);
            Assert.Equal(20, got.Listings.Count);
            Assert.All(got.Listings, l => Assert.Equal(A, l.CatalogId));
            Assert.Contains(f.Lines, l => l.Contains("reason=HistoryNotSearchedItem item=5736"));
        }

        [Fact]
        public void HistoryOfTheRefusedItemNeverJoinsTheKeptQuery()
        {
            // With history: a conflicting second history ends the query.
            var f = new Feed();
            f.Start(0, 20);
            f.History(A, searching: A);
            f.Page(A, 10);
            f.Start(Refused, 0);
            f.History(B, searching: B);
            f.Page(A, 10);
            Assert.Empty(f.New);
            Assert.Contains(f.Lines, l => l.Contains("reason=HistoryConflict item=5736"));

            // No history yet, one page in: the listings already name A.
            f = new Feed();
            f.Start(0, 20);
            f.Page(A, 10);
            f.Start(Refused, 0);
            f.History(B, searching: B);
            f.Page(A, 10);
            Assert.Empty(f.New);
            Assert.Contains(f.Lines, l => l.Contains("reason=ListingItemMismatch"));

            // No history, no page yet: the pages that follow name A.
            f = new Feed();
            f.Start(0, 20);
            f.Start(Refused, 0);
            f.History(B, searching: B);
            f.Page(A, 10);
            f.Page(A, 10);
            Assert.Empty(f.New);
            Assert.Contains(f.Lines, l => l.Contains("reason=ListingItemMismatch"));
        }

        [Fact]
        public void RefusalStillEndsAnEmptyQueryThatHasNoHistoryYet()
        {
            // Nothing but a later history could name this query's item, and that history may be the refused one's.
            var f = new Feed();
            f.Start(0, 0);
            f.Start(Refused, 0);
            f.History(B, searching: B);

            Assert.Empty(f.New);
            Assert.Contains(f.Lines, l => l.Contains("reason=Superseded item=none") && l.Contains("next=refused"));
            Assert.Contains(f.Lines, l => l.Contains("reason=StaleHistory item=5736"));
        }

        [Fact]
        public void HistoryOfAnEndedQueryNeverEmptiesTheNextOne()
        {
            // B, then A without listings, then B again: B's first history comes after A's start.
            var f = new Feed();
            f.Start(0, 10);
            f.Page(B, 10);
            f.Start(0, 0);
            f.History(B, searching: B);
            f.Start(0, 10);
            f.History(A, searching: B);
            f.Page(B, 10);
            f.History(B, searching: B);

            var got = Assert.Single(f.New);
            Assert.Equal(B, got.CatalogId);
            Assert.Equal(10, got.Listings.Count);
            Assert.DoesNotContain(f.New, q => q.Listings.Count == 0);
            Assert.Contains(f.Lines, l => l.Contains("reason=StaleHistory item=5736"));
            Assert.Contains("historiesInFlight=0", f.Pairer.BuildSummary());
        }

        [Fact]
        public void EmptyQueryAfterACompleteOneUsesTheNextHistory()
        {
            // Negative control for the case above: B got its history, so nothing is in flight.
            var f = new Feed();
            f.Start(0, 10);
            f.Page(B, 10);
            f.History(B, searching: B);
            f.Start(0, 0);
            f.History(A, searching: A);

            Assert.Equal(2, f.New.Count);
            Assert.Equal(A, f.New[1].CatalogId);
            Assert.Empty(f.New[1].Listings);
            Assert.DoesNotContain(f.Lines, l => l.Contains("reason="));
        }

        [Fact]
        public void RefusedStartOwesNoHistory()
        {
            // Live: 44 refusals with no query open, and no history ever arrived for one.
            var f = new Feed();
            f.Start(Refused, 0);
            f.Start(0, 0);
            f.History(A, searching: A);

            var got = Assert.Single(f.New);
            Assert.Equal(A, got.CatalogId);
            Assert.Empty(got.Listings);
        }

        [Fact]
        public void ALostHistoryOnlyCostsQueriesWithoutListings()
        {
            // B's history never comes. The next history is taken as B's: a query with listings still checks it
            // against its pages and completes; one without listings gives it up.
            var f = new Feed();
            f.Start(0, 10);
            f.Page(B, 10);
            f.Start(0, 5);
            f.Page(C, 5);
            f.History(C, searching: C);
            Assert.Equal(C, Assert.Single(f.New).CatalogId);

            f.Start(0, 3);
            f.Page(A, 3);
            f.History(A, searching: A);
            Assert.Equal(2, f.New.Count);

            f = new Feed();
            f.Start(0, 10);
            f.Page(B, 10);
            f.Start(0, 0);
            f.History(A, searching: A);
            f.Start(0, 3);
            f.Page(C, 3);
            f.History(C, searching: C);
            f.Start(0, 0);
            f.History(A, searching: A);

            Assert.Equal(new[] { C, A }, f.New.Select(q => q.CatalogId));
            Assert.Contains(f.Lines, l => l.Contains("reason=StaleHistory item=5731"));
        }

        [Fact]
        public void InterleavedStalePageOldMixesItIntoTheNextQuery()
        {
            var f = new Feed();
            f.Start(0, 12);
            f.History(A, searching: A);
            f.Page(A, 10);
            var seqA = f.CurrentSeq;
            f.Start(0, 12);
            f.Page(A, 2, seqA);
            f.Page(B, 10);
            f.History(B, searching: B);
            f.Page(B, 2);

            Assert.Contains(
                f.LegacyUploads,
                u => u.CatalogId == B && u.Listings.Any(l => l.CatalogId == A) && u.Listings.Count == 12);

            var got = Assert.Single(f.New);
            Assert.Equal(B, got.CatalogId);
            Assert.Equal(12, got.Listings.Count);
            Assert.All(got.Listings, l => Assert.Equal(B, l.CatalogId));
            Assert.Contains(f.Lines, l => l.Contains("reason=StalePage item=5731"));
        }

        [Fact]
        public void ListingsOfAnotherItemThanTheHistoryAreDropped()
        {
            var f = new Feed();
            f.Start(0, 3);
            f.Page(B, 3);
            f.History(A, searching: A);

            Assert.Empty(f.New);
            Assert.Contains(f.Lines, l => l.Contains("reason=ListingItemMismatch"));
        }

        [Fact]
        public void EmptyQueryNeedsTheSearchedItemsHistory()
        {
            var f = new Feed();
            f.Start(0, 0);
            f.History(0, searching: B);
            Assert.Empty(f.New);

            f.History(B, searching: B);
            var got = Assert.Single(f.New);
            Assert.Equal(B, got.CatalogId);
            Assert.Empty(got.Listings);
        }

        [Fact]
        public void EmptyQueryEndsOnAHistoryOfAnotherItem()
        {
            // Its own history named A while the client was already on B; B's history must not label it.
            var f = new Feed();
            f.Start(0, 0);
            f.History(A, searching: B);
            f.History(B, searching: B);

            Assert.Empty(f.New);
            Assert.Contains(f.Lines, l => l.Contains("reason=HistoryNotSearchedItem item=5731"));
            Assert.Contains(f.Lines, l => l.Contains("reason=HistoryWithoutQuery item=5736"));
        }

        [Fact]
        public void ExtraPageDropsTheQuery()
        {
            var f = new Feed();
            f.Start(0, 5);
            f.Page(A, 5);
            f.Page(A, 5);
            f.History(A, searching: A);

            Assert.Empty(f.New);
            Assert.Contains(f.Lines, l => l.Contains("reason=ExtraPage"));
        }

        [Fact]
        public void UnansweredPurchaseNoLongerSilencesTheRest()
        {
            var f = new Feed();
            f.PurchaseSent(A, 1, listingId: 1);
            f.PurchaseSent(B, 2, listingId: 2);
            f.PurchaseResult(B, 0, 2);
            f.PurchaseSent(C, 1, listingId: 3);
            f.PurchaseResult(C, 0, 1);

            Assert.Empty(f.LegacyPurchases);
            Assert.Equal(new ulong[] { 2, 3 }, f.NewPurchases.Select(p => p.ListingId));
            Assert.Contains(f.Lines, l => l.Contains("reason=PurchaseUnanswered item=5731"));
        }

        [Fact]
        public void FailedOrMismatchedPurchaseIsNotReported()
        {
            var f = new Feed();
            f.PurchaseSent(A, 1, listingId: 1);
            f.PurchaseResult(A, 0x1CC, 1);
            f.PurchaseSent(A, 1, listingId: 2);
            f.PurchaseResult(A + 1_000_000, 0, 1);
            f.PurchaseSent(B, 2, listingId: 3);
            f.PurchaseResult(B, 0, 1);

            Assert.Contains(f.LegacyPurchases, p => p.ListingId == 1);
            Assert.Equal(new ulong[] { 2 }, f.NewPurchases.Select(p => p.ListingId));
            Assert.Contains(f.Lines, l => l.Contains("reason=PurchaseMismatch"));
            Assert.Single(f.Lines, l => l.Contains("purchase failed status=0x1CC item=5731 qty=1"));
            Assert.Contains("failedPurchases=1", f.Pairer.BuildSummary());
        }

        [Fact]
        public void SuccessfulPurchasesLogNoFailure()
        {
            var f = new Feed();
            f.PurchaseSent(A, 1, listingId: 1);
            f.PurchaseResult(A, 0, 1);

            Assert.Single(f.NewPurchases);
            Assert.DoesNotContain(f.Lines, l => l.Contains("purchase failed"));
            Assert.Contains("failedPurchases=0", f.Pairer.BuildSummary());
        }

        [Fact]
        public void SameReasonIsWrittenOncePerWindowWithTheSuppressedCount()
        {
            var f = new Feed();
            for (var i = 0; i < 3; i++)
                f.Page(A, 1);
            Assert.Single(f.Lines);

            f.Clock += MarketBoardRequestPairer.DropLogWindowMs;
            f.Page(A, 1);
            Assert.Equal(2, f.Lines.Count);
            Assert.Contains("suppressed=2 total=4", f.Lines[1]);
            Assert.Contains("PageWithoutQuery:4", f.Pairer.BuildSummary());
        }

        private static T Parse<T>(byte[] bytes, Func<nint, T> read)
        {
            var block = Marshal.AllocHGlobal(1544);
            try
            {
                Marshal.Copy(new byte[1544], 0, block, 1544);
                Marshal.Copy(bytes, 0, block, bytes.Length);
                return read(block);
            }
            finally
            {
                Marshal.FreeHGlobal(block);
            }
        }

        private sealed class Feed
        {
            private readonly Subject<MarketBoardItemRequest> starts = new();
            private readonly Subject<MarketBoardHistory> histories = new();
            private readonly Subject<MarketBoardCurrentOfferings> pages = new();
            private readonly Subject<MarketBoardPurchaseHandler> sent = new();
            private readonly Subject<MarketBoardPurchase> results = new();
            private byte nextSeq = 1;
            private ulong listingId = 100;

            public Feed()
            {
                this.Pairer = new MarketBoardRequestPairer(() => this.Clock, this.Lines.Add);
                LegacyPipeline.Queries(this.starts, this.histories, this.pages)
                              .Where(q => q.Listings.Count == q.Request.AmountToArrive)
                              .Subscribe(q => this.LegacyUploads.Add((q.Sales.CatalogId, q.Listings)));
                LegacyPipeline.Purchases(this.sent, this.results).Subscribe(this.LegacyPurchases.Add);
            }

            public MarketBoardRequestPairer Pairer { get; }

            public long Clock { get; set; } = 1_000_000;

            public List<string> Lines { get; } = [];

            public List<MarketBoardRequestPairer.Completed> New { get; } = [];

            public List<(uint CatalogId, List<MarketBoardCurrentOfferings.MarketBoardItemListing> Listings)> LegacyUploads { get; } = [];

            public List<MarketBoardPurchaseHandler> NewPurchases { get; } = [];

            public List<MarketBoardPurchaseHandler> LegacyPurchases { get; } = [];

            public byte CurrentSeq { get; private set; }

            public void Start(uint status, uint amount)
            {
                // Mirrors the game: only an accepted start with listings opens a new page sequence.
                if (status == 0 && amount > 0)
                    this.CurrentSeq = this.nextSeq++;

                var bytes = new byte[8];
                BitConverter.GetBytes(status).CopyTo(bytes, 0);
                BitConverter.GetBytes(amount).CopyTo(bytes, 4);
                this.starts.OnNext(Parse(bytes, MarketBoardItemRequest.Read));
                this.Pairer.OnStart(Parse(bytes, MarketBoardItemRequest.Read));
            }

            public void History(uint item, uint searching)
            {
                var bytes = new byte[4 + (20 * 48)];
                BitConverter.GetBytes(item).CopyTo(bytes, 0);
                for (var i = 0; i < 2; i++)
                {
                    BitConverter.GetBytes(1000u + (uint)i).CopyTo(bytes, 4 + (i * 48));
                    BitConverter.GetBytes(1u).CopyTo(bytes, 4 + (i * 48) + 8);
                }

                this.histories.OnNext(Parse(bytes, MarketBoardHistory.Read));
                if (this.Pairer.OnHistory(Parse(bytes, MarketBoardHistory.Read), searching) is { } done)
                    this.New.Add(done);
            }

            public void Page(uint item, int count, byte? seq = null)
            {
                var bytes = new byte[0x5A4];
                for (var i = 0; i < count; i++)
                {
                    var at = i * 0x90;
                    BitConverter.GetBytes(this.listingId++).CopyTo(bytes, at);
                    BitConverter.GetBytes(500u).CopyTo(bytes, at + 32);
                    BitConverter.GetBytes(1u).CopyTo(bytes, at + 40);
                    BitConverter.GetBytes(item).CopyTo(bytes, at + 44);
                }

                var pageSeq = seq ?? this.CurrentSeq;
                bytes[0x5A2] = pageSeq;
                this.pages.OnNext(Parse(bytes, MarketBoardCurrentOfferings.Read));
                if (this.Pairer.OnPage(Parse(bytes, MarketBoardCurrentOfferings.Read), pageSeq == this.CurrentSeq) is { } done)
                    this.New.Add(done);
            }

            public void PurchaseSent(uint item, uint quantity, ulong listingId)
            {
                var bytes = new byte[36];
                BitConverter.GetBytes(listingId).CopyTo(bytes, 8);
                BitConverter.GetBytes(item).CopyTo(bytes, 16);
                BitConverter.GetBytes(quantity).CopyTo(bytes, 20);
                this.sent.OnNext(Parse(bytes, MarketBoardPurchaseHandler.Read));
                this.Pairer.OnPurchaseSent(Parse(bytes, MarketBoardPurchaseHandler.Read));
            }

            public void PurchaseResult(uint item, uint status, uint quantity)
            {
                var bytes = new byte[12];
                BitConverter.GetBytes(item).CopyTo(bytes, 0);
                BitConverter.GetBytes(status).CopyTo(bytes, 4);
                BitConverter.GetBytes(quantity).CopyTo(bytes, 8);
                this.results.OnNext(Parse(bytes, MarketBoardPurchase.Read));
                if (this.Pairer.OnPurchaseResult(Parse(bytes, MarketBoardPurchase.Read)) is { } bought)
                    this.NewPurchases.Add(bought);
            }
        }

        // The pairing as it was before MarketBoardRequestPairer, logging removed; keep it byte-for-byte otherwise.
        private static class LegacyPipeline
        {
            public static IObservable<(MarketBoardItemRequest Request, (uint CatalogId, List<MarketBoardHistory.MarketBoardHistoryListing> Sales) Sales, List<MarketBoardCurrentOfferings.MarketBoardItemListing> Listings)> Queries(
                IObservable<MarketBoardItemRequest> starts,
                IObservable<MarketBoardHistory> histories,
                IObservable<MarketBoardCurrentOfferings> offerings)
            {
                var startObservable = starts.Where(request => request.Ok).Publish().RefCount();
                return Observable.When(
                    startObservable
                        .And(SalesBatch(histories, startObservable))
                        .And(ListingsBatch(offerings, startObservable))
                        .Then((request, sales, listings) => (request, sales, listings)));
            }

            public static IObservable<MarketBoardPurchaseHandler> Purchases(
                IObservable<MarketBoardPurchaseHandler> sent,
                IObservable<MarketBoardPurchase> results)
            {
                return sent.Zip(results, (handler, purchase) => (handler, purchase))
                           .Where(d => d.purchase.ItemQuantity == d.handler.ItemQuantity &&
                                       (d.purchase.CatalogId == d.handler.CatalogId || d.purchase.CatalogId == d.handler.CatalogId + 1_000_000))
                           .Select(d => d.handler);
            }

            private static IObservable<List<MarketBoardCurrentOfferings.MarketBoardItemListing>> ListingsBatch(
                IObservable<MarketBoardCurrentOfferings> source,
                IObservable<MarketBoardItemRequest> start)
            {
                var offeringsObservable = source.Publish().RefCount();

                IObservable<MarketBoardCurrentOfferings> UntilBatchEnd(MarketBoardItemRequest request)
                {
                    var totalPackets = Convert.ToInt32(Math.Ceiling((double)request.AmountToArrive / 10));
                    if (totalPackets == 0)
                    {
                        return Observable.Empty<MarketBoardCurrentOfferings>();
                    }

                    return offeringsObservable
                           .Where(offerings => offerings.InternalItemListings.All(l => l.CatalogId != 0))
                           .Skip(totalPackets - 1);
                }

                return offeringsObservable
                       .Window(start, UntilBatchEnd)
                       .SelectMany(
                           o => o.Aggregate(
                               new List<MarketBoardCurrentOfferings.MarketBoardItemListing>(),
                               (agg, next) =>
                               {
                                   agg.AddRange(next.InternalItemListings);
                                   return agg;
                               }));
            }

            private static IObservable<(uint CatalogId, List<MarketBoardHistory.MarketBoardHistoryListing> Sales)> SalesBatch(
                IObservable<MarketBoardHistory> source,
                IObservable<MarketBoardItemRequest> start)
            {
                var historyObservable = source.Publish().RefCount();

                IObservable<MarketBoardHistory> UntilBatchEnd(MarketBoardItemRequest request)
                {
                    return historyObservable
                           .Where(history => history.CatalogId != 0)
                           .Take(1);
                }

                uint catalogId = 0;
                return historyObservable
                       .Window(start, UntilBatchEnd)
                       .SelectMany(
                           o => o.Aggregate(
                               new List<MarketBoardHistory.MarketBoardHistoryListing>(),
                               (agg, next) =>
                               {
                                   catalogId = next.CatalogId;

                                   agg.AddRange(next.InternalHistoryListings);
                                   return agg;
                               }))
                       .Select(o => (catalogId, o));
            }
        }
    }
}
