using System;
using System.Collections.Generic;

using Dalamud.Game.Network.Structures;

namespace Dalamud.Game.Network.Internal.MarketBoardUploaders;

/// <summary>
/// Assembles one market board query (start, sale history, listing pages) and one purchase (request, result)
/// by item id instead of by arrival order; anything that does not add up is dropped, never re-paired.
/// </summary>
/// <remarks>
/// Contract: never touches game memory (callers read the packets and pass the values in); callable from any
/// thread; the log delegate is always invoked outside the lock. An accepted start packet always discards an
/// unfinished query: late packets of an older query must never complete a newer one. A refused one does not
/// change the game's request id, so the unfinished query keeps collecting its own pages.
/// </remarks>
internal sealed class MarketBoardRequestPairer
{
    /// <summary>Fixed prefix of every line this class writes.</summary>
    public const string LogPrefix = "[MbPairGuard]";

    /// <summary>Same reason is written at most once per this many milliseconds.</summary>
    public const long DropLogWindowMs = 60_000;

    private const uint HqItemOffset = 1_000_000;

    private readonly Func<long> clockMs;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly DropStat[] drops = new DropStat[Enum.GetValues<DropReason>().Length];

    private DropStat refusedKept;
    private Query? pending;
    private MarketBoardPurchaseHandler? pendingPurchase;
    private long completedQueries;
    private long matchedPurchases;
    private long ignoredZeroHistory;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarketBoardRequestPairer"/> class.
    /// </summary>
    /// <param name="clockMs">Monotonic clock in milliseconds.</param>
    /// <param name="log">Receives each finished diagnostic line.</param>
    public MarketBoardRequestPairer(Func<long> clockMs, Action<string> log)
    {
        this.clockMs = clockMs;
        this.log = log;
    }

    /// <summary>Why data is not uploaded.</summary>
    public enum DropReason
    {
        /// <summary>A new start packet arrived before the previous query had all its parts.</summary>
        Superseded = 0,

        /// <summary>Sale history arrived while no accepted query was open.</summary>
        HistoryWithoutQuery = 1,

        /// <summary>Sale history is not for the item the client is searching; the query keeps waiting.</summary>
        HistoryNotSearchedItem = 2,

        /// <summary>A second, different sale history arrived for the same query.</summary>
        HistoryConflict = 3,

        /// <summary>A listing page arrived while no accepted query was open.</summary>
        PageWithoutQuery = 4,

        /// <summary>The game itself discards this page as belonging to an older query.</summary>
        StalePage = 5,

        /// <summary>Listings of one query name more than one item.</summary>
        ListingItemMismatch = 6,

        /// <summary>More listing pages arrived than the start packet announced.</summary>
        ExtraPage = 7,

        /// <summary>A purchase request got no result before the next purchase request.</summary>
        PurchaseUnanswered = 8,

        /// <summary>A purchase result arrived without a pending request.</summary>
        PurchaseResultWithoutRequest = 9,

        /// <summary>The purchase result names another item or quantity than the request.</summary>
        PurchaseMismatch = 10,
    }

    /// <summary>
    /// Handles a start packet; an accepted one ends any unfinished query, a refused one usually keeps it.
    /// </summary>
    /// <param name="request">The parsed start packet.</param>
    public void OnStart(MarketBoardItemRequest request)
    {
        string line;
        lock (this.gate)
        {
            if (!request.Ok && this.pending is { } kept && kept.CanOutliveRefusal)
            {
                line = this.LimitLocked(ref this.refusedKept, $"refused keep item={ItemText(kept.ItemForLog)} {kept.Describe()}");
            }
            else
            {
                line = this.pending is { } old
                           ? this.DropLocked(DropReason.Superseded, old.ItemForLog, old.Describe() + $" next={(request.Ok ? "accepted" : "refused")}")
                           : null;
                this.pending = request.Ok ? new Query(request) : null;
            }
        }

        this.Write(line);
    }

