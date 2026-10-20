using CheapLoc;

using Dalamud.Configuration.Internal;
using Dalamud.Game.Text;
using Dalamud.Hooking;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Windowing;
using Dalamud.Logging.Internal;
using Dalamud.Utility;
using Dalamud.Utility.Internal;

using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

using ValueType = FFXIVClientStructs.FFXIV.Component.GUI.ValueType;

namespace Dalamud.Game.Internal;

/// <summary>
/// This class implements in-game Dalamud options in the in-game System menu.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal sealed unsafe class DalamudAtkTweaks : IInternalDisposableService
{
    private const string SystemMenuFeature = "ESC 系統選單裡的 Dalamud 項目";

    private static readonly ModuleLog Log = new("DalamudAtkTweaks");

    private static readonly DetourFault SystemMenuFault = new("DalamudAtkTweaks.OpenSystemMenu", "在系統選單加入 Dalamud 項目");

    // Null when the address is missing or misplaced (see FindHookTargets).
    private readonly Hook<AgentHUD.Delegates.OpenSystemMenu>? hookAgentHudOpenSystemMenu;

    // TODO: Make this into events in Framework.Gui
    private readonly Hook<UIModule.Delegates.ExecuteMainCommand>? hookUiModuleExecuteMainCommand;

    private readonly Hook<AtkUnitBase.Delegates.ReceiveGlobalEvent>? hookAtkUnitBaseReceiveGlobalEvent;

    [ServiceManager.ServiceDependency]
    private readonly DalamudConfiguration configuration = Service<DalamudConfiguration>.Get();

    // [ServiceManager.ServiceDependency]
    // private readonly ContextMenu contextMenu = Service<ContextMenu>.Get();

    private readonly string locDalamudPlugins;
    private readonly string locDalamudSettings;

    private bool disposed = false;

    [ServiceManager.ServiceConstructor]
    private DalamudAtkTweaks(TargetSigScanner sigScanner)
    {
        var targets = HookTargets.For(nameof(DalamudAtkTweaks), sigScanner);
        var (openSystemMenu, executeMainCommand, receiveGlobalEvent) = FindHookTargets(targets);
        this.hookAgentHudOpenSystemMenu = openSystemMenu != 0 ? Hook<AgentHUD.Delegates.OpenSystemMenu>.FromAddress(openSystemMenu, this.AgentHudOpenSystemMenuDetour) : null;
        this.hookUiModuleExecuteMainCommand = executeMainCommand != 0 ? Hook<UIModule.Delegates.ExecuteMainCommand>.FromAddress(executeMainCommand, this.UiModuleExecuteMainCommandDetour) : null;
        this.hookAtkUnitBaseReceiveGlobalEvent = receiveGlobalEvent != 0 ? Hook<AtkUnitBase.Delegates.ReceiveGlobalEvent>.FromAddress(receiveGlobalEvent, this.AtkUnitBaseReceiveGlobalEventDetour) : null;
        targets.ReportDisabled();

        this.locDalamudPlugins = Loc.Localize("SystemMenuPlugins", "Dalamud Plugins");
        this.locDalamudSettings = Loc.Localize("SystemMenuSettings", "Dalamud Settings");

        // this.contextMenu.ContextMenuOpened += this.ContextMenuOnContextMenuOpened;

        this.hookAgentHudOpenSystemMenu?.Enable();
        this.hookUiModuleExecuteMainCommand?.Enable();
        this.hookAtkUnitBaseReceiveGlobalEvent?.Enable();
    }

    /// <summary>Finalizes an instance of the <see cref="DalamudAtkTweaks"/> class.</summary>
    ~DalamudAtkTweaks() => this.Dispose(false);

    private delegate void AgentHudOpenSystemMenuPrototype(AgentHUD* thisPtr, AtkValue* atkValueArgs, uint menuSize);

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService() => this.Dispose(true);

    /// <summary>
    /// Checks the addresses of this service's hooks. The two system menu hooks are kept together: the menu entries
    /// alone would send the game main command ids it does not have.
    /// </summary>
    /// <param name="targets">The checks.</param>
    /// <returns>OpenSystemMenu, ExecuteMainCommand and ReceiveGlobalEvent, 0 where left out.</returns>
    internal static (nint OpenSystemMenu, nint ExecuteMainCommand, nint ReceiveGlobalEvent) FindHookTargets(HookTargets targets)
    {
        var uiModule = targets.VirtualTable(UIModule.StaticVirtualTablePointer);
        var (openSystemMenu, executeMainCommand) = DecideSystemMenu(
            targets,
            targets.Function("AgentHUD.OpenSystemMenu", SystemMenuFeature, AgentHUD.Addresses.OpenSystemMenu.Value),
            targets.Slot("UIModule.ExecuteMainCommand", SystemMenuFeature, uiModule, uiModule == null ? 0 : (nint)uiModule->ExecuteMainCommand));

        var unitBase = targets.VirtualTable(AtkUnitBase.StaticVirtualTablePointer);
        var receiveGlobalEvent = targets.Slot(
            "AtkUnitBase.ReceiveGlobalEvent",
            "Dalamud 視窗開著時按 ESC 不關遊戲視窗",
            unitBase,
            unitBase == null ? 0 : (nint)unitBase->ReceiveGlobalEvent);

        return (openSystemMenu, executeMainCommand, receiveGlobalEvent);
    }

    /// <summary>
    /// Keeps the system menu hooks only as a pair.
    /// </summary>
    /// <param name="targets">The checks.</param>
    /// <param name="openSystemMenu">The checked OpenSystemMenu address, 0 if left out.</param>
    /// <param name="executeMainCommand">The checked ExecuteMainCommand address, 0 if left out.</param>
    /// <returns>Both addresses, or both 0.</returns>
    internal static (nint OpenSystemMenu, nint ExecuteMainCommand) DecideSystemMenu(HookTargets targets, nint openSystemMenu, nint executeMainCommand)
        => targets.AllOrNone(SystemMenuFeature, ("AgentHUD.OpenSystemMenu", openSystemMenu), ("UIModule.ExecuteMainCommand", executeMainCommand))
               ? (openSystemMenu, executeMainCommand)
               : (0, 0);

    // NUL-terminated: SetManagedString reads up to the first NUL, and the builder's view excludes its terminator.
    private static byte[] BuildEntryLabel(string text)
    {
        const int color = 539;

        using var rssb = new RentedSeStringBuilder();
        var view = rssb.Builder
                       .PushColorType(color)
                       .Append($"{SeIconChar.BoxedLetterD.ToIconString()} ")
                       .PopColorType()
                       .Append(text)
                       .GetViewAsSpan();

        var label = new byte[view.Length + 1];
        view.CopyTo(label);
        return label;
    }

    private void Dispose(bool disposing)
    {
        if (this.disposed)
            return;

        if (disposing)
        {
            this.hookAgentHudOpenSystemMenu?.Dispose();
            this.hookUiModuleExecuteMainCommand?.Dispose();
            this.hookAtkUnitBaseReceiveGlobalEvent?.Dispose();

            // this.contextMenu.ContextMenuOpened -= this.ContextMenuOnContextMenuOpened;
        }

        this.disposed = true;
    }

    /*
    private void ContextMenuOnContextMenuOpened(ContextMenuOpenedArgs args)
    {
        var systemText = Service<DataManager>.GetNullable()?.GetExcelSheet<Addon>()?.GetRow(1059)?.Text?.RawString; // "System"
        var interfaceManager = Service<InterfaceManager>.GetNullable();

        if (systemText == null || interfaceManager == null)
            return;

        if (args.Title == systemText && this.configuration.DoButtonsSystemMenu && interfaceManager.IsDispatchingEvents)
        {
            var dalamudInterface = Service<DalamudInterface>.Get();

            args.Items.Insert(0, new CustomContextMenuItem(this.locDalamudSettings, selectedArgs =>
            {
                dalamudInterface.ToggleSettingsWindow();
            }));

            args.Items.Insert(0, new CustomContextMenuItem(this.locDalamudPlugins, selectedArgs =>
            {
                dalamudInterface.TogglePluginInstallerWindow();
            }));
        }
    }
    */

    private void AtkUnitBaseReceiveGlobalEventDetour(AtkUnitBase* thisPtr, AtkEventType eventType, int eventParam, AtkEvent* atkEvent, AtkEventData* atkEventData)
    {
        // 3 == Close
        if (eventType == AtkEventType.InputReceived && WindowSystem.ShouldInhibitAtkCloseEvents && atkEventData != null && *(int*)atkEventData == 3 && this.configuration.IsFocusManagementEnabled)
        {
            Log.Verbose($"Cancelling global event SendHotkey command due to WindowSystem {WindowSystem.FocusedWindowSystemNamespace}");
            return;
        }

        this.hookAtkUnitBaseReceiveGlobalEvent!.Original(thisPtr, eventType, eventParam, atkEvent, atkEventData);
    }

    // Original is called exactly once, outside the try block, unless focus management cancels the menu on purpose.
    private void AgentHudOpenSystemMenuDetour(AgentHUD* thisPtr, AtkValue* atkValueArgs, uint menuSize)
    {
        var sizeToOpen = menuSize;
        try
        {
            if (WindowSystem.ShouldInhibitAtkCloseEvents && this.configuration.IsFocusManagementEnabled)
            {
                Log.Verbose($"Cancelling OpenSystemMenu due to WindowSystem {WindowSystem.FocusedWindowSystemNamespace}");
                return;
            }

            sizeToOpen = this.AddDalamudEntries(atkValueArgs, menuSize);
        }
        catch (Exception ex)
        {
            SystemMenuFault.Report(ex);
        }

        this.hookAgentHudOpenSystemMenu!.Original(thisPtr, atkValueArgs, sizeToOpen);
    }

    // Returns the size to open the menu with: menuSize when nothing was added. Everything that can throw runs before
    // the first write to the game's values, so a failure leaves them untouched.
    private uint AddDalamudEntries(AtkValue* atkValueArgs, uint menuSize)
    {
        var interfaceManager = Service<InterfaceManager>.GetNullable();
        if (interfaceManager == null)
            return menuSize;

        if (!this.configuration.DoButtonsSystemMenu || !interfaceManager.IsDispatchingEvents)
            return menuSize;

        // the max size (hardcoded) is 0x12/18, but the system menu currently uses 0xC/12
        // this is a just in case that doesnt really matter
        // see if we can add 2 entries
        if (menuSize >= 0x12)
            return menuSize;

        if (AtkValue.MemberFunctionPointers.ChangeType == null || AtkValue.MemberFunctionPointers.SetManagedString == null)
        {
            SystemMenuFault.Report("AtkValue.ChangeType or AtkValue.SetManagedString is not resolved");
            return menuSize;
        }

        var pluginsLabel = BuildEntryLabel(this.locDalamudPlugins);
        var settingsLabel = BuildEntryLabel(this.locDalamudSettings);

        // atkValueArgs is actually an array of AtkValues used as args. all their UI code works like this.
        // in this case, menu size is stored in atkValueArgs[4], and the next 17 slots are the MainCommand
        // the 17 slots after that, if they exist, are the entry names, but they are otherwise pulled from MainCommand EXD
        // reference the original function for more details :)

        // step 1) move all the current menu items down so we can put Dalamud at the top like it deserves
        (&atkValueArgs[menuSize + 5])->ChangeType(ValueType.Int); // currently this value has no type, set it to int
        (&atkValueArgs[menuSize + 5 + 1])->ChangeType(ValueType.Int);

        for (var i = menuSize + 2; i > 1; i--)
        {
            var curEntry = &atkValueArgs[i + 5 - 2];
            var nextEntry = &atkValueArgs[i + 5];

            nextEntry->Int = curEntry->Int;
        }

        // step 2) set our new entries to dummy commands
        var firstEntry = &atkValueArgs[5];
        firstEntry->Int = 69420;
        var secondEntry = &atkValueArgs[6];
        secondEntry->Int = 69421;

        // step 3) create strings for them
        // since the game first checks for strings in the AtkValue argument before pulling them from the exd, if we create strings we dont have to worry
        // about hooking the exd reader, thank god
        var firstStringEntry = &atkValueArgs[5 + 18];
        firstStringEntry->ChangeType(ValueType.String);

        var secondStringEntry = &atkValueArgs[6 + 18];
        secondStringEntry->ChangeType(ValueType.String);

        firstStringEntry->SetManagedString(pluginsLabel);
        secondStringEntry->SetManagedString(settingsLabel);

        // open menu with new size
        var sizeEntry = &atkValueArgs[4];
        sizeEntry->UInt = menuSize + 2;

        return menuSize + 2;
    }

    private unsafe void UiModuleExecuteMainCommandDetour(UIModule* thisPtr, uint commandId)
    {
        var dalamudInterface = Service<DalamudInterface>.GetNullable();

        switch (commandId)
        {
            case 69420:
                dalamudInterface?.OpenPluginInstaller();
                break;
            case 69421:
                dalamudInterface?.OpenSettings();
                break;
            default:
                this.hookUiModuleExecuteMainCommand!.Original(thisPtr, commandId);
                break;
        }
    }
}
