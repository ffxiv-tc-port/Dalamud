using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using Dalamud.Hooking.Internal;
using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Dalamud.Utility.Internal;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Dalamud.Game.Addon.Lifecycle;

/// <summary>
/// This class provides events for in-game addon lifecycles.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal unsafe class AddonLifecycle : IInternalDisposableService
{
    private const string CallsitesDisabledChatLine = "Dalamud：遊戲改版後，介面視窗的{0}事件與 Dalamud 內建的遊戲結構對不上，插件生命週期事件的這部分先停用（遊戲本身照常，用到它的插件功能可能沒反應），等 Dalamud 更新。詳情請輸入 /xllog 查看。";

    private static readonly ModuleLog Log = new("AddonLifecycle");

    [ServiceManager.ServiceDependency]
    private readonly Framework framework = Service<Framework>.Get();

    [ServiceManager.ServiceDependency]
    private readonly AddonLifecyclePooledArgs argsPool = Service<AddonLifecyclePooledArgs>.Get();

    private readonly nint disallowedReceiveEventAddress;
    
    private readonly AddonLifecycleAddressResolver address;

    // Call site hooks are null when the game's instruction does not read ClientStructs' slot (see AddonLifecycleCallsiteCheck):
    // the game then calls the vfunc itself and plugins get no events for it.
    private readonly AddonSetupHook<AtkUnitBase.Delegates.OnSetup>? onAddonSetupHook;
    private readonly Hook<AddonFinalizeDelegate> onAddonFinalizeHook;
    private readonly CallHook<AtkUnitBase.Delegates.Draw>? onAddonDrawHook;
    private readonly CallHook<AtkUnitBase.Delegates.Update>? onAddonUpdateHook;
    private readonly Hook<AtkUnitManager.Delegates.RefreshAddon> onAddonRefreshHook;
    private readonly CallHook<AtkUnitBase.Delegates.OnRequestedUpdate>? onAddonRequestedUpdateHook;

    [ServiceManager.ServiceConstructor]
    private AddonLifecycle(TargetSigScanner sigScanner)
    {
        this.address = new AddonLifecycleAddressResolver();
        this.address.Setup(sigScanner);

        this.disallowedReceiveEventAddress = (nint)AtkUnitBase.StaticVirtualTablePointer->ReceiveEvent;

        var refreshAddonAddress = (nint)RaptureAtkUnitManager.StaticVirtualTablePointer->RefreshAddon;

        var verified = new List<string>();
        var disabled = new List<string>();
        bool Hookable(string name, string chatName, nint callsite, byte[] opcode, int slot)
        {
            if (CallsiteMatches(sigScanner, name, callsite, opcode, slot))
            {
                verified.Add($"{name} +0x{slot:X}");
                return true;
            }

            disabled.Add(chatName);
            return false;
        }

        this.onAddonSetupHook = Hookable("OnSetup", "開啟", this.address.AddonSetup, AddonLifecycleCallsiteCheck.MovR9RaxDisp32, AddonLifecycleCallsiteCheck.OnSetupSlot)
                                    ? new AddonSetupHook<AtkUnitBase.Delegates.OnSetup>(this.address.AddonSetup, this.OnAddonSetup)
                                    : null;
        this.onAddonFinalizeHook = Hook<AddonFinalizeDelegate>.FromAddress(this.address.AddonFinalize, this.OnAddonFinalize);
        this.onAddonDrawHook = Hookable("Draw", "繪製", this.address.AddonDraw, AddonLifecycleCallsiteCheck.CallRaxDisp32, AddonLifecycleCallsiteCheck.DrawSlot)
                                   ? new CallHook<AtkUnitBase.Delegates.Draw>(this.address.AddonDraw, this.OnAddonDraw)
                                   : null;
        this.onAddonUpdateHook = Hookable("Update", "更新", this.address.AddonUpdate, AddonLifecycleCallsiteCheck.CallRaxDisp32, AddonLifecycleCallsiteCheck.UpdateSlot)
                                     ? new CallHook<AtkUnitBase.Delegates.Update>(this.address.AddonUpdate, this.OnAddonUpdate)
                                     : null;
        this.onAddonRefreshHook = Hook<AtkUnitManager.Delegates.RefreshAddon>.FromAddress(refreshAddonAddress, this.OnAddonRefresh);
        this.onAddonRequestedUpdateHook = Hookable("OnRequestedUpdate", "資料更新", this.address.AddonOnRequestedUpdate, AddonLifecycleCallsiteCheck.CallRaxDisp32, AddonLifecycleCallsiteCheck.OnRequestedUpdateSlot)
                                              ? new CallHook<AtkUnitBase.Delegates.OnRequestedUpdate>(this.address.AddonOnRequestedUpdate, this.OnRequestedUpdate)
                                              : null;

        if (verified.Count > 0)
            Log.Information("Addon call sites agree with ClientStructs: {Sites}", string.Join(", ", verified));
        if (disabled.Count > 0)
            FeatureDisabledEcho.Report("AddonLifecycle.Callsites", string.Format(CallsitesDisabledChatLine, string.Join("、", disabled)));

        this.onAddonSetupHook?.Enable();
        this.onAddonFinalizeHook.Enable();
        this.onAddonDrawHook?.Enable();
        this.onAddonUpdateHook?.Enable();
        this.onAddonRefreshHook.Enable();
        this.onAddonRequestedUpdateHook?.Enable();
    }

    private delegate void AddonFinalizeDelegate(AtkUnitManager* unitManager, AtkUnitBase** atkUnitBase);

    /// <summary>
    /// Gets a list of all AddonLifecycle ReceiveEvent Listener Hooks.
    /// </summary>
    internal List<AddonLifecycleReceiveEventListener> ReceiveEventListeners { get; } = new();
    
    /// <summary>
    /// Gets a list of all AddonLifecycle Event Listeners.
    /// </summary>
    internal List<AddonLifecycleEventListener> EventListeners { get; } = new();

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.onAddonSetupHook?.Dispose();
        this.onAddonFinalizeHook.Dispose();
        this.onAddonDrawHook?.Dispose();
        this.onAddonUpdateHook?.Dispose();
        this.onAddonRefreshHook.Dispose();
        this.onAddonRequestedUpdateHook?.Dispose();

        foreach (var receiveEventListener in this.ReceiveEventListeners)
        {
            receiveEventListener.Dispose();
        }
    }

    /// <summary>
    /// Register a listener for the target event and addon.
    /// </summary>
    /// <param name="listener">The listener to register.</param>
    internal void RegisterListener(AddonLifecycleEventListener listener)
    {
        this.framework.RunOnTick(() =>
        {
            this.EventListeners.Add(listener);
            
            // If we want receive event messages have an already active addon, enable the receive event hook.
            // If the addon isn't active yet, we'll grab the hook when it sets up.
            if (listener is { EventType: AddonEvent.PreReceiveEvent or AddonEvent.PostReceiveEvent })
            {
                if (this.ReceiveEventListeners.FirstOrDefault(listeners => listeners.AddonNames.Contains(listener.AddonName)) is { } receiveEventListener)
                {
                    receiveEventListener.TryEnable();
                }
            }
        });
    }

    /// <summary>
    /// Unregisters the listener from events.
    /// </summary>
    /// <param name="listener">The listener to unregister.</param>
    internal void UnregisterListener(AddonLifecycleEventListener listener)
    {
        // Set removed state to true immediately, then lazily remove it from the EventListeners list on next Framework Update.
        listener.Removed = true;
        
        this.framework.RunOnTick(() =>
        {
            this.EventListeners.Remove(listener);
            
            // If we are disabling an ReceiveEvent listener, check if we should disable the hook.
            if (listener is { EventType: AddonEvent.PreReceiveEvent or AddonEvent.PostReceiveEvent })
            {
                // Get the ReceiveEvent Listener for this addon
                if (this.ReceiveEventListeners.FirstOrDefault(listeners => listeners.AddonNames.Contains(listener.AddonName)) is { } receiveEventListener)
                {
                    // If there are no other listeners listening for this event, disable the hook.
                    if (!this.EventListeners.Any(listeners => listeners.AddonName.Contains(listener.AddonName) && listener.EventType is AddonEvent.PreReceiveEvent or AddonEvent.PostReceiveEvent))
                    {
                        receiveEventListener.Disable();
                    }
                }
            }
        });
    }

    /// <summary>
    /// Invoke listeners for the specified event type.
    /// </summary>
    /// <param name="eventType">Event Type.</param>
    /// <param name="args">AddonArgs.</param>
    /// <param name="blame">What to blame on errors.</param>
    internal void InvokeListenersSafely(AddonEvent eventType, AddonArgs args, [CallerMemberName] string blame = "")
    {
        // Do not use linq; this is a high-traffic function, and more heap allocations avoided, the better.
        foreach (var listener in this.EventListeners)
        {
            if (listener.EventType != eventType)
                continue;

            // If the listener is pending removal, and is waiting until the next Framework Update, don't invoke listener.
            if (listener.Removed)
                continue;
            
            // Match on string.empty for listeners that want events for all addons.
            if (!string.IsNullOrWhiteSpace(listener.AddonName) && !args.IsAddon(listener.AddonName))
                continue;

            var meter = listener.AllocationMeter;
            var allocatedBefore = meter is null ? default : PluginAllocationMeter.Begin();
            try
            {
                listener.FunctionDelegate.Invoke(eventType, args);
            }
            catch (Exception e)
            {
                if (!UnresolvedAddressLog.Instance.TryAbsorb(e, meter?.PluginName, $"{eventType} listener in {blame}"))
                    Log.Error(e, $"Exception in {blame} during {eventType} invoke.");
            }

            meter?.End(allocatedBefore);
        }
    }

    // Reads the game's own code at the scan result (not the scanner's module copy): that is what the hook replaces.
    private static bool CallsiteMatches(TargetSigScanner scanner, string name, nint callsite, byte[] opcode, int slot)
    {
        bool matches;
        string evidence;
        try
        {
            var length = AddonLifecycleCallsiteCheck.InstructionLength(opcode);
            var rva = (long)callsite - (long)scanner.Module.BaseAddress;
            if (rva < scanner.TextSectionOffset || rva + length > scanner.TextSectionOffset + scanner.TextSectionSize)
            {
                matches = false;
                evidence = $"0x{callsite:X} is not inside the game's .text";
            }
            else
            {
                matches = AddonLifecycleCallsiteCheck.Matches(new ReadOnlySpan<byte>((void*)callsite, length), opcode, slot, out evidence);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not check the {Name} call site; not hooking it", name);
            return false;
        }

        if (!matches)
            Log.Error("Not hooking the {Name} call site at 0x{Address:X}: {Evidence}; plugins get none of its addon events until Dalamud is updated", name, callsite, evidence);

        return matches;
    }

    private void RegisterReceiveEventHook(AtkUnitBase* addon)
    {
        // Hook the addon's ReceiveEvent function here, but only enable the hook if we have an active listener.
        // Disallows hooking the core internal event handler.
        var addonName = NativeStringUtil.GetAddonName(addon);
        var receiveEventAddress = (nint)addon->VirtualTable->ReceiveEvent;
        if (receiveEventAddress != this.disallowedReceiveEventAddress)
        {
            // If we have a ReceiveEvent listener already made for this hook address, add this addon's name to that handler.
            if (this.ReceiveEventListeners.FirstOrDefault(listener => listener.FunctionAddress == receiveEventAddress) is { } existingListener)
            {
                if (!existingListener.AddonNames.Contains(addonName))
                {
                    existingListener.AddonNames.Add(addonName);
                }
            }

            // Else, we have an addon that we don't have the ReceiveEvent for yet, make it.
            else
            {
                this.ReceiveEventListeners.Add(new AddonLifecycleReceiveEventListener(this, addonName, receiveEventAddress));
            }

            // If we have an active listener for this addon already, we need to activate this hook.
            if (this.EventListeners.Any(listener => (listener.EventType is AddonEvent.PostReceiveEvent or AddonEvent.PreReceiveEvent) && listener.AddonName == addonName))
            {
                if (this.ReceiveEventListeners.FirstOrDefault(listener => listener.AddonNames.Contains(addonName)) is { } receiveEventListener)
                {
                    receiveEventListener.TryEnable();
                }
            }
        }
    }

    private void UnregisterReceiveEventHook(string addonName)
    {
        // Remove this addons ReceiveEvent Registration
        if (this.ReceiveEventListeners.FirstOrDefault(listener => listener.AddonNames.Contains(addonName)) is { } eventListener)
        {
            eventListener.AddonNames.Remove(addonName);

            // If there are no more listeners let's remove and dispose.
            if (eventListener.AddonNames.Count is 0)
            {
                this.ReceiveEventListeners.Remove(eventListener);
                eventListener.Dispose();
            }
        }
    }

    private void OnAddonSetup(AtkUnitBase* addon, uint valueCount, AtkValue* values)
    {
        try
        {
            this.RegisterReceiveEventHook(addon);
        }
        catch (Exception e)
        {
            Log.Error(e, "Exception in OnAddonSetup ReceiveEvent Registration.");
        }

        using var returner = this.argsPool.Rent(out AddonSetupArgs arg);
        arg.Clear();
        arg.Addon = (nint)addon;
        arg.AtkValueCount = valueCount;
        arg.AtkValues = (nint)values;
        this.InvokeListenersSafely(AddonEvent.PreSetup, arg);
        valueCount = arg.AtkValueCount;
        values = (AtkValue*)arg.AtkValues;

        try
        {
            addon->OnSetup(valueCount, values);
        }
        catch (Exception e)
        {
            Log.Error(e, "Caught exception when calling original AddonSetup. This may be a bug in the game or another plugin hooking this method.");
        }

        this.InvokeListenersSafely(AddonEvent.PostSetup, arg);
    }

    private void OnAddonFinalize(AtkUnitManager* unitManager, AtkUnitBase** atkUnitBase)
    {
        try
        {
            var addonName = NativeStringUtil.GetAddonName(atkUnitBase[0]);
            this.UnregisterReceiveEventHook(addonName);
        }
        catch (Exception e)
        {
            Log.Error(e, "Exception in OnAddonFinalize ReceiveEvent Removal.");
        }

        using var returner = this.argsPool.Rent(out AddonFinalizeArgs arg);
        arg.Clear();
        arg.Addon = (nint)atkUnitBase[0];
        this.InvokeListenersSafely(AddonEvent.PreFinalize, arg);

        try
        {
            this.onAddonFinalizeHook.Original(unitManager, atkUnitBase);
        }
        catch (Exception e)
        {
            Log.Error(e, "Caught exception when calling original AddonFinalize. This may be a bug in the game or another plugin hooking this method.");
        }
    }

    private void OnAddonDraw(AtkUnitBase* addon)
    {
        using var returner = this.argsPool.Rent(out AddonDrawArgs arg);
        arg.Clear();
        arg.Addon = (nint)addon;
        this.InvokeListenersSafely(AddonEvent.PreDraw, arg);

        try
        {
            addon->Draw();
        }
        catch (Exception e)
        {
            Log.Error(e, "Caught exception when calling original AddonDraw. This may be a bug in the game or another plugin hooking this method.");
        }

        this.InvokeListenersSafely(AddonEvent.PostDraw, arg);
    }

    private void OnAddonUpdate(AtkUnitBase* addon, float delta)
    {
        using var returner = this.argsPool.Rent(out AddonUpdateArgs arg);
        arg.Clear();
        arg.Addon = (nint)addon;
        arg.TimeDeltaInternal = delta;
        this.InvokeListenersSafely(AddonEvent.PreUpdate, arg);

        try
        {
            addon->Update(delta);
        }
        catch (Exception e)
        {
            Log.Error(e, "Caught exception when calling original AddonUpdate. This may be a bug in the game or another plugin hooking this method.");
        }

        this.InvokeListenersSafely(AddonEvent.PostUpdate, arg);
    }

    private bool OnAddonRefresh(AtkUnitManager* thisPtr, AtkUnitBase* addon, uint valueCount, AtkValue* values)
    {
        var result = false;

        using var returner = this.argsPool.Rent(out AddonRefreshArgs arg);
        arg.Clear();
        arg.Addon = (nint)addon;
        arg.AtkValueCount = valueCount;
        arg.AtkValues = (nint)values;
        this.InvokeListenersSafely(AddonEvent.PreRefresh, arg);
        valueCount = arg.AtkValueCount;
        values = (AtkValue*)arg.AtkValues;

        try
        {
            result = this.onAddonRefreshHook.Original(thisPtr, addon, valueCount, values);
        }
        catch (Exception e)
        {
            Log.Error(e, "Caught exception when calling original AddonRefresh. This may be a bug in the game or another plugin hooking this method.");
        }

        this.InvokeListenersSafely(AddonEvent.PostRefresh, arg);
        return result;
    }

    private void OnRequestedUpdate(AtkUnitBase* addon, NumberArrayData** numberArrayData, StringArrayData** stringArrayData)
    {
        using var returner = this.argsPool.Rent(out AddonRequestedUpdateArgs arg);
        arg.Clear();
        arg.Addon = (nint)addon;
        arg.NumberArrayData = (nint)numberArrayData;
        arg.StringArrayData = (nint)stringArrayData;
        this.InvokeListenersSafely(AddonEvent.PreRequestedUpdate, arg);
        numberArrayData = (NumberArrayData**)arg.NumberArrayData;
        stringArrayData = (StringArrayData**)arg.StringArrayData;

        try
        {
            addon->OnRequestedUpdate(numberArrayData, stringArrayData);
        }
        catch (Exception e)
        {
            Log.Error(e, "Caught exception when calling original AddonRequestedUpdate. This may be a bug in the game or another plugin hooking this method.");
        }

        this.InvokeListenersSafely(AddonEvent.PostRequestedUpdate, arg);
    }
}

