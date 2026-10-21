using System.Collections.Generic;
using System.Linq;

using Dalamud.Common.Game;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Logging.Internal;

using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using Utf8String = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Checks, before any service starts, the game addresses without which Dalamud must not run at all.
/// </summary>
/// <remarks>
/// These stay fail-stop: Tick and Destroy run and tear down everything, an empty Condition or ObjectTable or a stale
/// territory would let automation act on wrong state, and without Framework.Instance, GetNetworkModuleProxy or
/// Device.Instance the interface never starts and plugins wait forever; GameGui does not start without its
/// Utf8String.Ctor_FromSequence hook. Only resolution and the section each address must lie in are checked, never
/// what the instances point to: in entrypoint mode they do not exist yet.
/// </remarks>
internal static class CoreAddressPreflight
{
    /// <summary>The message box title.</summary>
    internal const string DialogCaption = "台服 Dalamud：停止載入";

    private const int FrameworkDestroySlot = 2;
    private const int FrameworkTickSlot = 4;

    private static readonly ModuleLog Log = new("PREFLIGHT");

    /// <summary>The PE section an address has to be in.</summary>
    internal enum Section
    {
        /// <summary>Code.</summary>
        Text,

        /// <summary>Read-only data, where vtables live.</summary>
        RData,

        /// <summary>Writable data, where the game's globals live.</summary>
        Data,
    }

