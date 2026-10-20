using System.Runtime.InteropServices;

using Dalamud.Bindings.ImGui;
using Dalamud.Game.NativeWrapper;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Hooking;
using Dalamud.Interface.Utility;
using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Dalamud.Utility.Internal;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Dalamud.Game.Gui;

/// <summary>
/// A class handling many aspects of the in-game UI.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal sealed unsafe class GameGui : IInternalDisposableService, IGameGui
{
    private static readonly ModuleLog Log = new("GameGui");

    private static readonly DetourFault UiVisibilityFault = new("GameGui.SetUiVisibility", "切換介面顯示（通知插件）");

    private static readonly DetourFault Utf8StringFault = new("GameGui.Utf8StringFromSequence", "建立遊戲字串");

    private readonly GameGuiAddressResolver address;

    // Null when the address is missing or misplaced (see FindHookTargets); the string hook is required.
    private readonly Hook<AgentItemDetail.Delegates.Update>? handleItemHoverHook;
    private readonly Hook<AgentItemDetail.Delegates.ReceiveEvent>? handleItemOutHook;
    private readonly Hook<AgentActionDetail.Delegates.HandleActionHover>? handleActionHoverHook;
    private readonly Hook<AgentActionDetail.Delegates.ReceiveEvent>? handleActionOutHook;
    private readonly Hook<HandleImmDelegate>? handleImmHook;
    private readonly Hook<RaptureAtkModule.Delegates.SetUiVisibility>? setUiVisibilityHook;
    private readonly Hook<Utf8String.Delegates.Ctor_FromSequence> utf8StringFromSequenceHook;
    private readonly Hook<RaptureAtkModule.Delegates.Update>? raptureAtkModuleUpdateHook;

    [ServiceManager.ServiceConstructor]
    private GameGui(TargetSigScanner sigScanner)
    {
        this.address = new GameGuiAddressResolver();
        this.address.Setup(sigScanner);

        Log.Verbose("===== G A M E G U I =====");
        Log.Verbose($"HandleImm address {Util.DescribeAddress(this.address.HandleImm)}");

        // Required, unlike the hooks below: whatever builds a game string from a null source would crash without it.
        var targets = HookTargets.For(nameof(GameGui), sigScanner);
        var utf8StringFromSequence = Utf8String.Addresses.Ctor_FromSequence.Value;
        if (!targets.InCode(utf8StringFromSequence, 1))
            throw new InvalidOperationException($"Utf8String.Ctor_FromSequence (0x{utf8StringFromSequence:X}) is unresolved or not in the game's code; GameGui cannot start without it");

        var hooks = FindHookTargets(targets, this.address);
        this.handleItemHoverHook = hooks.ItemHover != 0 ? Hook<AgentItemDetail.Delegates.Update>.FromAddress(hooks.ItemHover, this.HandleItemHoverDetour) : null;
        this.handleItemOutHook = hooks.ItemOut != 0 ? Hook<AgentItemDetail.Delegates.ReceiveEvent>.FromAddress(hooks.ItemOut, this.HandleItemOutDetour) : null;

        this.handleActionHoverHook = hooks.ActionHover != 0 ? Hook<AgentActionDetail.Delegates.HandleActionHover>.FromAddress(hooks.ActionHover, this.HandleActionHoverDetour) : null;
        this.handleActionOutHook = hooks.ActionOut != 0 ? Hook<AgentActionDetail.Delegates.ReceiveEvent>.FromAddress(hooks.ActionOut, this.HandleActionOutDetour) : null;

        this.handleImmHook = hooks.Imm != 0 ? Hook<HandleImmDelegate>.FromAddress(hooks.Imm, this.HandleImmDetour) : null;

        this.setUiVisibilityHook = hooks.SetUiVisibility != 0 ? Hook<RaptureAtkModule.Delegates.SetUiVisibility>.FromAddress(hooks.SetUiVisibility, this.SetUiVisibilityDetour) : null;

        this.utf8StringFromSequenceHook = Hook<Utf8String.Delegates.Ctor_FromSequence>.FromAddress(utf8StringFromSequence, this.Utf8StringFromSequenceDetour);

        this.raptureAtkModuleUpdateHook = hooks.UpdateSlot != 0
                                              ? Hook<RaptureAtkModule.Delegates.Update>.FromFunctionPointerVariable(hooks.UpdateSlot, this.RaptureAtkModuleUpdateDetour)
                                              : null;
        targets.ReportDisabled();

        this.handleItemHoverHook?.Enable();
        this.handleItemOutHook?.Enable();
        this.handleImmHook?.Enable();
        this.setUiVisibilityHook?.Enable();
        this.handleActionHoverHook?.Enable();
        this.handleActionOutHook?.Enable();
        this.utf8StringFromSequenceHook.Enable();
        this.raptureAtkModuleUpdateHook?.Enable();
    }

    // Hooked delegates

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate char HandleImmDelegate(IntPtr framework, char a2, byte a3);

    /// <inheritdoc/>
    public event EventHandler<bool>? UiHideToggled;

    /// <inheritdoc/>
    public event EventHandler<ulong>? HoveredItemChanged;

    /// <inheritdoc/>
    public event EventHandler<HoveredAction>? HoveredActionChanged;

    /// <inheritdoc/>
    public event Action<AgentUpdateFlag> AgentUpdate;

    /// <inheritdoc/>
    public bool GameUiHidden { get; private set; }

    /// <inheritdoc/>
    public ulong HoveredItem { get; set; }

    /// <inheritdoc/>
    public HoveredAction HoveredAction { get; } = new HoveredAction();

    /// <inheritdoc/>
    public bool OpenMapWithMapLink(MapLinkPayload mapLink)
    {
        // RaptureAtkModule.Instance() is a hand-written wrapper that walks
        // Framework -> UIModule -> GetRaptureAtkModule(), returning null at each step if the previous
        // one is null. It therefore legitimately returns null before the UI module exists. Reading
        // through it raises an AccessViolationException, a corrupted-state exception that no
        // try/catch can recover from, so bail out and report failure instead.
        var raptureAtkModule = RaptureAtkModule.Instance();
        if (raptureAtkModule == null)
            return false;

        return raptureAtkModule->OpenMapWithMapLink(mapLink.DataString);
    }

    /// <inheritdoc/>
    public bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)
        => this.WorldToScreen(worldPos, out screenPos, out var inView) && inView;

    /// <inheritdoc/>
    public bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos, out bool inView)
    {
        // Read current ViewProjectionMatrix plus game window size
        var windowPos = ImGuiHelpers.MainViewport.Pos;
        var viewProjectionMatrix = Control.Instance()->ViewProjectionMatrix;

        // Device.Instance() resolves a [StaticAddress(..., isPointer: true)] slot: the pointer is null
        // until the graphics device has been created, and reading Width/Height through it raises an
        // AccessViolationException that no try/catch can recover from. Bail out through the same
        // failure path the degenerate-W case below uses. Control.Instance() above deliberately gets no
        // such check: without isPointer the static address *is* the struct, so it can only throw.
        var device = Device.Instance();
        if (device == null)
        {
            screenPos = Vector2.Zero;
            inView = false;
            return false;
        }

        float width = device->Width;
        float height = device->Height;

        var pCoords = Vector4.Transform(new Vector4(worldPos, 1.0f), viewProjectionMatrix);
        var inFront = pCoords.W > 0.0f;

        if (Math.Abs(pCoords.W) < float.Epsilon)
        {
            screenPos = Vector2.Zero;
            inView = false;
            return false;
        }

        pCoords *= MathF.Abs(1.0f / pCoords.W);
        screenPos = new Vector2(pCoords.X, pCoords.Y);

        screenPos.X = (0.5f * width * (screenPos.X + 1f)) + windowPos.X;
        screenPos.Y = (0.5f * height * (1f - screenPos.Y)) + windowPos.Y;

        inView = inFront &&
                 screenPos.X > windowPos.X && screenPos.X < windowPos.X + width &&
                 screenPos.Y > windowPos.Y && screenPos.Y < windowPos.Y + height;

        return inFront;
    }

    /// <inheritdoc/>
    public bool ScreenToWorld(Vector2 screenPos, out Vector3 worldPos, float rayDistance = 100000.0f)
    {
        // The game is only visible in the main viewport, so if the cursor is outside
        // of the game window, do not bother calculating anything
        var windowPos = ImGuiHelpers.MainViewport.Pos;
        var windowSize = ImGuiHelpers.MainViewport.Size;

        if (screenPos.X < windowPos.X || screenPos.X > windowPos.X + windowSize.X ||
            screenPos.Y < windowPos.Y || screenPos.Y > windowPos.Y + windowSize.Y)
        {
            worldPos = default;
            return false;
        }

        // CameraManager.Instance() resolves a [StaticAddress(..., isPointer: true)] slot that is null
        // until the scene camera manager has been built. The camera == null check below is too late:
        // evaluating CurrentCamera already dereferences the manager, an AccessViolationException that
        // no try/catch can recover from. Take the manager out first and fail through the exact same
        // path the null-camera case already uses.
        var cameraManager = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CameraManager.Instance();
        if (cameraManager == null)
        {
            worldPos = Vector3.Zero;
            return false;
        }

        var camera = cameraManager->CurrentCamera;
        if (camera == null)
        {
            worldPos = Vector3.Zero;
            return false;
        }

        var ray = camera->ScreenPointToRay(screenPos);
        var result = BGCollisionModule.RaycastMaterialFilter(ray.Origin, ray.Direction, out var hit);
        worldPos = hit.Point;
        return result;
    }

    /// <inheritdoc/>
    public UIModulePtr GetUIModule()
    {
        return (nint)UIModule.Instance();
    }

    /// <inheritdoc/>
    public AtkUnitBasePtr GetAddonByName(string name, int index = 1)
    {
        var unitManager = RaptureAtkUnitManager.Instance();
        if (unitManager == null)
            return 0;

        return (nint)unitManager->GetAddonByName(name, index);
    }

    /// <inheritdoc/>
    public T* GetAddonByName<T>(string name, int index = 1) where T : unmanaged
        => (T*)this.GetAddonByName(name, index).Address;

    /// <inheritdoc/>
    public AgentInterfacePtr GetAgentById(int id)
    {
        var agentModule = AgentModule.Instance();
        if (agentModule == null || id < 0 || id >= agentModule->Agents.Length)
            return 0;

        return (nint)agentModule->Agents[id].Value;
    }

    /// <inheritdoc/>
    public AgentInterfacePtr FindAgentInterface(string addonName)
    {
        var addon = this.GetAddonByName(addonName);
        return this.FindAgentInterface(addon);
    }

    /// <inheritdoc/>
    public AgentInterfacePtr FindAgentInterface(AtkUnitBasePtr addon)
    {
        if (addon.IsNull)
            return 0;

        var agentModule = AgentModule.Instance();
        if (agentModule == null)
            return 0;

        var addonId = addon.ParentId == 0 ? addon.Id : addon.ParentId;
        if (addonId == 0)
            return 0;

        foreach (AgentInterface* agent in agentModule->Agents)
        {
            if (agent != null && agent->AddonId == addonId)
                return (nint)agent;
        }

        return 0;
    }

    /// <summary>
    /// Disables the hooks and submodules of this module.
    /// </summary>
    void IInternalDisposableService.DisposeService()
    {
        this.handleItemHoverHook?.Dispose();
        this.handleItemOutHook?.Dispose();
        this.handleImmHook?.Dispose();
        this.setUiVisibilityHook?.Dispose();
        this.handleActionHoverHook?.Dispose();
        this.handleActionOutHook?.Dispose();
        this.utf8StringFromSequenceHook.Dispose();
        this.raptureAtkModuleUpdateHook?.Dispose();
    }

    /// <summary>
    /// Checks the addresses of the hooks this service can run without. A hover hook and the hook that clears it are
    /// kept together: one without the other would leave a stale hovered item or action.
    /// </summary>
    /// <param name="targets">The checks.</param>
    /// <param name="address">The resolved signatures.</param>
    /// <returns>The functions, 0 where left out; for Update, the address of its vtable slot.</returns>
    internal static HookAddresses FindHookTargets(HookTargets targets, GameGuiAddressResolver address)
    {
        const string hoveredItem = "滑鼠指向物品的偵測";
        const string hoveredAction = "滑鼠指向技能的偵測";

        var itemDetail = targets.VirtualTable(AgentItemDetail.StaticVirtualTablePointer);
        var itemHover = targets.Slot("AgentItemDetail.Update", hoveredItem, itemDetail, itemDetail == null ? 0 : (nint)itemDetail->Update);
        var itemOut = targets.Slot("AgentItemDetail.ReceiveEvent", hoveredItem, itemDetail, itemDetail == null ? 0 : (nint)itemDetail->ReceiveEvent);
        if (!targets.AllOrNone(hoveredItem, ("AgentItemDetail.Update", itemHover), ("AgentItemDetail.ReceiveEvent", itemOut)))
            itemHover = itemOut = 0;

        var actionDetail = targets.VirtualTable(AgentActionDetail.StaticVirtualTablePointer);
        var actionHover = targets.Function("HandleActionHover", hoveredAction, AgentActionDetail.Addresses.HandleActionHover.Value);
        var actionOut = targets.Slot("AgentActionDetail.ReceiveEvent", hoveredAction, actionDetail, actionDetail == null ? 0 : (nint)actionDetail->ReceiveEvent);
        if (!targets.AllOrNone(hoveredAction, ("HandleActionHover", actionHover), ("AgentActionDetail.ReceiveEvent", actionOut)))
            actionHover = actionOut = 0;

        var imm = targets.Function("HandleImm", "在 Dalamud 視窗打字時擋下遊戲的文字輸入", address.HandleImm, HookTargets.SignatureNotFound);

        var atkModule = targets.VirtualTable(RaptureAtkModule.StaticVirtualTablePointer);
        var setUiVisibility = targets.Slot("RaptureAtkModule.SetUiVisibility", "隱藏介面的偵測", atkModule, atkModule == null ? 0 : (nint)atkModule->SetUiVisibility);
        var update = targets.Slot("RaptureAtkModule.Update", "介面更新事件（背包變動等）", atkModule, atkModule == null ? 0 : (nint)atkModule->Update);

        return new(itemHover, itemOut, actionHover, actionOut, imm, setUiVisibility, update == 0 ? 0 : (nint)(&atkModule->Update));
    }

    /// <summary>
    /// Indicates if the game is in the lobby scene (title screen, chara select, chara make, aesthetician etc.).
    /// </summary>
    /// <returns>A value indicating whether the game is in the lobby scene.</returns>
    internal bool IsInLobby()
    {
        // See OpenMapWithMapLink for why Instance() can be null here. If the module does not exist
        // yet the game has not even reached the lobby, so report true: every caller uses this to
        // gate rendering of character data that is equally unavailable in that state.
        var raptureAtkModule = RaptureAtkModule.Instance();
        if (raptureAtkModule == null)
            return true;

        return raptureAtkModule->CurrentUIScene.StartsWith("LobbyMain"u8);
    }

    /// <summary>
    /// Sets the current background music.
    /// </summary>
    /// <param name="bgmId">The BGM row id.</param>
    /// <param name="sceneId">The BGM scene index. Defaults to MiniGame scene to avoid conflicts.</param>
    internal void SetBgm(ushort bgmId, uint sceneId = 2) => BGMSystem.SetBGM(bgmId, sceneId);

    /// <summary>
    /// Resets the current background music.
    /// </summary>
    /// <param name="sceneId">The BGM scene index.</param>
    internal void ResetBgm(uint sceneId = 2)
    {
        // BGMSystem.Instance() resolves a [StaticAddress(..., isPointer: true)] slot that is null until
        // the game has built the BGM system. ResetBGM is a [MemberFunction], so a null "this" is handed
        // straight to native code: an AccessViolationException that no try/catch can recover from.
        // Nothing can observe whether this ran (the method returns void and both callers ignore it),
        // so skipping quietly is the correct no-op.
        var bgmSystem = BGMSystem.Instance();
        if (bgmSystem == null)
            return;

        bgmSystem->ResetBGM(sceneId);
    }

    /// <summary>
    /// Reset the stored "UI hide" state.
    /// </summary>
    internal void ResetUiHideState()
    {
        this.GameUiHidden = false;
    }

    private void HandleItemHoverDetour(AgentItemDetail* thisPtr, uint frameCount)
    {
        this.handleItemHoverHook!.Original(thisPtr, frameCount);

        if (!thisPtr->IsAgentActive())
            return;

        var itemId = (ulong)thisPtr->ItemId;
        if (this.HoveredItem == itemId)
            return;

        this.HoveredItem = itemId;
        this.HoveredItemChanged?.InvokeSafely(this, itemId);
    }

    private AtkValue* HandleItemOutDetour(AgentItemDetail* thisPtr, AtkValue* returnValue, AtkValue* values, uint valueCount, ulong eventKind)
    {
        var ret = this.handleItemOutHook!.Original(thisPtr, returnValue, values, valueCount, eventKind);

        if (values != null && valueCount == 1 && values->Int == -1)
        {
            this.HoveredItem = 0;
            this.HoveredItemChanged?.InvokeSafely(this, 0ul);
        }

        return ret;
    }

    private void HandleActionHoverDetour(AgentActionDetail* hoverState, FFXIVClientStructs.FFXIV.Client.UI.Agent.ActionKind actionKind, uint actionId, int a4, bool a5, int a6, int a7)
    {
        this.handleActionHoverHook!.Original(hoverState, actionKind, actionId, a4, a5, a6, a7);
        this.HoveredAction.ActionKind = (HoverActionKind)actionKind;
        this.HoveredAction.BaseActionID = actionId;
        this.HoveredAction.ActionID = hoverState->ActionId;
        this.HoveredActionChanged?.InvokeSafely(this, this.HoveredAction);
    }

    private AtkValue* HandleActionOutDetour(AgentActionDetail* agentActionDetail, AtkValue* a2, AtkValue* a3, uint a4, ulong a5)
    {
        var retVal = this.handleActionOutHook!.Original(agentActionDetail, a2, a3, a4, a5);

        if (a3 != null && a4 == 1)
        {
            var a3Val = a3->Int;

            if (a3Val == 255)
            {
                this.HoveredAction.ActionKind = HoverActionKind.None;
                this.HoveredAction.BaseActionID = 0;
                this.HoveredAction.ActionID = 0;
                this.HoveredActionChanged?.InvokeSafely(this, this.HoveredAction);
            }
        }

        return retVal;
    }

    private void SetUiVisibilityDetour(RaptureAtkModule* thisPtr, bool uiVisible)
    {
        this.setUiVisibilityHook!.Original(thisPtr, uiVisible);

        try
        {
            // See OpenMapWithMapLink for why Instance() can be null here. Leave GameUiHidden at its
            // previous value and skip the event rather than dereferencing null; the next toggle will
            // resynchronise it.
            var raptureAtkModule = RaptureAtkModule.Instance();
            if (raptureAtkModule == null)
                return;

            this.GameUiHidden = !raptureAtkModule->IsUiVisible;
            this.UiHideToggled?.InvokeSafely(this, this.GameUiHidden);

            Log.Debug("GameUiHidden: {0}", this.GameUiHidden);
        }
        catch (Exception ex)
        {
            UiVisibilityFault.Report(ex);
        }
    }

    private char HandleImmDetour(IntPtr framework, char a2, byte a3)
    {
        var result = this.handleImmHook!.Original(framework, a2, a3);
        if (!ImGuiHelpers.IsImGuiInitialized)
            return result;

        return ImGui.GetIO().WantTextInput
                   ? (char)0
                   : result;
    }

    // The string is constructed exactly once: by Original, or for a null source by Ctor instead.
    private Utf8String* Utf8StringFromSequenceDetour(Utf8String* thisPtr, byte* sourcePtr, nuint sourceLen)
    {
        if (sourcePtr != null)
        {
            this.utf8StringFromSequenceHook.Original(thisPtr, sourcePtr, sourceLen);
            return thisPtr;
        }

        try
        {
            thisPtr->Ctor(); // this is in ClientStructs but you could do it manually too
        }
        catch (Exception ex)
        {
            // Ctor throws before touching the string, so it is still unconstructed. An empty sequence from a
            // valid pointer builds an empty string too, but reserving its terminator clears IsEmpty (+0x20) where
            // Ctor sets it; that is also the state the game's own empty sequences leave.
            Utf8StringFault.Report(ex);
            var empty = (byte)0;
            this.utf8StringFromSequenceHook.Original(thisPtr, &empty, 0);
        }

        return thisPtr; // this function shouldn't need to return but the original asm moves this into rax before returning so be safe?
    }

    private void RaptureAtkModuleUpdateDetour(RaptureAtkModule* thisPtr, float delta)
    {
        // The game clears the AgentUpdateFlag in the original function, but it also updates agents in it too.
        // We'll make a copy of the flags, so that we can fire events after the agents have been updated.

        var agentUpdateFlag = thisPtr->AgentUpdateFlag;

        this.raptureAtkModuleUpdateHook!.Original(thisPtr, delta);

        if (agentUpdateFlag != RaptureAtkModule.AgentUpdateFlags.None)
        {
            this.AgentUpdate.InvokeSafely((AgentUpdateFlag)agentUpdateFlag);
        }
    }

    /// <summary>The addresses of GameGui's optional hooks, 0 where left out.</summary>
    /// <param name="ItemHover">AgentItemDetail.Update.</param>
    /// <param name="ItemOut">AgentItemDetail.ReceiveEvent.</param>
    /// <param name="ActionHover">AgentActionDetail.HandleActionHover.</param>
    /// <param name="ActionOut">AgentActionDetail.ReceiveEvent.</param>
    /// <param name="Imm">HandleImm.</param>
    /// <param name="SetUiVisibility">RaptureAtkModule.SetUiVisibility.</param>
    /// <param name="UpdateSlot">RaptureAtkModule's vtable slot of Update.</param>
    internal readonly record struct HookAddresses(nint ItemHover, nint ItemOut, nint ActionHover, nint ActionOut, nint Imm, nint SetUiVisibility, nint UpdateSlot);
}