/// <summary>
/// Plugin-scoped version of a AddonLifecycle service.
/// </summary>
[PluginInterface]
[ServiceManager.ScopedService]
#pragma warning disable SA1015
[ResolveVia<IAddonLifecycle>]
#pragma warning restore SA1015
internal class AddonLifecyclePluginScoped : IInternalDisposableService, IAddonLifecycle
{
    [ServiceManager.ServiceDependency]
    private readonly AddonLifecycle addonLifecycleService = Service<AddonLifecycle>.Get();

    private readonly List<AddonLifecycleEventListener> eventListeners = new();
    private readonly PluginAllocationMeter allocationMeter;

    /// <summary>
    /// Initializes a new instance of the <see cref="AddonLifecyclePluginScoped"/> class.
    /// </summary>
    /// <param name="plugin">The plugin this instance belongs to.</param>
    internal AddonLifecyclePluginScoped(LocalPlugin plugin)
    {
        this.allocationMeter = new PluginAllocationMeter(plugin.InternalName, PluginAllocationMeter.AddonEvents);
    }

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        foreach (var listener in this.eventListeners)
        {
            this.addonLifecycleService.UnregisterListener(listener);
        }

        this.allocationMeter.Retire();
    }

    /// <inheritdoc/>
    public void RegisterListener(AddonEvent eventType, IEnumerable<string> addonNames, IAddonLifecycle.AddonEventDelegate handler)
    {
        foreach (var addonName in addonNames)
        {
            this.RegisterListener(eventType, addonName, handler);
        }
    }

    /// <inheritdoc/>
    public void RegisterListener(AddonEvent eventType, string addonName, IAddonLifecycle.AddonEventDelegate handler)
    {
        var listener = new AddonLifecycleEventListener(eventType, addonName, handler) { AllocationMeter = this.allocationMeter };
        this.eventListeners.Add(listener);
        this.addonLifecycleService.RegisterListener(listener);
    }

    /// <inheritdoc/>
    public void RegisterListener(AddonEvent eventType, IAddonLifecycle.AddonEventDelegate handler)
    {
        this.RegisterListener(eventType, string.Empty, handler);
    }

    /// <inheritdoc/>
    public void UnregisterListener(AddonEvent eventType, IEnumerable<string> addonNames, IAddonLifecycle.AddonEventDelegate? handler = null)
    {
        foreach (var addonName in addonNames)
        {
            this.UnregisterListener(eventType, addonName, handler);
        }
    }

    /// <inheritdoc/>
    public void UnregisterListener(AddonEvent eventType, string addonName, IAddonLifecycle.AddonEventDelegate? handler = null)
    {
        this.eventListeners.RemoveAll(entry =>
        {
            if (entry.EventType != eventType) return false;
            if (entry.AddonName != addonName) return false;
            if (handler is not null && entry.FunctionDelegate != handler) return false;

            this.addonLifecycleService.UnregisterListener(entry);
            return true;
        });
    }

    /// <inheritdoc/>
    public void UnregisterListener(AddonEvent eventType, IAddonLifecycle.AddonEventDelegate? handler = null)
    {
        this.UnregisterListener(eventType, string.Empty, handler);
    }

    /// <inheritdoc/>
    public void UnregisterListener(params IAddonLifecycle.AddonEventDelegate[] handlers)
    {
        foreach (var handler in handlers)
        {
            this.eventListeners.RemoveAll(entry =>
            {
                if (entry.FunctionDelegate != handler) return false;
            
                this.addonLifecycleService.UnregisterListener(entry);
                return true;
            });
        }
    }
}