    /// <summary>
    /// Handles a sale history packet.
    /// </summary>
    /// <param name="history">The parsed history packet.</param>
    /// <param name="searchedItemId">The item the client was searching when the packet arrived; 0 when unknown.</param>
    /// <returns>The finished query, or null.</returns>
    public Completed? OnHistory(MarketBoardHistory history, uint searchedItemId)
    {
        string line = null;
        Completed? done = null;
        lock (this.gate)
        {
            if (history.CatalogId == 0)
            {
                // The game ignores these packets too.
                this.ignoredZeroHistory++;
            }
            else if (this.pending is not { } query)
            {
                line = this.DropLocked(DropReason.HistoryWithoutQuery, history.CatalogId, $"searching={searchedItemId}");
            }
            else if (history.CatalogId != searchedItemId)
            {
                line = this.DropLocked(DropReason.HistoryNotSearchedItem, history.CatalogId, $"searching={searchedItemId}");
            }
            else if (query.History is { } seen)
            {
                if (seen.CatalogId != history.CatalogId)
                {
                    line = this.DropLocked(DropReason.HistoryConflict, history.CatalogId, query.Describe());
                    this.pending = null;
                }
            }
            else if (!query.ListingsAreFor(history.CatalogId))
            {
                line = this.DropLocked(DropReason.ListingItemMismatch, history.CatalogId, query.Describe());
                this.pending = null;
            }
            else
            {
                query.History = history;
                done = this.TryCompleteLocked(query);
            }
        }

        this.Write(line);
        return done;
    }

    /// <summary>
    /// Handles one listing page.
    /// </summary>
    /// <param name="page">The parsed page.</param>
    /// <param name="isCurrentQuery">False when the game discards the page as belonging to an older query.</param>
    /// <returns>The finished query, or null.</returns>
    public Completed? OnPage(MarketBoardCurrentOfferings page, bool isCurrentQuery)
    {
        string line = null;
        Completed? done = null;
        var pageItem = page.InternalItemListings.Count > 0 ? BaseItem(page.InternalItemListings[0].CatalogId) : 0u;
        lock (this.gate)
        {
            if (!isCurrentQuery)
            {
                line = this.DropLocked(DropReason.StalePage, pageItem, $"listings={page.InternalItemListings.Count}");
            }
            else if (this.pending is not { } query)
            {
                line = this.DropLocked(DropReason.PageWithoutQuery, pageItem, $"listings={page.InternalItemListings.Count}");
            }
            else if (query.PagesReceived >= query.PagesExpected)
            {
                line = this.DropLocked(DropReason.ExtraPage, query.ItemForLog, query.Describe());
                this.pending = null;
            }
            else
            {
                query.PagesReceived++;
                query.Listings.AddRange(page.InternalItemListings);
                if (!query.ListingsAreFor(query.History?.CatalogId ?? 0))
                {
                    line = this.DropLocked(DropReason.ListingItemMismatch, query.ItemForLog, query.Describe());
                    this.pending = null;
                }
                else
                {
                    done = this.TryCompleteLocked(query);
                }
            }
        }

        this.Write(line);
        return done;
    }

    /// <summary>
    /// Handles an outgoing purchase request; an unanswered earlier request is dropped.
    /// </summary>
    /// <param name="handler">The request as sent.</param>
    public void OnPurchaseSent(MarketBoardPurchaseHandler handler)
    {
        string line = null;
        lock (this.gate)
        {
            if (this.pendingPurchase is { } old)
                line = this.DropLocked(DropReason.PurchaseUnanswered, old.CatalogId, $"qty={old.ItemQuantity}");
            this.pendingPurchase = handler;
        }

        this.Write(line);
    }

    /// <summary>
    /// Handles a purchase result from the server.
    /// </summary>
    /// <param name="purchase">The parsed result.</param>
    /// <returns>The request to report as bought, or null.</returns>
    public MarketBoardPurchaseHandler? OnPurchaseResult(MarketBoardPurchase purchase)
    {
        string line = null;
        MarketBoardPurchaseHandler? matched = null;
        lock (this.gate)
        {
            var handler = this.pendingPurchase;
            this.pendingPurchase = null;
            if (handler == null)
            {
                line = this.DropLocked(DropReason.PurchaseResultWithoutRequest, purchase.CatalogId, $"status={purchase.Status}");
            }
            else if (purchase.Status != 0)
            {
                // The purchase failed; nothing was bought, nothing to report.
            }
            else if (BaseItem(purchase.CatalogId) != handler.CatalogId || purchase.ItemQuantity != handler.ItemQuantity)
            {
                line = this.DropLocked(
                    DropReason.PurchaseMismatch,
                    handler.CatalogId,
                    $"result={purchase.CatalogId}x{purchase.ItemQuantity} request={handler.CatalogId}x{handler.ItemQuantity}");
            }
            else
            {
                this.matchedPurchases++;
                matched = handler;
            }
        }

        this.Write(line);
        return matched;
    }

