#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Dalamud.Data;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.Game.Gui.Toast;
using Dalamud.Game.Internal;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.IoC.Internal;
using Dalamud.Utility.Internal;

using Lumina.Text.ReadOnly;

using Xunit;

using Utf8String = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String;

namespace Dalamud.Test.Game
{
    // Dalamud's own detours must call the game's Original exactly once (or, where they deliberately take over the
    // call, never), also when their own handling throws. Outside the game every ClientStructs address is unresolved,
    // so CS calls throw exactly like they do on a client whose signatures stopped matching; other failures are forced
    // through plugin handlers. Services are built uninitialized with only the fields each detour touches.
    public sealed class DetourOriginalOnceTests : IDisposable
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly List<nint> blocks = [];

        public DetourOriginalOnceTests()
        {
            // Reporting a fault looks up the Framework service, whose locator entry waits for the container.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        public void Dispose()
        {
            foreach (var block in this.blocks)
                Marshal.FreeHGlobal(block);
        }

        [Fact]
        public void OriginalCallSitesSitWhereTheDesignSays()
        {
            // O = outside every try with a catch, T = inside one, C = in a catch block, F = in a finally block.
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(ContextMenu), "AtkModuleVf22OpenAddonByAgentDetour"));
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(ContextMenu), "AddonContextMenuOnMenuSelectedDetour"));
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(DalamudAtkTweaks), "AgentHudOpenSystemMenuDetour"));
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(DalamudCompletion), "OpenCompletionCoreDetour"));
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(DalamudCompletion), "OpenSuggestionsDetour"));
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(DalamudCompletion), "GetSelectionDetour"));
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(GameGui), "SetUiVisibilityDetour"));
            Assert.Equal("O,C", DetourHarness.OriginalCallSites(typeof(GameGui), "Utf8StringFromSequenceDetour"));
            Assert.Equal("O,O", DetourHarness.OriginalCallSites(typeof(ToastGui), "HandleNormalToastDetour"));
            Assert.Equal("O,O", DetourHarness.OriginalCallSites(typeof(ToastGui), "HandleQuestToastDetour"));
            Assert.Equal("O,O", DetourHarness.OriginalCallSites(typeof(ToastGui), "HandleErrorToastDetour"));
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(ChatGui), "HandlePrintMessageDetour"));
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(AddonEventManager), "UpdateCursorDetour"));
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(CommandManager), "OnTryInvokeDebugCommand"));
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(RsvResolver), "AddRsvStringDetour"));

            // Flag-guarded: every call in the try sets the flag first; the catch / finally call only while it is unset.
            // NamePlate wraps each call, also the one in its finally block, in a try that only logs.
            Assert.Equal("T,T,C", DetourHarness.OriginalCallSites(typeof(FlyTextGui), "CreateFlyTextDetour"));
            Assert.Equal("T,T,T,FT", DetourHarness.OriginalCallSites(typeof(NamePlateGui), "OnRequestedUpdateDetour"));
        }

        [Fact]
        public void ContextMenuOpenFallsBackToTheGamesOwnValues()
        {
            var menu = this.NewContextMenu();
            var original = DetourHarness.InstallFakeHook(menu, "atkModuleVf22OpenAddonByAgentHook", (ushort)7);
            var name = this.Text("ContextMenu");
            var values = this.Track(DetourHarness.Zeroed(16 * 8));
            var faults = Faults(typeof(ContextMenu), "OpenAddonFault");

            // RaptureAtkUnitManager.Instance() throws: the resolver never ran.
            var ret = DetourHarness.Invoke(menu, "AtkModuleVf22OpenAddonByAgentDetour", (nint)0x1000, name, 8, values, nint.Zero, (nint)5, true);

            Assert.Equal((ushort)7, ret);
            var call = Assert.Single(original.Calls);
            Assert.Equal(name, call[1]);
            Assert.Equal(8, call[2]);
            Assert.Equal(values, call[3]);
            Assert.Equal(faults + 1, Faults(typeof(ContextMenu), "OpenAddonFault"));
        }

        [Fact]
        public void ContextMenuOpenOfAnotherAddonCallsOriginalOnce()
        {
            var menu = this.NewContextMenu();
            var original = DetourHarness.InstallFakeHook(menu, "atkModuleVf22OpenAddonByAgentHook");
            var faults = Faults(typeof(ContextMenu), "OpenAddonFault");

            DetourHarness.Invoke(menu, "AtkModuleVf22OpenAddonByAgentDetour", nint.Zero, this.Text("AddonContextMenuTitle"), 3, nint.Zero, nint.Zero, nint.Zero, false);

            Assert.Single(original.Calls);
            Assert.Equal(faults, Faults(typeof(ContextMenu), "OpenAddonFault"));
        }

        [Fact]
        public void ContextMenuNativeSelectionCallsOriginalOnceWithTheNativeIndex()
        {
            var menu = this.NewContextMenu();
            var original = DetourHarness.InstallFakeHook(menu, "addonContextMenuOnMenuSelectedHook", true);
            SetProperty(menu, "SelectedItems", new List<IMenuItem>());
            GetProperty<List<int>>(menu, "MenuCallbackIds").AddRange([-1, -2]);

            var ret = DetourHarness.Invoke(menu, "AddonContextMenuOnMenuSelectedDetour", this.Track(DetourHarness.Zeroed(0x400)), 1, (byte)0);

            Assert.True((bool)ret!);
            var call = Assert.Single(original.Calls);
            Assert.Equal(1, call[1]);
        }

        [Fact]
        public void ContextMenuDalamudItemNeverReachesTheGameEvenWhenClosingFails()
        {
            var menu = this.NewContextMenu();
            var original = DetourHarness.InstallFakeHook(menu, "addonContextMenuOnMenuSelectedHook", true);
            var clicks = 0;
            SetProperty(menu, "SelectedItems", new List<IMenuItem> { new MenuItem { Name = "x", OnClicked = _ => clicks++ } });
            GetProperty<List<int>>(menu, "MenuCallbackIds").Add(0);
            var faults = Faults(typeof(ContextMenu), "MenuSelectedFault");

            // Closing the menu calls AtkUnitBase.FireCallbackInt, which throws: the resolver never ran.
            var ret = DetourHarness.Invoke(menu, "AddonContextMenuOnMenuSelectedDetour", this.Track(DetourHarness.Zeroed(0x400)), 0, (byte)0);

            Assert.False((bool)ret!);
            Assert.Equal(1, clicks);
            Assert.Empty(original.Calls);
            Assert.Equal(faults + 1, Faults(typeof(ContextMenu), "MenuSelectedFault"));
        }

        [Fact]
        public void SystemMenuWithoutInterfaceOpensUnchangedOnce()
        {
            var tweaks = (DalamudAtkTweaks)RuntimeHelpers.GetUninitializedObject(typeof(DalamudAtkTweaks));
            var original = DetourHarness.InstallFakeHook(tweaks, "hookAgentHudOpenSystemMenu");
            var values = this.Track(DetourHarness.Zeroed(16 * 48));

            DetourHarness.Invoke(tweaks, "AgentHudOpenSystemMenuDetour", nint.Zero, values, 12u);

            var call = Assert.Single(original.Calls);
            Assert.Equal(values, call[1]);
            Assert.Equal(12u, call[2]);
            Assert.All(Read(values, 16 * 48), b => Assert.Equal(0, b));
        }

        [Fact]
        public void CompletionOpenCallsOriginalOnceWhenUpdatingFails()
        {
            var completion = (DalamudCompletion)RuntimeHelpers.GetUninitializedObject(typeof(DalamudCompletion));
            var core = DetourHarness.InstallFakeHook(completion, "openCompletionCoreHook", true);
            var suggestions = DetourHarness.InstallFakeHook(completion, "openSuggestionsHook");
            var faults = Faults(typeof(DalamudCompletion), "UpdateFault");

            // Unverified layout: nothing to do.
            Assert.True((bool)DetourHarness.Invoke(completion, "OpenCompletionCoreDetour", nint.Zero, nint.Zero, nint.Zero)!);
            Assert.Single(core.Calls);
            Assert.Equal(faults, Faults(typeof(DalamudCompletion), "UpdateFault"));

            // Verified layout: RaptureAtkModule.Instance() throws.
            DetourHarness.SetField(completion, "layoutVerified", true);
            Assert.True((bool)DetourHarness.Invoke(completion, "OpenCompletionCoreDetour", nint.Zero, nint.Zero, nint.Zero)!);
            DetourHarness.Invoke(completion, "OpenSuggestionsDetour", nint.Zero);

            Assert.Equal(2, core.Calls.Count);
            Assert.Single(suggestions.Calls);
            Assert.Equal(faults + 2, Faults(typeof(DalamudCompletion), "UpdateFault"));
        }

        [Fact]
        public void CompletionSelectionCallsOriginalOnceWhenInsertingFails()
        {
            var completion = (DalamudCompletion)RuntimeHelpers.GetUninitializedObject(typeof(DalamudCompletion));
            var selection = DetourHarness.InstallFakeHook(completion, "getSelectionHook", -2);
            DetourHarness.SetField(completion, "displayToCommand", new Dictionary<string, string> { ["/xlhelp"] = "/xlhelp" });
            var faults = Faults(typeof(DalamudCompletion), "SelectionFault");

            // A Dalamud entry was picked; finding the chat input calls RaptureAtkModule.Instance(), which throws.
            var ret = DetourHarness.Invoke(completion, "GetSelectionDetour", nint.Zero, nint.Zero, 0, this.Utf8("/xlhelp"), nint.Zero);

            Assert.Equal(-2, ret);
            Assert.Single(selection.Calls);
            Assert.Equal(faults + 1, Faults(typeof(DalamudCompletion), "SelectionFault"));
        }

        [Fact]
        public void NamePlateCallsOriginalOnceWhenUpdatingFails()
        {
            var plates = (NamePlateGui)RuntimeHelpers.GetUninitializedObject(typeof(NamePlateGui));
            var original = DetourHarness.InstallFakeHook(plates, "onRequestedUpdateHook");
            var faults = Faults(typeof(NamePlateGui), "UpdateFault");

            // Nobody listening: early return.
            DetourHarness.Invoke(plates, "OnRequestedUpdateDetour", nint.Zero, nint.Zero, nint.Zero);
            Assert.Single(original.Calls);

            // Building the update context calls RaptureAtkModule.Instance(), which throws.
            plates.OnNamePlateUpdate += (_, _) => { };
            DetourHarness.Invoke(plates, "OnRequestedUpdateDetour", nint.Zero, nint.Zero, nint.Zero);

            Assert.Equal(2, original.Calls.Count);
            Assert.Equal(faults + 1, Faults(typeof(NamePlateGui), "UpdateFault"));
        }

        [Fact]
        public void UiVisibilityCallsOriginalOnceBeforeItsOwnFailure()
        {
            var gui = (GameGui)RuntimeHelpers.GetUninitializedObject(typeof(GameGui));
            var original = DetourHarness.InstallFakeHook(gui, "setUiVisibilityHook");
            var faults = Faults(typeof(GameGui), "UiVisibilityFault");

            DetourHarness.Invoke(gui, "SetUiVisibilityDetour", (nint)0x1000, true);

            var call = Assert.Single(original.Calls);
            Assert.True((bool)call[1]!);
            Assert.Equal(faults + 1, Faults(typeof(GameGui), "UiVisibilityFault"));
        }

        [Fact]
        public void Utf8StringFromNullSequenceIsConstructedOnceFromAnEmptySequence()
        {
            var gui = (GameGui)RuntimeHelpers.GetUninitializedObject(typeof(GameGui));
            var original = DetourHarness.InstallFakeHook(gui, "utf8StringFromSequenceHook");
            var target = this.Track(DetourHarness.Zeroed(Unsafe.SizeOf<Utf8String>()));
            var source = this.Text("abc");

            // A real source goes straight to Original.
            Assert.Equal(target, DetourHarness.Invoke(gui, "Utf8StringFromSequenceDetour", target, source, (nuint)3));
            Assert.Equal(source, Assert.Single(original.Calls)[1]);

            // No source: Utf8String.Ctor throws (the resolver never ran), so Original builds it from an empty sequence.
            Assert.Equal(target, DetourHarness.Invoke(gui, "Utf8StringFromSequenceDetour", target, nint.Zero, (nuint)0));
            Assert.Equal(2, original.Calls.Count);
            Assert.NotEqual(nint.Zero, original.Calls[1][1]);
            Assert.Equal((nuint)0, original.Calls[1][2]);
        }

        [Fact]
        public void ToastsCallOriginalOnceAlsoWhenTheirOwnHandlingFails()
        {
            var toasts = (ToastGui)RuntimeHelpers.GetUninitializedObject(typeof(ToastGui));
            var normal = DetourHarness.InstallFakeHook(toasts, "showNormalToastHook");
            var quest = DetourHarness.InstallFakeHook(toasts, "showQuestToastHook");
            var error = DetourHarness.InstallFakeHook(toasts, "showErrorToastHook");
            var text = this.Text("hello");
            var module = (nint)0x1000;

            // Normal path: re-encoded text. The quest toast also looks up UIModule.Instance(), which throws here,
            // so it falls back to the game's own call (before, that exception escaped into the game).
            DetourHarness.Invoke(toasts, "HandleNormalToastDetour", module, text, 5, (byte)1, (byte)0, 0u);
            DetourHarness.Invoke(toasts, "HandleQuestToastDetour", module, 0, text, 0u, (byte)0, 0u, (byte)0);
            DetourHarness.Invoke(toasts, "HandleErrorToastDetour", module, text, (byte)0);
            Assert.Single(normal.Calls);
            Assert.Equal(new object?[] { module, 0, text, 0u, (byte)0, 0u, (byte)0 }, Assert.Single(quest.Calls));
            Assert.Single(error.Calls);

            // A handler leaves Dalamud nothing it can encode: the game's own arguments go through, once.
            toasts.Toast += (ref SeString _, ref ToastOptions options, ref bool _) => options = null!;
            toasts.QuestToast += (ref SeString _, ref QuestToastOptions options, ref bool _) => options = null!;
            toasts.ErrorToast += (ref SeString message, ref bool _) => message = null!;
            DetourHarness.Invoke(toasts, "HandleNormalToastDetour", module, text, 5, (byte)1, (byte)0, 0u);
            DetourHarness.Invoke(toasts, "HandleQuestToastDetour", module, 0, text, 7u, (byte)1, 0u, (byte)1);
            DetourHarness.Invoke(toasts, "HandleErrorToastDetour", module, text, (byte)1);

            Assert.Equal(2, normal.Calls.Count);
            Assert.Equal(text, normal.Calls[1][1]);
            Assert.Equal(2, quest.Calls.Count);
            Assert.Equal(new object?[] { module, 0, text, 7u, (byte)1, 0u, (byte)1 }, quest.Calls[1]);
            Assert.Equal(2, error.Calls.Count);
            Assert.Equal(text, error.Calls[1][1]);

            // Taken by a handler: deliberately never shown.
            toasts.ErrorToast += (ref SeString _, ref bool handled) => handled = true;
            DetourHarness.Invoke(toasts, "HandleErrorToastDetour", module, text, (byte)1);
            Assert.Equal(2, error.Calls.Count);
        }

        [Fact]
        public void ChatPrintsOnceWhenAnUnhandledListenerThrows()
        {
            var chat = (ChatGui)RuntimeHelpers.GetUninitializedObject(typeof(ChatGui));
            var original = DetourHarness.InstallFakeHook(chat, "printMessageHook", 42u);
            var unhandledCalls = 0;
            chat.ChatMessageUnhandled += (_, _, _, _) => throw new InvalidOperationException("plugin bug");
            chat.ChatMessageUnhandled += (_, _, _, _) => unhandledCalls++;

            var ret = DetourHarness.Invoke(chat, "HandlePrintMessageDetour", nint.Zero, XivChatType.Echo, this.Utf8("me"), this.Utf8("hi"), 0, (byte)0);

            // Before the fix the throwing listener sent the outer catch into a second Original call.
            Assert.Equal(42u, ret);
            Assert.Single(original.Calls);
            Assert.Equal(1, unhandledCalls);
        }

        [Fact]
        public void ChatHandledMessageStaysHiddenEvenWhenAHandledListenerThrows()
        {
            var chat = (ChatGui)RuntimeHelpers.GetUninitializedObject(typeof(ChatGui));
            var original = DetourHarness.InstallFakeHook(chat, "printMessageHook", 42u);
            chat.CheckMessageHandled += (XivChatType _, int _, ref SeString _, ref SeString _, ref bool handled) => handled = true;
            chat.ChatMessageHandled += (_, _, _, _) => throw new InvalidOperationException("plugin bug");

            var ret = DetourHarness.Invoke(chat, "HandlePrintMessageDetour", nint.Zero, XivChatType.Echo, this.Utf8("me"), this.Utf8("hi"), 0, (byte)0);

            Assert.Equal(0u, ret);
            Assert.Empty(original.Calls);
        }

        [Fact]
        public void ChatPrintsTheGamesMessageOnceWhenDalamudCannotEncodeIt()
        {
            var chat = (ChatGui)RuntimeHelpers.GetUninitializedObject(typeof(ChatGui));
            var original = DetourHarness.InstallFakeHook(chat, "printMessageHook", 42u);
            var sender = this.Utf8("me");
            var message = this.Utf8("hi");
            chat.ChatMessage += (XivChatType _, int _, ref SeString _, ref SeString msg, ref bool _) => msg = null!;
            var faults = Faults(typeof(ChatGui), "PrintMessageFault");

            var ret = DetourHarness.Invoke(chat, "HandlePrintMessageDetour", nint.Zero, XivChatType.Echo, sender, message, 0, (byte)0);

            Assert.Equal(42u, ret);
            var call = Assert.Single(original.Calls);
            Assert.Equal(message, call[3]);
            Assert.Equal(faults + 1, Faults(typeof(ChatGui), "PrintMessageFault"));
        }

        [Fact]
        public void FlyTextCallsOriginalOnceWhenItsOwnHandlingFails()
        {
            var flyText = (FlyTextGui)RuntimeHelpers.GetUninitializedObject(typeof(FlyTextGui));
            var original = DetourHarness.InstallFakeHook(flyText, "createFlyTextHook", (nint)0x77);
            object?[] Args() => [nint.Zero, 1, 2, 3, nint.Zero, 4u, 5u, 6u, nint.Zero, 0f];

            Assert.Equal((nint)0x77, DetourHarness.Invoke(flyText, "CreateFlyTextDetour", Args()));
            Assert.Single(original.Calls);

            // Before: logged and returned 0 without the game ever creating the fly text.
            flyText.FlyTextCreated += (ref FlyTextKind _, ref int _, ref int _, ref SeString text1, ref SeString _, ref uint _, ref uint _, ref uint _, ref float _, ref bool _) => text1 = null!;
            Assert.Equal((nint)0x77, DetourHarness.Invoke(flyText, "CreateFlyTextDetour", Args()));
            Assert.Equal(2, original.Calls.Count);
            Assert.Equal(Args(), original.Calls[1]);
        }

        [Fact]
        public void FlyTextTakenByAHandlerNeverReachesTheGame()
        {
            var flyText = (FlyTextGui)RuntimeHelpers.GetUninitializedObject(typeof(FlyTextGui));
            var original = DetourHarness.InstallFakeHook(flyText, "createFlyTextHook", (nint)0x77);
            flyText.FlyTextCreated += (ref FlyTextKind _, ref int _, ref int _, ref SeString _, ref SeString _, ref uint _, ref uint _, ref uint _, ref float _, ref bool handled) => handled = true;

            Assert.Equal(nint.Zero, DetourHarness.Invoke(flyText, "CreateFlyTextDetour", nint.Zero, 1, 2, 3, nint.Zero, 4u, 5u, 6u, nint.Zero, 0f));
            Assert.Empty(original.Calls);
        }

        [Fact]
        public void CursorUpdateCallsOriginalOnceWhenTheStageIsUnresolved()
        {
            var events = (AddonEventManager)RuntimeHelpers.GetUninitializedObject(typeof(AddonEventManager));
            var original = DetourHarness.InstallFakeHook(events, "onUpdateCursor");
            var faults = Faults(typeof(AddonEventManager), "UpdateCursorFault");

            DetourHarness.Invoke(events, "UpdateCursorDetour", nint.Zero);
            DetourHarness.Invoke(events, "UpdateCursorDetour", nint.Zero);

            Assert.Equal(2, original.Calls.Count);
            Assert.Equal(faults + 2, Faults(typeof(AddonEventManager), "UpdateCursorFault"));
        }

        [Fact]
        public void DebugCommandFailureReturnsTheGamesResult()
        {
            var commands = (CommandManager)RuntimeHelpers.GetUninitializedObject(typeof(CommandManager));
            var original = DetourHarness.InstallFakeHook(commands, "tryInvokeDebugCommandHook", -1);
            DetourHarness.SetField(commands, "commandMap", new ConcurrentDictionary<string, IReadOnlyCommandInfo>());

            // An empty command makes ProcessCommand call Substring(0, -1).
            Assert.Equal(-1, DetourHarness.Invoke(commands, "OnTryInvokeDebugCommand", nint.Zero, this.Utf8(string.Empty), nint.Zero));
            Assert.Equal(-1, DetourHarness.Invoke(commands, "OnTryInvokeDebugCommand", nint.Zero, this.Utf8("/nosuchcommand"), nint.Zero));
            Assert.Equal(2, original.Calls.Count);
        }

        [Fact]
        public void RsvStringFailureStillRegistersInTheGame()
        {
            var rsv = (RsvResolver)RuntimeHelpers.GetUninitializedObject(typeof(RsvResolver));
            var original = DetourHarness.InstallFakeHook(rsv, "addRsvStringHook", true);
            var key = this.Text("_rsv_1");
            var value = this.Text("v");

            // No lookup table yet: storing throws.
            Assert.True((bool)DetourHarness.Invoke(rsv, "AddRsvStringDetour", nint.Zero, key, value, (nuint)1)!);

            DetourHarness.SetField(rsv, "<Lookup>k__BackingField", new Dictionary<ReadOnlySeString, ReadOnlySeString>());
            Assert.True((bool)DetourHarness.Invoke(rsv, "AddRsvStringDetour", nint.Zero, key, value, (nuint)1)!);

            Assert.Equal(2, original.Calls.Count);
            Assert.True(rsv.TryResolve(new ReadOnlySeString("_rsv_1"u8.ToArray()), out _));
        }

        [Fact]
        public void DetourFaultLogsOnceAndCountsTheRest()
        {
            var fault = new DetourFault("Test.Detour", "測試");

            fault.Report(new InvalidOperationException("a"));
            fault.Report(new InvalidOperationException("b"));
            fault.Report("missing");

            Assert.Equal(3, fault.Failures);
            Assert.Contains("測試時出錯", fault.ChatLineFor(null));
        }

        [Fact]
        public void DetourFaultBlamesTheUpdateOnlyForAMissingAddress()
        {
            var fault = new DetourFault("Test.Wording", "測試");
            var unresolved = Assert.Throws<InvalidOperationException>(() => FFXIVClientStructs.FFXIV.Client.UI.UIGlobals.PlaySoundEffect(1));

            Assert.Contains("多半是遊戲改版所致", fault.ChatLineFor(null));
            Assert.Contains("多半是遊戲改版所致", fault.ChatLineFor(unresolved));
            Assert.Contains("多半是遊戲改版所致", fault.ChatLineFor(new AggregateException(unresolved)));

            var pluginSide = fault.ChatLineFor(new NullReferenceException());
            Assert.Contains("某個插件傳入的內容有問題", pluginSide);
            Assert.DoesNotContain("多半是遊戲改版所致", pluginSide);
            Assert.Contains("某個插件傳入的內容有問題", fault.ChatLineFor(new InvalidOperationException("Address for X.Y is not null.")));
        }

        private static long Faults(Type owner, string field)
            => ((DetourFault)DetourHarness.GetStaticField(owner, field)!).Failures;

        private static T GetProperty<T>(object target, string name)
            => (T)target.GetType().GetProperty(name, AnyInstance)!.GetValue(target)!;

        private static void SetProperty(object target, string name, object? value)
            => target.GetType().GetProperty(name, AnyInstance)!.SetValue(target, value);

        private static byte[] Read(nint block, int size)
        {
            var bytes = new byte[size];
            Marshal.Copy(block, bytes, 0, size);
            return bytes;
        }

        private static int Offset(string field)
            => typeof(Utf8String).GetField(field, AnyInstance)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;

        private ContextMenu NewContextMenu()
        {
            var menu = (ContextMenu)RuntimeHelpers.GetUninitializedObject(typeof(ContextMenu));
            DetourHarness.SetField(menu, "<MenuCallbackIds>k__BackingField", new List<int>());
            DetourHarness.SetField(menu, "<SelectedEventInterfaces>k__BackingField", new HashSet<nint>());
            DetourHarness.SetField(menu, "<MenuItems>k__BackingField", new Dictionary<ContextMenuType, List<IMenuItem>>());
            DetourHarness.SetField(menu, "<MenuItemsLock>k__BackingField", new object());
            return menu;
        }

        private nint Track(nint block)
        {
            this.blocks.Add(block);
            return block;
        }

        private nint Text(string text) => this.Track(DetourHarness.Bytes(Encoding.UTF8.GetBytes(text + "\0")));

        private nint Utf8(string text)
        {
            var data = this.Text(text);
            var length = Encoding.UTF8.GetByteCount(text);
            var str = this.Track(DetourHarness.Zeroed(Unsafe.SizeOf<Utf8String>()));
            Marshal.WriteIntPtr(str, Offset(nameof(Utf8String.StringPtr)), data);
            Marshal.WriteInt64(str, Offset(nameof(Utf8String.BufSize)), length + 1);
            Marshal.WriteInt64(str, Offset(nameof(Utf8String.BufUsed)), length + 1);
            Marshal.WriteInt64(str, Offset(nameof(Utf8String.StringLength)), length);
            return str;
        }
    }
}