/// <summary>
/// Plugin-scoped version of a AddonLifecycle service.
/// </summary>
[PluginInterface]
[ServiceManager.ScopedService]
#pragma warning disable SA1015
[ResolveVia<IGameGui>]
#pragma warning restore SA1015
internal class GameGuiPluginScoped : IInternalDisposableService, IGameGui
{
    [ServiceManager.ServiceDependency]
    private readonly GameGui gameGuiService = Service<GameGui>.Get();

    /// <summary>
    /// Initializes a new instance of the <see cref="GameGuiPluginScoped"/> class.
    /// </summary>
    internal GameGuiPluginScoped()
    {
        this.gameGuiService.UiHideToggled += this.UiHideToggledForward;
        this.gameGuiService.HoveredItemChanged += this.HoveredItemForward;
        this.gameGuiService.HoveredActionChanged += this.HoveredActionForward;
        this.gameGuiService.AgentUpdate += this.AgentUpdateForward;
    }

    /// <inheritdoc/>
    public event EventHandler<bool>? UiHideToggled;

    /// <inheritdoc/>
    public event EventHandler<ulong>? HoveredItemChanged;

    /// <inheritdoc/>
    public event EventHandler<HoveredAction>? HoveredActionChanged;

    /// <inheritdoc/>
    public event Action<AgentUpdateFlag> AgentUpdate;

