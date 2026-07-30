using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.Configuration.Internal;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Logging.Internal;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.System.Memory;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.Completion;
using FFXIVClientStructs.FFXIV.Component.GUI;

using Lumina.Text;

namespace Dalamud.Game.Internal;

/// <summary>
/// This class adds Dalamud and plugin commands to the chat box's autocompletion.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal sealed unsafe class DalamudCompletion : IInternalDisposableService
{
    // 0xFF is a magic group number that causes CompletionModule's internals to treat entries
    // as raw strings instead of as lookups into an EXD sheet
    private const int GroupNumber = 0xFF;

    /// <summary>
    /// UIColor row used for the command itself.
    /// </summary>
    private const ushort CommandColorType = 539;

    /// <summary>
    /// UIColor row used for the trailing help message.
    /// </summary>
    private const ushort HelpColorType = 3;

    /// <summary>
    /// Help messages longer than this get ellipsised so a completion row stays readable.
    /// </summary>
    private const int MaxHelpLength = 48;

    /// <summary>
    /// Prologue of the function that <c>AtkTextInput::OpenCompletion</c> calls to build the
    /// "word currently being completed" strings, right before handing them to
    /// <c>CompletionModule</c>.
    /// </summary>
    /// <remarks>
    /// The TC/CN client inlines <c>OpenCompletion</c> into <c>AtkTextInput::ProcessKeyShortcut</c>,
    /// which leaves the standalone <c>OpenCompletion</c> body completely unreferenced - hooking it
    /// (as upstream does) silently never fires. This inner function is still a real call on every
    /// completion-open path, so it is the portable place to hook.
    /// </remarks>
    private const string OpenCompletionCoreSig =
        "48 89 5C 24 ?? 55 56 41 56 48 83 EC ?? 4C 8B F2 49 8B E8 48 8D 91 ?? ?? ?? ?? 48 8B F1 48 8B 02 80 38 00";

    /// <summary>
    /// The call site of <see cref="OpenCompletionCoreSig"/> inside <c>ProcessKeyShortcut</c>.
    /// Used as a second way to find the same function if its prologue ever drifts.
    /// </summary>
    private const string OpenCompletionCoreCallSig =
        "4C 8D 86 ?? ?? ?? ?? 48 8B CE 48 8D 96 ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 4E";

    /// <summary>
    /// Offset of the <c>E8</c> opcode within <see cref="OpenCompletionCoreCallSig"/>.
    /// </summary>
    private const int OpenCompletionCoreCallOffset = 17;

    /// <summary>
    /// The place inside <c>UIModule::Update</c> where the game itself calls
    /// <c>CompletionModule::Update</c>. We read the four <c>UIModule</c> displacements straight out
    /// of these instructions rather than trusting FFXIVClientStructs' declared field offsets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because those declared offsets were, on the TC client, 0x10 too high - which
    /// meant <c>&amp;uiModule-&gt;CompletionSheetName</c> actually pointed at the real string's
    /// <c>BufUsed</c> field. The game read that 0xB as a <c>char*</c> and crashed. Reading the
    /// displacements from the caller makes us track the client instead of the struct definitions.
    /// </para>
    /// <para>
    /// The matched instructions are:
    /// <code>
    ///   lea r9,  [r14+closeIconMacro]
    ///   mov qword [rsp+0x20], 0
    ///   lea r8,  [r14+openIconMacro]
    ///   lea rdx, [r14+completionSheetName]
    ///   lea rcx, [r14+completionModule]
    ///   call CompletionModule::Update
    /// </code>
    /// The trailing <c>mov qword [rsp+0x20], 0</c> is load-bearing: without it the pattern matches
    /// in two places. Do not shorten this signature.
    /// </para>
    /// </remarks>
    private const string UIModuleUpdateCompletionSig =
        "4D 8D 8E ?? ?? ?? ?? 48 C7 44 24 ?? 00 00 00 00 4D 8D 86 ?? ?? ?? ?? 49 8D 96 ?? ?? ?? ?? 49 8D 8E ?? ?? ?? ?? E8";

    private const int CloseIconMacroDispOffset = 0x03;
    private const int OpenIconMacroDispOffset = 0x13;
    private const int CompletionSheetNameDispOffset = 0x1A;
    private const int CompletionModuleDispOffset = 0x21;

    /// <summary>
    /// Any candidate string pointer below this is definitely not a real pointer. The failure this
    /// guards against handed the game 0xB (a misaligned read of <c>Utf8String.BufUsed</c>).
    /// </summary>
    private const ulong MinPlausibleStringPointer = 0x10000;

    /// <summary>
    /// Displacements outside this range are nonsense - <c>UIModule</c> is about 0xF7000 bytes.
    /// A generous bound is enough; the string checks below are what really validate the result.
    /// </summary>
    private const int MaxPlausibleUIModuleDisplacement = 0x1000000;

    /// <summary>
    /// The EXD sheet name that <c>UIModule</c>'s constructor stores in the string we resolve. This
    /// is a sheet name, not a localised string, so it is identical on every client.
    /// </summary>
    private const string ExpectedSheetName = "Completion";

    private static readonly ModuleLog Log = new("DalamudCompletion");

    [ServiceManager.ServiceDependency]
    private readonly CommandManager commandManager = Service<CommandManager>.Get();

    [ServiceManager.ServiceDependency]
    private readonly Framework framework = Service<Framework>.Get();

    [ServiceManager.ServiceDependency]
    private readonly DalamudConfiguration configuration = Service<DalamudConfiguration>.Get();

    private readonly Dictionary<string, EntryStrings> cachedCommands = [];

    /// <summary>
    /// Maps the payload-stripped text of an entry we added back onto the bare command, so that
    /// picking an entry inserts just the command and not its help message.
    /// </summary>
    private readonly Dictionary<string, string> displayToCommand = [];

    private EntryStrings? dalamudCategory;

    private Hook<OpenCompletionCoreDelegate>? openCompletionCoreHook;
    private Hook<AtkTextInput.Delegates.OpenCompletion>? openSuggestionsHook;
    private Hook<CompletionModule.Delegates.GetSelection>? getSelectionHook;

    /// <summary>
    /// Displacements into <c>UIModule</c>, read out of the game's own code. Only valid once
    /// <see cref="layoutVerified"/> is true.
    /// </summary>
    private int completionModuleDisp;
    private int completionSheetNameDisp;
    private int openIconMacroDisp;
    private int closeIconMacroDisp;

    /// <summary>
    /// Whether the offsets above were resolved *and* proved to point at real strings. Nothing in
    /// this service touches the game until this is true.
    /// </summary>
    private bool layoutVerified;

    /// <summary>
    /// Initializes a new instance of the <see cref="DalamudCompletion"/> class.
    /// </summary>
    [ServiceManager.ServiceConstructor]
    internal DalamudCompletion()
    {
        this.framework.RunOnTick(this.Setup);
    }

    /// <summary>
    /// Builds the two <see cref="Utf8String"/>s describing the word being completed.
    /// </summary>
    /// <param name="thisPtr">The owning text input.</param>
    /// <param name="rawWord">Receives the raw word.</param>
    /// <param name="filteredWord">Receives the filtered word.</param>
    /// <returns>Whether a completable word was found.</returns>
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate bool OpenCompletionCoreDelegate(
        AtkTextInput* thisPtr, Utf8String* rawWord, Utf8String* filteredWord);

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.openCompletionCoreHook?.Disable();
        this.openCompletionCoreHook?.Dispose();

        this.openSuggestionsHook?.Disable();
        this.openSuggestionsHook?.Dispose();

        this.getSelectionHook?.Disable();
        this.getSelectionHook?.Dispose();

        this.dalamudCategory?.Dispose();

        this.ClearCachedCommands();
    }

    /// <summary>
    /// Flattens a help message onto a single short line so it can ride along in a completion row.
    /// </summary>
    /// <param name="help">The raw help message.</param>
    /// <returns>The flattened message, possibly empty.</returns>
    private static string SanitizeHelp(string help)
    {
        if (string.IsNullOrWhiteSpace(help))
            return string.Empty;

        var flat = string.Join(' ', help.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return flat.Length > MaxHelpLength ? flat[..MaxHelpLength] + "…" : flat;
    }

    private void Setup()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null || uiModule->FrameCount == 0)
        {
            this.framework.RunOnTick(this.Setup);
            return;
        }

        // Fail closed. If we cannot prove we know where the completion strings live, we install no
        // hooks at all - a missing 【Dalamud】 category is a bug, handing the game a bad pointer is
        // a crash.
        if (!this.ResolveAndVerifyLayout(uiModule))
        {
            Log.Error(
                "Chat autocompletion for Dalamud/plugin commands has been disabled because the " +
                "UIModule completion layout could not be verified against this client.");
            return;
        }

        this.dalamudCategory = new EntryStrings("【Dalamud】");

        this.SetupOpenCompletionHook();

        this.getSelectionHook = Hook<CompletionModule.Delegates.GetSelection>.FromAddress(
            (nint)this.GetCompletionModule(uiModule)->VirtualTable->GetSelection,
            this.GetSelectionDetour);

        this.getSelectionHook.Enable();
    }

    /// <summary>
    /// Reads the four <c>UIModule</c> displacements out of <c>UIModule::Update</c> and then proves
    /// they are right by dereferencing them and checking the strings they land on.
    /// </summary>
    /// <param name="uiModule">The live UI module.</param>
    /// <returns>Whether the layout is safe to use.</returns>
    private bool ResolveAndVerifyLayout(UIModule* uiModule)
    {
        var scanner = Service<TargetSigScanner>.Get();

        // Guard 1: the signature must resolve at all.
        if (!scanner.TryScanText(UIModuleUpdateCompletionSig, out var site))
        {
            Log.Error(
                "Could not find the UIModule::Update -> CompletionModule::Update call site. " +
                "The completion layout is unknown on this client.");
            return false;
        }

        var moduleDisp = Marshal.ReadInt32(site + CompletionModuleDispOffset);
        var sheetDisp = Marshal.ReadInt32(site + CompletionSheetNameDispOffset);
        var openDisp = Marshal.ReadInt32(site + OpenIconMacroDispOffset);
        var closeDisp = Marshal.ReadInt32(site + CloseIconMacroDispOffset);

        Log.Information(
            $"UIModule::Update completion call at {site:X}: CompletionModule +{moduleDisp:X}, " +
            $"CompletionSheetName +{sheetDisp:X}, OpenIconMacro +{openDisp:X}, " +
            $"CloseIconMacro +{closeDisp:X}");

        foreach (var (name, disp) in new[]
                 {
                     ("CompletionModule", moduleDisp),
                     ("CompletionSheetName", sheetDisp),
                     ("CompletionOpenIconMacro", openDisp),
                     ("CompletionCloseIconMacro", closeDisp),
                 })
        {
            if (disp <= 0 || disp >= MaxPlausibleUIModuleDisplacement)
            {
                Log.Error($"Resolved {name} displacement +{disp:X} is not a plausible UIModule offset.");
                return false;
            }
        }

        // Guard 2: the three Utf8Strings must actually contain pointers. This is the check that
        // catches the class of failure we hit before - a half-field-sized layout error makes
        // StringPtr read back as some small integer (it was 0xB, i.e. BufUsed of "Completion").
        // Reading here is safe: all three displacements are inside the live UIModule allocation,
        // and we only *look at* the value, we never hand it to the game before validating it.
        foreach (var (name, disp) in new[]
                 {
                     ("CompletionSheetName", sheetDisp),
                     ("CompletionOpenIconMacro", openDisp),
                     ("CompletionCloseIconMacro", closeDisp),
                 })
        {
            var str = (Utf8String*)((byte*)uiModule + disp);
            var ptr = (ulong)str->StringPtr.Value;

            if (ptr < MinPlausibleStringPointer)
            {
                Log.Error(
                    $"{name} at UIModule+{disp:X} has StringPtr {ptr:X}, which is not a valid " +
                    "pointer. Refusing to hand this to the game.");
                return false;
            }
        }

        // Guard 3: the sheet name must literally be "Completion". This is an EXD sheet name that
        // the UIModule constructor writes, not a localised string, so it is the same everywhere.
        var sheetName = (Utf8String*)((byte*)uiModule + sheetDisp);
        var sheetText = sheetName->StringPtr.ExtractText();
        if (sheetText != ExpectedSheetName)
        {
            Log.Error(
                $"CompletionSheetName at UIModule+{sheetDisp:X} reads \"{sheetText}\", expected " +
                $"\"{ExpectedSheetName}\". The completion layout is not what we think it is.");
            return false;
        }

        // Purely diagnostic: shout if FFXIVClientStructs disagrees with the client, so the next
        // person gets a breadcrumb instead of a mystery.
        WarnOnClientStructsMismatch("CompletionModule", moduleDisp, GetFieldDisplacement(uiModule, &uiModule->CompletionModule));
        WarnOnClientStructsMismatch("CompletionSheetName", sheetDisp, GetFieldDisplacement(uiModule, &uiModule->CompletionSheetName));
        WarnOnClientStructsMismatch("CompletionOpenIconMacro", openDisp, GetFieldDisplacement(uiModule, &uiModule->CompletionOpenIconMacro));
        WarnOnClientStructsMismatch("CompletionCloseIconMacro", closeDisp, GetFieldDisplacement(uiModule, &uiModule->CompletionCloseIconMacro));

        this.completionModuleDisp = moduleDisp;
        this.completionSheetNameDisp = sheetDisp;
        this.openIconMacroDisp = openDisp;
        this.closeIconMacroDisp = closeDisp;
        this.layoutVerified = true;

        Log.Information($"Completion layout verified; sheet name reads \"{sheetText}\".");
        return true;

        static int GetFieldDisplacement(UIModule* module, void* field)
            => (int)((byte*)field - (byte*)module);

        static void WarnOnClientStructsMismatch(string name, int fromClient, int fromStructs)
        {
            if (fromClient != fromStructs)
            {
                Log.Warning(
                    $"FFXIVClientStructs declares UIModule.{name} at +{fromStructs:X}, but this " +
                    $"client uses +{fromClient:X}. Using the client's value.");
            }
        }
    }

    /// <summary>
    /// Gets the completion module using the displacement we read from the client.
    /// </summary>
    /// <param name="uiModule">The UI module.</param>
    /// <returns>The completion module.</returns>
    private CompletionModule* GetCompletionModule(UIModule* uiModule)
        => (CompletionModule*)((byte*)uiModule + this.completionModuleDisp);

    /// <summary>
    /// Installs the hook that repopulates the completion data just before the game opens the
    /// completion list.
    /// </summary>
    private void SetupOpenCompletionHook()
    {
        var address = this.ResolveOpenCompletionCore();
        if (address != nint.Zero)
        {
            this.openCompletionCoreHook = Hook<OpenCompletionCoreDelegate>.FromAddress(
                address,
                this.OpenCompletionCoreDetour);
            this.openCompletionCoreHook.Enable();
            return;
        }

        // Last resort: upstream's hook point. It is dead code on the TC client, so this will not
        // actually populate anything - but a future client may make it live again, and it is
        // better to be a no-op than to have no hook at all. This is only reached after the layout
        // has already been verified, so it cannot reintroduce the bad-pointer crash.
        Log.Error(
            "Could not locate the OpenCompletion core function; plugin commands will most likely " +
            "be missing from chat autocompletion. Falling back to AtkTextInput::OpenCompletion.");

        this.openSuggestionsHook = Hook<AtkTextInput.Delegates.OpenCompletion>.FromAddress(
            (nint)AtkTextInput.MemberFunctionPointers.OpenCompletion,
            this.OpenSuggestionsDetour);
        this.openSuggestionsHook.Enable();
    }

    /// <summary>
    /// Finds the function that actually runs whenever the completion list is opened.
    /// </summary>
    /// <returns>Its address, or <see cref="nint.Zero"/> if it could not be found.</returns>
    private nint ResolveOpenCompletionCore()
    {
        var scanner = Service<TargetSigScanner>.Get();

        if (scanner.TryScanText(OpenCompletionCoreSig, out var direct))
        {
            Log.Information($"OpenCompletion core found by prologue at {direct:X}");
            return direct;
        }

        // The prologue drifted. Fall back to the call site inside ProcessKeyShortcut and follow
        // its rel32 - the same technique GetStaticAddressFromSig uses for data references.
        if (scanner.TryScanText(OpenCompletionCoreCallSig, out var callSite))
        {
            var callOpcode = callSite + OpenCompletionCoreCallOffset;
            var target = callOpcode + 5 + Marshal.ReadInt32(callOpcode + 1);
            Log.Warning($"OpenCompletion core prologue drifted; found via call site {callOpcode:X} -> {target:X}");
            return target;
        }

        return nint.Zero;
    }

    private bool OpenCompletionCoreDetour(AtkTextInput* thisPtr, Utf8String* rawWord, Utf8String* filteredWord)
    {
        this.UpdateCompletionData();
        return this.openCompletionCoreHook!.Original(thisPtr, rawWord, filteredWord);
    }

    private void OpenSuggestionsDetour(AtkTextInput* thisPtr)
    {
        this.UpdateCompletionData();
        this.openSuggestionsHook!.Original(thisPtr);
    }

    private int GetSelectionDetour(CompletionModule* thisPtr, CategoryData.CompletionDataStruct* dataStructs, int index, Utf8String* outputString, Utf8String* outputDisplayString)
    {
        var ret = this.getSelectionHook!.Original.Invoke(thisPtr, dataStructs, index, outputString, outputDisplayString);
        this.HandleInsert(ret, outputString, outputDisplayString);
        return ret;
    }

    private void UpdateCompletionData()
    {
        if (!this.layoutVerified)
            return;

        if (!this.TryGetActiveTextInput(out var component, out var addon))
        {
            if (this.HasDalamudCategory())
                this.ResetCompletionData();

            return;
        }

        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return;

        this.ResetCompletionData();
        this.ClearCachedCommands();

        var currentText = component->EvaluatedString.StringPtr.ExtractText();

        var commands = this.commandManager.Commands
            .Where(kv => kv.Value.ShowInHelp && (currentText.Length == 0 || kv.Key.StartsWith(currentText)))
            .OrderBy(kv => kv.Key);

        if (!commands.Any())
            return;

        var completionModule = this.GetCompletionModule(uiModule);

        var categoryData = (CategoryData*)IMemorySpace.GetDefaultSpace()->Malloc((ulong)sizeof(CategoryData), 0x08);
        categoryData->Ctor(GroupNumber, 0xFF);

        completionModule->AddCategoryData(
            GroupNumber,
            this.dalamudCategory!.Display->StringPtr,
            this.dalamudCategory.Match->StringPtr, categoryData);

        var showHelp = this.configuration.ShowCommandHelpInCompletion;

        foreach (var (cmd, info) in commands)
        {
            if (!this.cachedCommands.TryGetValue(cmd, out var entryString))
            {
                entryString = new EntryStrings(cmd, showHelp ? SanitizeHelp(info.HelpMessage) : string.Empty);
                this.cachedCommands.Add(cmd, entryString);
                this.displayToCommand[entryString.PlainDisplay] = cmd;
            }

            completionModule->AddCompletionEntry(
                GroupNumber,
                0xFF,
                entryString.Display->StringPtr,
                entryString.Match->StringPtr,
                0xFF);
        }

        categoryData->SortEntries();
    }

    private void HandleInsert(int ret, Utf8String* outputString, Utf8String* outputDisplayString)
    {
        // -2 means it was a plain text final selection, so it might be ours.
        if (ret != -2 || outputString == null)
            return;

        // Strip out the color payloads and the trailing help message that we added to the string.
        var txt = outputString->StringPtr.ExtractText();
        if (!this.displayToCommand.TryGetValue(txt, out var command))
        {
            if (!this.cachedCommands.ContainsKey(txt))
                return;

            command = txt;
        }

        if (!this.TryGetActiveTextInput(out _, out _))
        {
            outputString->Clear();

            if (outputDisplayString != null)
                outputDisplayString->Clear();

            return;
        }

        outputString->SetString(command + ' ');
    }

    private bool TryGetActiveTextInput(out AtkComponentTextInput* component, out AtkUnitBase* addon)
    {
        component = null;
        addon = null;

        var raptureAtkModule = RaptureAtkModule.Instance();
        if (raptureAtkModule == null)
            return false;

        var textInputEventInterface = raptureAtkModule->TextInput.TargetTextInputEventInterface;
        if (textInputEventInterface == null)
            return false;

        var ownerNode = textInputEventInterface->GetOwnerNode();
        if (ownerNode == null || ownerNode->GetNodeType() != NodeType.Component)
            return false;

        var componentNode = (AtkComponentNode*)ownerNode;
        var componentBase = componentNode->Component;
        if (componentBase == null || componentBase->GetComponentType() != ComponentType.TextInput)
            return false;

        component = (AtkComponentTextInput*)componentBase;

        addon = component->OwnerAddon;

        if (addon == null)
            addon = component->ContainingAddon2;

        if (addon == null)
            addon = RaptureAtkUnitManager.Instance()->GetAddonByNode((AtkResNode*)component->OwnerNode);

        return addon != null && addon->NameString == "ChatLog";
    }

    private bool HasDalamudCategory()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return false;

        var completionModule = this.GetCompletionModule(uiModule);

        for (var i = 0; i < completionModule->CategoryNames.Count; i++)
        {
            if (completionModule->CategoryNames[i].AsReadOnlySeStringSpan().ContainsText("【Dalamud】"u8))
            {
                return true;
            }
        }

        return false;
    }

    private void ResetCompletionData()
    {
        // Belt and braces: this is the call that crashed the client when the offsets were wrong,
        // so it never runs unless the layout was proved good at startup.
        if (!this.layoutVerified)
            return;

        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return;

        var completionModule = this.GetCompletionModule(uiModule);

        completionModule->ClearCompletionData();

        // This happens in UIModule.Update. Just repeat it to fill CompletionData back up with
        // defaults, using the very displacements the game passes at that call site.
        completionModule->Update(
            (Utf8String*)((byte*)uiModule + this.completionSheetNameDisp),
            (Utf8String*)((byte*)uiModule + this.openIconMacroDisp),
            (Utf8String*)((byte*)uiModule + this.closeIconMacroDisp),
            0);
    }

    private void ClearCachedCommands()
    {
        foreach (var entry in this.cachedCommands.Values)
        {
            entry.Dispose();
        }

        this.cachedCommands.Clear();
        this.displayToCommand.Clear();
    }

    private class EntryStrings : IDisposable
    {
        public EntryStrings(string command)
            : this(command, string.Empty)
        {
        }

        public EntryStrings(string command, string help)
        {
            var rssb = SeStringBuilder.SharedPool.Get();

            rssb.PushColorType(CommandColorType)
                .Append(command)
                .PopColorType();

            if (help.Length > 0)
            {
                rssb.Append("  ")
                    .PushColorType(HelpColorType)
                    .Append(help)
                    .PopColorType();

                this.PlainDisplay = command + "  " + help;
            }
            else
            {
                this.PlainDisplay = command;
            }

            this.Display = Utf8String.FromSequence(rssb.GetViewAsSpan());

            SeStringBuilder.SharedPool.Return(rssb);

            this.Match = Utf8String.FromString(command);
        }

        public Utf8String* Display { get; }

        public Utf8String* Match { get; }

        /// <summary>
        /// Gets the payload-stripped form of <see cref="Display"/>, i.e. what
        /// <c>ExtractText()</c> will hand back when the game echoes this entry.
        /// </summary>
        public string PlainDisplay { get; }

        public void Dispose()
        {
            this.Display->Dtor(true);
            this.Match->Dtor(true);
        }
    }
}
