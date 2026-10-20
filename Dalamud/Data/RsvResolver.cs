using System.Collections.Generic;

using Dalamud.Hooking;
using Dalamud.Logging.Internal;
using Dalamud.Memory;
using Dalamud.Utility.Internal;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using Lumina.Text.ReadOnly;

namespace Dalamud.Data;

/// <summary>
/// Provides functionality for resolving RSV strings.
/// </summary>
internal sealed unsafe class RsvResolver : IDisposable
{
    /// <summary>The <see cref="FeatureDisabledEcho"/> key of the notice.</summary>
    internal const string EchoKey = "RsvResolver.AddRsvString";

    /// <summary>The notice shown when the game function is missing.</summary>
    internal const string ChatLine =
        "Dalamud：遊戲改版後找不到記錄 RSV 字串的遊戲位址，新內容的部分名稱可能顯示成 _rsv_ 開頭的代碼，請等 Dalamud 更新。";

    private static readonly ModuleLog Log = new("RsvProvider");

    private static readonly DetourFault AddRsvStringFault = new("RsvResolver.AddRsvString", "記錄遊戲的 RSV 字串");

    private readonly Hook<LayoutWorld.Delegates.AddRsvString>? addRsvStringHook;

    /// <summary>
    /// Initializes a new instance of the <see cref="RsvResolver"/> class. Without the game function the hook is
    /// skipped and <see cref="TryResolve"/> finds nothing; Dalamud still starts.
    /// </summary>
    public RsvResolver()
    {
        var address = (nint)LayoutWorld.MemberFunctionPointers.AddRsvString;
        if (address == 0)
        {
            Log.Information("[HOOKSKIP] RsvResolver.AddRsvString: LayoutWorld.AddRsvString is unresolved; RSV strings stay unresolved this session");
            FeatureDisabledEcho.Report(EchoKey, ChatLine);
            return;
        }

        this.addRsvStringHook = Hook<LayoutWorld.Delegates.AddRsvString>.FromAddress(address, this.AddRsvStringDetour);
        this.addRsvStringHook.Enable();
    }

    /// <summary>Gets a value indicating whether the game's RSV strings are being recorded.</summary>
    internal bool IsHooked => this.addRsvStringHook != null;

    private Dictionary<ReadOnlySeString, ReadOnlySeString> Lookup { get; } = [];

    /// <summary>Attemps to resolve an RSV string.</summary>
    /// <inheritdoc cref="Lumina.Excel.ExcelModule.ResolveRsvDelegate"/>
    public bool TryResolve(ReadOnlySeString rsvString, out ReadOnlySeString resolvedString) =>
        this.Lookup.TryGetValue(rsvString, out resolvedString);

    /// <inheritdoc/>
    public void Dispose()
    {
        this.addRsvStringHook?.Dispose();
    }

    // Original is called exactly once, outside the try block.
    private bool AddRsvStringDetour(LayoutWorld* @this, byte* rsvString, byte* resolvedString, nuint resolvedStringSize)
    {
        try
        {
            var rsv = new ReadOnlySeString(MemoryHelper.ReadRawNullTerminated((nint)rsvString));
            var resolved = new ReadOnlySeString(new ReadOnlySpan<byte>(resolvedString, (int)resolvedStringSize).ToArray());
            Log.Debug($"Resolving RSV \"{rsv}\" to \"{resolved}\".");
            this.Lookup[rsv] = resolved;
        }
        catch (Exception ex)
        {
            AddRsvStringFault.Report(ex);
        }

        return this.addRsvStringHook!.Original(@this, rsvString, resolvedString, resolvedStringSize);
    }
}