    /// <summary>
    /// Session totals, including drops that were suppressed after the last written line.
    /// </summary>
    /// <returns>One line, or null when nothing ever happened.</returns>
    public string BuildSummary()
    {
        lock (this.gate)
        {
            long dropped = 0;
            var parts = new List<string>();
            foreach (var reason in Enum.GetValues<DropReason>())
            {
                var total = this.drops[(int)reason].Total;
                dropped += total;
                if (total > 0)
                    parts.Add($"{reason}:{total}");
            }

            if (dropped + this.completedQueries + this.matchedPurchases + this.ignoredZeroHistory + this.refusedKept.Total == 0)
                return null;

            return $"{LogPrefix} summary completed={this.completedQueries} purchases={this.matchedPurchases} " +
                   $"ignoredZeroHistory={this.ignoredZeroHistory} refusedKept={this.refusedKept.Total} " +
                   $"dropped={(parts.Count == 0 ? "none" : string.Join(",", parts))}";
        }
    }

    private static uint BaseItem(uint catalogId) => catalogId >= HqItemOffset ? catalogId - HqItemOffset : catalogId;

    private static string ItemText(uint itemId) => itemId == 0 ? "none" : itemId.ToString();

    private Completed? TryCompleteLocked(Query query)
    {
        if (query.History is not { } history || query.PagesReceived != query.PagesExpected)
            return null;

        this.pending = null;
        this.completedQueries++;
        return new Completed(query.Request, history.CatalogId, history.InternalHistoryListings, query.Listings);
    }

    private string DropLocked(DropReason reason, uint itemId, string detail) =>
        this.LimitLocked(ref this.drops[(int)reason], $"drop reason={reason} item={ItemText(itemId)} {detail}");

    private string LimitLocked(ref DropStat stat, string body)
    {
        var now = this.clockMs();
        stat.Total++;
        if (stat.Total > 1 && now - stat.LastLogMs < DropLogWindowMs)
        {
            stat.Suppressed++;
            return null;
        }

        var line = $"{LogPrefix} {body} suppressed={stat.Suppressed} total={stat.Total}";
        stat.LastLogMs = now;
        stat.Suppressed = 0;
        return line;
    }

    private void Write(string line)
    {
        if (line != null)
            this.log(line);
    }

    /// <summary>
    /// A finished query whose parts all name the same item.
    /// </summary>
    /// <param name="Request">The start packet.</param>
    /// <param name="CatalogId">The item, taken from the sale history packet.</param>
    /// <param name="Sales">Sale history entries.</param>
    /// <param name="Listings">Listings from all pages.</param>
    public readonly record struct Completed(
        MarketBoardItemRequest Request,
        uint CatalogId,
        List<MarketBoardHistory.MarketBoardHistoryListing> Sales,
        List<MarketBoardCurrentOfferings.MarketBoardItemListing> Listings);

    private struct DropStat
    {
        public long Total;
        public long Suppressed;
        public long LastLogMs;
    }

    private sealed class Query
    {
        public Query(MarketBoardItemRequest request)
        {
            this.Request = request;
            this.PagesExpected = (int)((request.AmountToArrive + 9) / 10);
        }

        public MarketBoardItemRequest Request { get; }

        public int PagesExpected { get; }

        public int PagesReceived { get; set; }

        public MarketBoardHistory? History { get; set; }

        public List<MarketBoardCurrentOfferings.MarketBoardItemListing> Listings { get; } = [];

        public uint ItemForLog => this.History?.CatalogId ?? (this.Listings.Count > 0 ? BaseItem(this.Listings[0].CatalogId) : 0u);

        // After a refusal the client searches the refused item, so a later history may be that item's. A query that
        // still awaits pages has them to check it against; an empty one without history has nothing.
        public bool CanOutliveRefusal => this.History != null || this.PagesExpected > 0;

        // itemId 0 only requires the listings to agree with each other.
        public bool ListingsAreFor(uint itemId)
        {
            foreach (var listing in this.Listings)
            {
                var item = BaseItem(listing.CatalogId);
                if (itemId == 0)
                    itemId = item;
                else if (item != itemId)
                    return false;
            }

            return true;
        }

        public string Describe() =>
            $"pages={this.PagesReceived}/{this.PagesExpected} listings={this.Listings.Count}/{this.Request.AmountToArrive} " +
            $"history={(this.History == null ? 0 : 1)}";
    }
}