    /// <summary>
    /// Decides whether Dalamud may start its services; shows why not if it may not. Never throws.
    /// </summary>
    /// <param name="scanner">The game's scanner, after the ClientStructs resolver ran.</param>
    /// <param name="version">The game version, for the messages.</param>
    /// <param name="showError">Shows the stop message (text, caption).</param>
    /// <returns>Whether every core address resolved.</returns>
    internal static bool Check(TargetSigScanner scanner, GameVersion? version, Action<string, string> showError)
    {
        List<CoreAddress> missing;
        try
        {
            var layout = ModuleLayout.Of(scanner);
            var addresses = Gather(layout, signature => scanner.TryScanText(signature, out var found) ? found : 0);
            missing = FindMissing(addresses, layout);
            if (missing.Count == 0)
            {
                Log.Information("All {Count} core game addresses resolved", addresses.Count);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The core address check failed; starting normally");
            return true;
        }

        Log.Error(
            "Core game addresses missing for game version {Version}: {Missing}; Dalamud and plugins are not loaded this session",
            version?.ToString() ?? "?",
            string.Join(", ", missing.Select(m => m.Name)));

        try
        {
            showError(FormatDialog(version?.ToString() ?? "?", missing), DialogCaption);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not show the stop message");
        }

        return false;
    }

    /// <summary>
    /// Collects the core addresses. Reads game memory only for the Framework vtable slots, and only once the vtable
    /// address lies in .rdata.
    /// </summary>
    /// <param name="layout">Where the game's sections are.</param>
    /// <param name="scanText">Scans .text for a signature; returns 0 if not found.</param>
    /// <returns>The addresses, 0 where unresolved.</returns>
    internal static List<CoreAddress> Gather(ModuleLayout layout, Func<string, nint> scanText)
    {
        var vtable = CSFramework.Addresses.StaticVirtualTable.Value;
        return
        [
            new("Framework.VirtualTable", "遊戲主迴圈", vtable, Section.RData),
            new("Framework.Tick", "每幀更新，插件靠它執行", ReadSlot(vtable, FrameworkTickSlot, layout), Section.Text),
            new("Framework.Destroy", "關閉遊戲時卸載 Dalamud", ReadSlot(vtable, FrameworkDestroySlot, layout), Section.Text),
            new("Conditions.Instance", "角色狀態（戰鬥、詠唱、過場等）", Conditions.Addresses.Instance.Value, Section.Data),
            new("GameObjectManager.Instance", "周圍物件清單", GameObjectManager.Addresses.Instance.Value, Section.Data),
            new(
                "ClientState.HandleZoneInitPacket",
                "切換區域",
                scanText(ClientStateAddressResolver.HandleZoneInitPacketSignature),
                Section.Text),
            new("Framework.Instance", "遊戲主物件", CSFramework.Addresses.Instance.Value, Section.Data),
            new("Framework.GetNetworkModuleProxy", "Dalamud 介面的啟動時機", CSFramework.Addresses.GetNetworkModuleProxy.Value, Section.Text),
            new("Device.Instance", "繪圖裝置（Dalamud 介面）", Device.Addresses.Instance.Value, Section.Data),
            new("Utf8String.Ctor_FromSequence", "建立遊戲字串", Utf8String.Addresses.Ctor_FromSequence.Value, Section.Text),
        ];
    }

    /// <summary>
    /// Reads one vtable slot, but only if the whole slot lies in .rdata.
    /// </summary>
    /// <param name="vtable">The vtable address.</param>
    /// <param name="slot">The slot index.</param>
    /// <param name="layout">Where the game's sections are.</param>
    /// <returns>The slot's value, or 0 if the slot is not in .rdata.</returns>
    internal static unsafe nint ReadSlot(nint vtable, int slot, ModuleLayout layout)
    {
        var at = vtable + (slot * sizeof(nint));
        return vtable != 0 && layout.RData.Contains(at, sizeof(nint)) ? *(nint*)at : 0;
    }

    /// <summary>
    /// Picks the addresses that are unresolved or outside their section.
    /// </summary>
    /// <param name="addresses">The addresses.</param>
    /// <param name="layout">Where the game's sections are.</param>
    /// <returns>The missing ones, in input order.</returns>
    internal static List<CoreAddress> FindMissing(IEnumerable<CoreAddress> addresses, ModuleLayout layout)
        => addresses.Where(a => a.Value == 0 || !layout.Get(a.Expected).Contains(a.Value, 1)).ToList();

    /// <summary>
    /// Builds the stop message.
    /// </summary>
    /// <param name="version">The game version.</param>
    /// <param name="missing">The missing addresses.</param>
    /// <returns>The message box text.</returns>
    internal static string FormatDialog(string version, IEnumerable<CoreAddress> missing)
        => $"台服 Dalamud 在遊戲版本 {version} 找不到下列核心遊戲位址，這次不載入 Dalamud 與所有插件：\n" +
           string.Concat(missing.Select(m => $"・{m.Name}（{m.Purpose}）\n")) +
           "\n遊戲照常運作，只是這次沒有插件。通常是遊戲改版、Dalamud 還沒跟上所致；請等 Dalamud 更新後重新啟動遊戲。\n" +
           "詳情記在 dalamud.log（搜尋 [PREFLIGHT]）。\n\n" +
           "Dalamud could not find core game addresses for this game version. " +
           "Dalamud and plugins are not loaded this session; the game continues normally.";

    /// <summary>One core address.</summary>
    /// <param name="Name">The address, as Type.Member.</param>
    /// <param name="Purpose">What it is for, in the user's language.</param>
    /// <param name="Value">The resolved address, 0 if unresolved.</param>
    /// <param name="Expected">The section it must lie in.</param>
    internal readonly record struct CoreAddress(string Name, string Purpose, nint Value, Section Expected);

    /// <summary>One section of the game module in memory.</summary>
    /// <param name="Start">The first byte.</param>
    /// <param name="Size">The size in bytes.</param>
    internal readonly record struct SectionRange(nint Start, long Size)
    {
        /// <summary>Gets whether <paramref name="length"/> bytes at <paramref name="address"/> lie inside.</summary>
        /// <param name="address">The address.</param>
        /// <param name="length">How many bytes.</param>
        /// <returns>Whether they do.</returns>
        public bool Contains(nint address, int length)
            => this.Size > 0 && address >= this.Start && (long)(address - this.Start) <= this.Size - length;
    }

    /// <summary>The game module's sections in memory.</summary>
    /// <param name="Text">.text.</param>
    /// <param name="RData">.rdata.</param>
    /// <param name="Data">.data.</param>
    internal sealed record ModuleLayout(SectionRange Text, SectionRange RData, SectionRange Data)
    {
        /// <summary>Reads the layout of the real module (not the scanner's copy).</summary>
        /// <param name="scanner">The scanner.</param>
        /// <returns>The layout.</returns>
        public static ModuleLayout Of(ISigScanner scanner)
        {
            var moduleBase = scanner.Module.BaseAddress;
            return new(
                new(moduleBase + (nint)scanner.TextSectionOffset, scanner.TextSectionSize),
                new(moduleBase + (nint)scanner.RDataSectionOffset, scanner.RDataSectionSize),
                new(moduleBase + (nint)scanner.DataSectionOffset, scanner.DataSectionSize));
        }

        /// <summary>Gets one section.</summary>
        /// <param name="section">Which.</param>
        /// <returns>Its range.</returns>
        public SectionRange Get(Section section)
            => section switch
            {
                Section.Text => this.Text,
                Section.RData => this.RData,
                _ => this.Data,
            };
    }
}
