using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;
using Dalamud.Data;
using Dalamud.Game.Gui;
using Dalamud.Game.Network.Internal.MarketBoardUploaders;
using Dalamud.Game.Network.Internal.MarketBoardUploaders.Universalis;
using Dalamud.Game.Network.Structures;
using Dalamud.Game.Player;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.Networking.Http;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Network;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

using Lumina.Excel.Sheets;

using Serilog;

namespace Dalamud.Game.Network.Internal;

/// <summary>
/// This class handles network notifications and uploading market board data.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal unsafe class NetworkHandlers : IInternalDisposableService
{
    private readonly IMarketBoardUploader uploader;

    private readonly IDisposable handleMarketBoardItemRequest;
    private readonly IDisposable handleMarketTaxRates;
    private readonly IDisposable handleMarketBoardPurchaseHandler;

    private readonly NetworkHandlersAddressResolver addressResolver;

    private readonly Hook<PublicContentDirector.Delegates.HandleEnterContentInfoPacket> cfPopHook;
    private readonly Hook<PacketDispatcher.Delegates.HandleMarketBoardPurchasePacket> mbPurchaseHook;
    private readonly Hook<InfoProxyItemSearch.Delegates.ProcessItemHistory> mbHistoryHook;
    private readonly Hook<CustomTalkReceiveResponse> customTalkHook; // used for marketboard taxes
    private readonly Hook<PacketDispatcher.Delegates.HandleMarketBoardItemRequestStartPacket> mbItemRequestStartHook;
    private readonly Hook<InfoProxyItemSearch.Delegates.AddPage> mbOfferingsHook;
    private readonly Hook<InfoProxyItemSearch.Delegates.SendPurchaseRequestPacket> mbSendPurchaseRequestHook;

    [ServiceManager.ServiceDependency]
    private readonly DalamudConfiguration configuration = Service<DalamudConfiguration>.Get();

    private readonly MarketBoardUploadGuard uploadGuard = new(
        static () => Environment.TickCount64,
        static line => Log.Information("{MbUploadGuardLine:l}", line));

    private readonly MarketBoardRequestPairer pairer = new(
        static () => Environment.TickCount64,
        static line => Log.Information("{MbPairGuardLine:l}", line));

    private bool disposing;

    [ServiceManager.ServiceConstructor]
    private NetworkHandlers(
        GameNetwork gameNetwork,
        TargetSigScanner sigScanner,
        HappyHttpClient happyHttpClient)
    {
        this.uploader = new UniversalisMarketBoardUploader(happyHttpClient);

        this.addressResolver = new NetworkHandlersAddressResolver();
        this.addressResolver.Setup(sigScanner);

        this.CfPop = _ => { };

        this.MbPurchaseObservable = Observable.Create<MarketBoardPurchase>(observer =>
        {
            this.MarketBoardPurchaseReceived += Observe;
            return () => { this.MarketBoardPurchaseReceived -= Observe; };

            void Observe(nint packetPtr)
            {
                observer.OnNext(MarketBoardPurchase.Read(packetPtr));
            }
        });

        this.MbHistoryObservable = Observable.Create<MarketBoardHistory>(observer =>
        {
            this.MarketBoardHistoryReceived += Observe;
            return () => { this.MarketBoardHistoryReceived -= Observe; };

            void Observe(nint packetPtr)
            {
                observer.OnNext(MarketBoardHistory.Read(packetPtr));
            }
        });

        this.MbTaxesObservable = Observable.Create<MarketTaxRates>(observer =>
        {
            this.MarketBoardTaxesReceived += Observe;
            return () => { this.MarketBoardTaxesReceived -= Observe; };

            void Observe(nint dataPtr)
            {
                // n.b. we precleared the packet information so we're sure that this is *just* tax rate info.
                observer.OnNext(MarketTaxRates.ReadFromCustomTalk(dataPtr));
            }
        });

        this.MbItemRequestObservable = Observable.Create<MarketBoardItemRequest>(observer =>
        {
            this.MarketBoardItemRequestStartReceived += Observe;
            return () => this.MarketBoardItemRequestStartReceived -= Observe;

            void Observe(nint dataPtr)
            {
                observer.OnNext(MarketBoardItemRequest.Read(dataPtr));
            }
        });

        this.MbOfferingsObservable = Observable.Create<MarketBoardCurrentOfferings>(observer =>
        {
            this.MarketBoardOfferingsReceived += Observe;
            return () => { this.MarketBoardOfferingsReceived -= Observe; };

            void Observe(nint packetPtr)
            {
                observer.OnNext(MarketBoardCurrentOfferings.Read(packetPtr));
            }
        });

        this.MbPurchaseSentObservable = Observable.Create<MarketBoardPurchaseHandler>(observer =>
        {
            this.MarketBoardPurchaseRequestSent += Observe;
            return () => { this.MarketBoardPurchaseRequestSent -= Observe; };

            void Observe(nint dataPtr)
            {
                // fortunately, this dataptr has the same structure as the sent packet.
                observer.OnNext(MarketBoardPurchaseHandler.Read(dataPtr));
            }
        });

        this.handleMarketBoardItemRequest = this.HandleMarketBoardItemRequest();
        this.handleMarketTaxRates = this.HandleMarketTaxRates();
        this.handleMarketBoardPurchaseHandler = this.HandleMarketBoardPurchaseHandler();

        this.mbPurchaseHook =
            Hook<PacketDispatcher.Delegates.HandleMarketBoardPurchasePacket>.FromAddress(
                PacketDispatcher.Addresses.HandleMarketBoardPurchasePacket.Value,
                this.MarketPurchasePacketDetour);
        this.mbPurchaseHook.Enable();

        this.mbHistoryHook =
            Hook<InfoProxyItemSearch.Delegates.ProcessItemHistory>.FromAddress(
                InfoProxyItemSearch.Addresses.ProcessItemHistory.Value,
                this.MarketHistoryPacketDetour);
        this.mbHistoryHook.Enable();

        this.customTalkHook =
            Hook<CustomTalkReceiveResponse>.FromAddress(
                this.addressResolver.CustomTalkEventResponsePacketHandler,
                this.CustomTalkReceiveResponseDetour);
        this.customTalkHook.Enable();

        this.mbItemRequestStartHook = Hook<PacketDispatcher.Delegates.HandleMarketBoardItemRequestStartPacket>.FromAddress(
            PacketDispatcher.Addresses.HandleMarketBoardItemRequestStartPacket.Value,
            this.MarketItemRequestStartDetour);
        this.mbItemRequestStartHook.Enable();

        this.mbOfferingsHook = Hook<InfoProxyItemSearch.Delegates.AddPage>.FromAddress(
            (nint)InfoProxyItemSearch.StaticVirtualTablePointer->AddPage,
            this.MarketBoardOfferingsDetour);
        this.mbOfferingsHook.Enable();

        this.mbSendPurchaseRequestHook = Hook<InfoProxyItemSearch.Delegates.SendPurchaseRequestPacket>.FromAddress(
            InfoProxyItemSearch.Addresses.SendPurchaseRequestPacket.Value,
            this.MarketBoardSendPurchaseRequestDetour);
        this.mbSendPurchaseRequestHook.Enable();

        this.cfPopHook = Hook<PublicContentDirector.Delegates.HandleEnterContentInfoPacket>.FromAddress(PublicContentDirector.Addresses.HandleEnterContentInfoPacket.Value, this.CfPopDetour);
        this.cfPopHook.Enable();
    }

    private delegate nint MarketBoardPurchasePacketHandler(nint a1, nint packetRef);

    private delegate nint MarketBoardHistoryPacketHandler(nint self, nint packetData, uint a3, char a4);

    private delegate void CustomTalkReceiveResponse(
        nuint a1, ushort eventId, byte responseId, uint* args, byte argCount);

    private delegate nint MarketBoardItemRequestStartPacketHandler(nint a1, nint packetRef);

    private delegate byte InfoProxyItemSearchAddPage(nint self, nint packetRef);

    private delegate byte MarketBoardSendPurchaseRequestPacket(InfoProxyItemSearch* infoProxy);

    /// <summary>
    /// Event which gets fired when a duty is ready.
    /// </summary>
    public event Action<ContentFinderCondition> CfPop;

    private event Action<nint>? MarketBoardPurchaseReceived;

    private event Action<nint>? MarketBoardHistoryReceived;

    private event Action<nint>? MarketBoardTaxesReceived;

    private event Action<nint>? MarketBoardItemRequestStartReceived;

    private event Action<nint>? MarketBoardOfferingsReceived;

    private event Action<nint>? MarketBoardPurchaseRequestSent;

    private event Action<MarketBoardRequestPairer.Completed>? MarketBoardQueryCompleted;

    private event Action<MarketBoardPurchaseHandler>? MarketBoardPurchaseMatched;

    /// <summary>
    /// Gets an observable to track marketboard purchase events.
    /// </summary>
    public IObservable<MarketBoardPurchase> MbPurchaseObservable { get; }

    /// <summary>
    /// Gets an observable to track marketboard history events.
    /// </summary>
    public IObservable<MarketBoardHistory> MbHistoryObservable { get; }

    /// <summary>
    /// Gets an observable to track marketboard tax events.
    /// </summary>
    public IObservable<MarketTaxRates> MbTaxesObservable { get; }

    /// <summary>
    /// Gets an observable to track marketboard item request events.
    /// </summary>
    public IObservable<MarketBoardItemRequest> MbItemRequestObservable { get; }

    /// <summary>
    /// Gets an observable to track marketboard offerings events.
    /// </summary>
    public IObservable<MarketBoardCurrentOfferings> MbOfferingsObservable { get; }

    /// <summary>
    /// Gets an observable to track marketboard purchase request events.
    /// </summary>
    public IObservable<MarketBoardPurchaseHandler> MbPurchaseSentObservable { get; }

    /// <summary>
    /// Disposes of managed and unmanaged resources.
    /// </summary>
    void IInternalDisposableService.DisposeService()
    {
        this.disposing = true;
        this.Dispose(this.disposing);
    }

    /// <summary>
    /// Disposes of managed and unmanaged resources.
    /// </summary>
    /// <param name="shouldDispose">Whether to execute the disposal.</param>
    protected void Dispose(bool shouldDispose)
    {
        if (!shouldDispose)
            return;

        this.handleMarketBoardItemRequest.Dispose();
        this.handleMarketTaxRates.Dispose();
        this.handleMarketBoardPurchaseHandler.Dispose();

        this.mbPurchaseHook.Dispose();
        this.mbHistoryHook.Dispose();
        this.customTalkHook.Dispose();
        this.mbItemRequestStartHook.Dispose();
        this.mbOfferingsHook.Dispose();
        this.mbSendPurchaseRequestHook.Dispose();
        this.cfPopHook.Dispose();

        if (this.uploadGuard.BuildSummary() is { } summary)
            Log.Information("{MbUploadGuardLine:l}", summary);
        if (this.pairer.BuildSummary() is { } pairSummary)
            Log.Information("{MbPairGuardLine:l}", pairSummary);
    }

    // 讀的欄位跟 PlayerState.CurrentWorld／HomeWorld 同一批；不保存指標。
    private static MarketBoardUploadGuard.Worlds ReadUploadWorlds()
    {
        var localPlayer = FFXIVClientStructs.FFXIV.Client.Game.Control.Control.GetLocalPlayer();
        var agentLobby = FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentLobby.Instance();
        var hasPlayer = localPlayer != null;
        var hasLobby = agentLobby != null;
        return new MarketBoardUploadGuard.Worlds(
            hasPlayer,
            hasPlayer ? localPlayer->CurrentWorld : 0u,
            hasPlayer ? localPlayer->HomeWorld : 0u,
            hasLobby && agentLobby->IsLoggedIn,
            hasLobby ? agentLobby->LobbyData.CurrentWorldId : 0u,
            hasLobby ? agentLobby->LobbyData.HomeWorldId : 0u);
    }

    // 契約：在 Rx 的選擇器裡被呼叫，絕不擲例外（擲了那條管線整個結束、之後都不上傳）。
    // 刻意不走 PlayerState.CurrentWorld：它沒有本機角色時會退回大廳欄位，那裡可能殘留舊世界。
    private UploaderInfo ResolveUploader(MarketBoardUploadGuard.Kind kind, uint itemId)
    {
        try
        {
            if (!this.configuration.IsMbCollect)
            {
                this.uploadGuard.NoteDisabled();
                return default;
            }

            var uploaderId = Service<PlayerState>.Get().ContentId;
            return this.uploadGuard.TryResolve(kind, itemId, ReadUploadWorlds(), out var worldId)
                       ? new UploaderInfo(true, uploaderId, worldId)
                       : default;
        }
        catch (Exception ex)
        {
            try
            {
                this.uploadGuard.NoteSkip(
                    kind, itemId, MarketBoardUploadGuard.SkipReason.ResolveFailed, default, ex.GetType().Name);
            }
            catch
            {
                // 診斷本身失敗也不能讓例外離開選擇器。
            }

            return default;
        }
    }

    private unsafe nint CfPopDetour(PublicContentDirector.EnterContentInfoPacket* packetData)
    {
        var result = this.cfPopHook.OriginalDisposeSafe(packetData);

        try
        {
            using var stream = new UnmanagedMemoryStream((byte*)packetData, 64);
            using var reader = new BinaryReader(stream);

            var notifyType = reader.ReadByte();
            stream.Position += 0x1B;
            var conditionId = reader.ReadUInt16();

            if (notifyType != 3)
                return result;

            if (this.configuration.DutyFinderTaskbarFlash)
                Util.FlashWindow();

            var cfCondition = LuminaUtils.CreateRef<ContentFinderCondition>(conditionId);

            if (!cfCondition.IsValid)
            {
                Log.Error("CFC key {ConditionId} not in Lumina data", conditionId);
                return result;
            }

            var cfcName = cfCondition.Value.Name.ToDalamudString();
            if (cfcName.Payloads.Count == 0)
                cfcName = "隨機任務";

            Task.Run(() =>
            {
                if (this.configuration.DutyFinderChatMessage)
                {
                    var b = new SeStringBuilder();
                    b.Append("副本匹配完成：");
                    b.Append(cfcName);
                    Service<ChatGui>.GetNullable()?.Print(b.Build());
                }

                this.CfPop.InvokeSafely(cfCondition.Value);
            }).ContinueWith(
                task => Log.Error(task.Exception, "CfPop.Invoke failed"),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CfPopDetour threw an exception");
        }

        return result;
    }

    // 契約：MarketBoardQueryCompleted 只在遊戲執行緒的封包 detour 裡觸發，ResolveUploader 因此在遊戲執行緒讀世界。
    private IDisposable HandleMarketBoardItemRequest()
    {
        return Observable.Create<MarketBoardRequestPairer.Completed>(observer =>
                         {
                             this.MarketBoardQueryCompleted += observer.OnNext;
                             return () => { this.MarketBoardQueryCompleted -= observer.OnNext; };
                         })
                         .Select(query => (query, Uploader: this.ResolveUploader(MarketBoardUploadGuard.Kind.Listings, query.CatalogId)))
                         .Where(data => data.Uploader.Upload)
                         .Where(this.ShouldUpload)
                         .SubscribeOn(ThreadPoolScheduler.Instance)
                         .Subscribe(
                             data =>
                             {
                                 var (query, uploaderInfo) = data;
                                 this.UploadMarketBoardData(query, uploaderInfo.UploaderId, uploaderInfo.WorldId);
                             },
                             ex => Log.Error(ex, "Failed to handle Market Board item request event"));
    }

    private void UploadMarketBoardData(MarketBoardRequestPairer.Completed query, ulong uploaderId, uint worldId)
    {
        var (request, catalogId, sales, listings) = query;
        if (listings.Count != request.AmountToArrive)
        {
            Log.Error(
                "Wrong number of Market Board listings received for request: {ListingsCount} != {RequestAmountToArrive} item#{RequestCatalogId}",
                listings.Count, request.AmountToArrive, catalogId);
            return;
        }

        Log.Verbose(
            "Market Board request resolved, starting upload: item#{CatalogId} listings#{ListingsObserved} sales#{SalesObserved}",
            catalogId,
            listings.Count,
            sales.Count);

        request.CatalogId = catalogId;
        request.Listings.AddRange(listings);
        request.History.AddRange(sales);

        Task.Run(() => this.uploader.Upload(request, uploaderId, worldId))
            .ContinueWith(
                task => Log.Error(task.Exception, "Market Board offerings data upload failed"),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    private IDisposable HandleMarketTaxRates()
    {
        return this.MbTaxesObservable
                   .Select((taxes) => (taxes, Uploader: this.ResolveUploader(MarketBoardUploadGuard.Kind.Tax, 0)))
                   .Where(data => data.Uploader.Upload)
                   .Where(this.ShouldUpload)
                   .SubscribeOn(ThreadPoolScheduler.Instance)
                   .Subscribe(
                       data =>
                       {
                           var (taxes, uploaderInfo) = data;

                           Log.Verbose(
                               "MarketTaxRates: limsa#{0} grid#{1} uldah#{2} ish#{3} kugane#{4} cr#{5} sh#{6}",
                               taxes.LimsaLominsaTax,
                               taxes.GridaniaTax,
                               taxes.UldahTax,
                               taxes.IshgardTax,
                               taxes.KuganeTax,
                               taxes.CrystariumTax,
                               taxes.SharlayanTax);

                           Task.Run(() => this.uploader.UploadTax(taxes, uploaderInfo.UploaderId, uploaderInfo.WorldId))
                               .ContinueWith(
                                   task => Log.Error(task.Exception, "Market Board tax data upload failed"),
                                   TaskContinuationOptions.OnlyOnFaulted);
                       },
                       ex => Log.Error(ex, "Failed to handle Market Board tax data event"));
    }

    // 契約：MarketBoardPurchaseMatched 只在遊戲執行緒的封包 detour 裡觸發。
    private IDisposable HandleMarketBoardPurchaseHandler()
    {
        return Observable.Create<MarketBoardPurchaseHandler>(observer =>
                   {
                       this.MarketBoardPurchaseMatched += observer.OnNext;
                       return () => { this.MarketBoardPurchaseMatched -= observer.OnNext; };
                   })
                   .Select(handler => (handler, Uploader: this.ResolveUploader(MarketBoardUploadGuard.Kind.Purchase, handler.CatalogId)))
                   .Where(data => data.Uploader.Upload)
                   .Where(this.ShouldUpload)
                   .SubscribeOn(ThreadPoolScheduler.Instance)
                   .Subscribe(
                       data =>
                       {
                           var (handler, uploaderInfo) = data;
                           Log.Verbose(
                               "Bought {PurchaseItemQuantity}x {HandlerCatalogId} for {HandlerPricePerUnit} gils, listing id is {HandlerListingId}",
                               handler.ItemQuantity,
                               handler.CatalogId,
                               handler.PricePerUnit * handler.ItemQuantity,
                               handler.ListingId);
                           Task.Run(() => this.uploader.UploadPurchase(handler, uploaderInfo.UploaderId, uploaderInfo.WorldId))
                               .ContinueWith(
                                   task => Log.Error(task.Exception, "Market Board purchase data upload failed"),
                                   TaskContinuationOptions.OnlyOnFaulted);
                       },
                       ex => Log.Error(ex, "Failed to handle Market Board purchase event"));
    }

    private bool ShouldUpload<T>(T any)
    {
        return this.configuration.IsMbCollect;
    }

    private void MarketPurchasePacketDetour(uint targetId, nint packetData)
    {
        try
        {
            this.MarketBoardPurchaseReceived?.InvokeSafely(packetData);
            if (this.pairer.OnPurchaseResult(MarketBoardPurchase.Read(packetData)) is { } bought)
                this.MarketBoardPurchaseMatched?.InvokeSafely(bought);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MarketPurchasePacketHandler threw an exception");
        }

        this.mbPurchaseHook.OriginalDisposeSafe(targetId, packetData);
    }

    private void MarketHistoryPacketDetour(InfoProxyItemSearch* a1, nint packetData)
    {
        try
        {
            this.MarketBoardHistoryReceived?.InvokeSafely(packetData);

            // a1 是遊戲這一次呼叫傳進來的 this（呼叫端已判非 null），只讀不存。
            var searched = a1 != null ? a1->SearchItemId : 0u;
            if (this.pairer.OnHistory(MarketBoardHistory.Read(packetData), searched) is { } done)
                this.MarketBoardQueryCompleted?.InvokeSafely(done);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MarketHistoryPacketDetour threw an exception");
        }

        this.mbHistoryHook.OriginalDisposeSafe(a1, packetData);
    }

    private void CustomTalkReceiveResponseDetour(nuint a1, ushort eventId, byte responseId, uint* args, byte argCount)
    {
        try
        {
            // Event ID 0 covers the crystarium, 7 covers all other cities
            if (eventId is 7 or 0 && responseId == 8)
                this.MarketBoardTaxesReceived?.InvokeSafely((nint)args);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CustomTalkReceiveResponseDetour threw an exception");
        }

        this.customTalkHook.OriginalDisposeSafe(a1, eventId, responseId, args, argCount);
    }

    private void MarketItemRequestStartDetour(uint targetId, nint packetRef)
    {
        try
        {
            this.MarketBoardItemRequestStartReceived?.InvokeSafely(packetRef);
            this.pairer.OnStart(MarketBoardItemRequest.Read(packetRef));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MarketItemRequestStartDetour threw an exception");
        }

        this.mbItemRequestStartHook.OriginalDisposeSafe(targetId, packetRef);
    }

    private void MarketBoardOfferingsDetour(InfoProxyItemSearch* a1, nint packetRef)
    {
        try
        {
            this.MarketBoardOfferingsReceived?.InvokeSafely(packetRef);

            // 與遊戲自己的判斷相同：分頁的請求序號不等於目前序號就是舊查詢的分頁，遊戲會丟掉它。
            var page = MarketBoardCurrentOfferings.Read(packetRef);
            var current = a1 != null && a1->InfoProxyPageInterface.CurrentRequestId == (byte)page.RequestId;
            if (this.pairer.OnPage(page, current) is { } done)
                this.MarketBoardQueryCompleted?.InvokeSafely(done);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MarketBoardOfferingsDetour threw an exception");
        }

        this.mbOfferingsHook.OriginalDisposeSafe(a1, packetRef);
    }

    private bool MarketBoardSendPurchaseRequestDetour(InfoProxyItemSearch* infoProxyItemSearch)
    {
        try
        {
            this.MarketBoardPurchaseRequestSent?.InvokeSafely((nint)infoProxyItemSearch + 0x5680);
            if (infoProxyItemSearch != null)
                this.pairer.OnPurchaseSent(MarketBoardPurchaseHandler.Read((nint)infoProxyItemSearch + 0x5680));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "MarketBoardSendPurchaseRequestDetour threw an exception");
        }

        return this.mbSendPurchaseRequestHook.OriginalDisposeSafe(infoProxyItemSearch);
    }

    private readonly record struct UploaderInfo(bool Upload, ulong UploaderId, uint WorldId);
}