    /// <inheritdoc/>
    public bool GameUiHidden => this.gameGuiService.GameUiHidden;

    /// <inheritdoc/>
    public ulong HoveredItem
    {
        get => this.gameGuiService.HoveredItem;
        set => this.gameGuiService.HoveredItem = value;
    }

    /// <inheritdoc/>
    public HoveredAction HoveredAction => this.gameGuiService.HoveredAction;

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.gameGuiService.UiHideToggled -= this.UiHideToggledForward;
        this.gameGuiService.HoveredItemChanged -= this.HoveredItemForward;
        this.gameGuiService.HoveredActionChanged -= this.HoveredActionForward;
        this.gameGuiService.AgentUpdate -= this.AgentUpdateForward;

        this.UiHideToggled = null;
        this.HoveredItemChanged = null;
        this.HoveredActionChanged = null;
    }

    /// <inheritdoc/>
    public bool OpenMapWithMapLink(MapLinkPayload mapLink)
        => this.gameGuiService.OpenMapWithMapLink(mapLink);

    /// <inheritdoc/>
    public bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)
        => this.gameGuiService.WorldToScreen(worldPos, out screenPos);

    /// <inheritdoc/>
    public bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos, out bool inView)
        => this.gameGuiService.WorldToScreen(worldPos, out screenPos, out inView);

    /// <inheritdoc/>
    public bool ScreenToWorld(Vector2 screenPos, out Vector3 worldPos, float rayDistance = 100000)
        => this.gameGuiService.ScreenToWorld(screenPos, out worldPos, rayDistance);

    /// <inheritdoc/>
    public UIModulePtr GetUIModule()
        => this.gameGuiService.GetUIModule();

    /// <inheritdoc/>
    public AtkUnitBasePtr GetAddonByName(string name, int index = 1)
        => this.gameGuiService.GetAddonByName(name, index);

    /// <inheritdoc/>
    public unsafe T* GetAddonByName<T>(string name, int index = 1) where T : unmanaged
        => (T*)this.gameGuiService.GetAddonByName(name, index).Address;

    /// <inheritdoc/>
    public AgentInterfacePtr GetAgentById(int id)
        => this.gameGuiService.GetAgentById(id);

    /// <inheritdoc/>
    public AgentInterfacePtr FindAgentInterface(string addonName)
        => this.gameGuiService.FindAgentInterface(addonName);

    /// <inheritdoc/>
    public AgentInterfacePtr FindAgentInterface(AtkUnitBasePtr addon)
        => this.gameGuiService.FindAgentInterface(addon);

    private void UiHideToggledForward(object sender, bool toggled) => this.UiHideToggled?.Invoke(sender, toggled);

    private void HoveredItemForward(object sender, ulong itemId) => this.HoveredItemChanged?.Invoke(sender, itemId);

    private void HoveredActionForward(object sender, HoveredAction hoverAction) => this.HoveredActionChanged?.Invoke(sender, hoverAction);

    private void AgentUpdateForward(AgentUpdateFlag agentUpdateFlag) => this.AgentUpdate.InvokeSafely(agentUpdateFlag);
}
