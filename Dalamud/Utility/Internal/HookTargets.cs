using System.Collections.Generic;

using Dalamud.Game;
using Dalamud.Logging.Internal;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Checks the addresses one service hooks and records the ones it has to leave out.
/// </summary>
/// <remarks>
/// A function is accepted only inside the game's .text. A vtable is accepted only when all of it lies inside .rdata,
/// and only then may its slots be read. Each address left out is logged once as Information
/// (<c>[HOOKSKIP] Service.Member: reason</c>) and its feature is named in one notice per service.
/// Not thread-safe: one instance per service constructor. Never throws.
/// </remarks>
internal sealed unsafe class HookTargets
{
    /// <summary>The notice; {0} lists the features that are off, {1} is the closing sentence.</summary>
    internal const string ChatLineFormat =
        "Dalamud：遊戲改版後找不到部分遊戲位址，下列功能先停用（遊戲本身照常，用到它們的插件功能可能沒反應），請等 Dalamud 更新：{0}。{1}";

    /// <summary>The usual closing sentence of the notice.</summary>
    internal const string DefaultChatTail = "詳情請輸入 /xllog 查看。";

    /// <summary>Prefix of the <see cref="FeatureDisabledEcho"/> key; the service name follows.</summary>
    internal const string EchoKeyPrefix = "HookSkip.";

    /// <summary>Why an address found by a Dalamud signature is 0, for the log.</summary>
    internal const string SignatureNotFound = "the signature does not match this game version";

    private static readonly ModuleLog Log = new("HOOKSKIP");

    private readonly CoreAddressPreflight.ModuleLayout? layout;
    private readonly List<string> features = [];
    private readonly List<string> skipped = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="HookTargets"/> class.
    /// </summary>
    /// <param name="service">The service, as written in the log.</param>
    /// <param name="layout">The game's sections; null accepts nothing.</param>
    internal HookTargets(string service, CoreAddressPreflight.ModuleLayout? layout)
    {
        this.Service = service;
        this.layout = layout;
    }

    /// <summary>Gets the service, as written in the log.</summary>
    internal string Service { get; }

    /// <summary>Gets the features that are off, in the order first left out.</summary>
    internal IReadOnlyList<string> DisabledFeatures => this.features;

    /// <summary>Gets the members left out, in order.</summary>
    internal IReadOnlyList<string> SkippedMembers => this.skipped;

    /// <summary>
    /// Creates the checks for one service from the scanner's view of the real game module.
    /// </summary>
    /// <param name="service">The service, as written in the log.</param>
    /// <param name="scanner">The game's scanner.</param>
    /// <returns>The checks; if the sections cannot be read, every address is left out.</returns>
    internal static HookTargets For(string service, ISigScanner scanner)
    {
        CoreAddressPreflight.ModuleLayout? layout = null;
        try
        {
            layout = CoreAddressPreflight.ModuleLayout.Of(scanner);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Service:l}: could not read the game's sections; all of its hooks are left out", service);
        }

        return new(service, layout);
    }

    /// <summary>
    /// Accepts a function address inside the game's code.
    /// </summary>
    /// <param name="member">The address, as written in the log.</param>
    /// <param name="feature">What is off without it, in the user's language.</param>
    /// <param name="address">The address, 0 if unresolved.</param>
    /// <param name="unresolved">Why it is 0, for the log.</param>
    /// <returns>The address, or 0 if it was left out.</returns>
    internal nint Function(string member, string feature, nint address, string unresolved = "unresolved in this game version")
    {
        if (address == 0)
            return this.Skip(member, feature, unresolved);

        if (this.layout is not { } sections || !sections.Text.Contains(address, 1))
            return this.Skip(member, feature, $"0x{address:X} is not in the game's code");

        return address;
    }

    /// <summary>
    /// Accepts a data address inside the game's .data.
    /// </summary>
    /// <param name="member">The address, as written in the log.</param>
    /// <param name="feature">What is off without it, in the user's language.</param>
    /// <param name="address">The address, 0 if unresolved.</param>
    /// <param name="length">How many bytes are read or written there.</param>
    /// <returns>The address, or 0 if it was left out.</returns>
    internal nint Data(string member, string feature, nint address, int length)
    {
        if (address == 0)
            return this.Skip(member, feature, "unresolved in this game version");

        if (this.layout is not { } sections || !sections.Data.Contains(address, length))
            return this.Skip(member, feature, $"0x{address:X}+{length} is not in the game's data");

        return address;
    }

    /// <summary>
    /// Gets whether <paramref name="length"/> bytes at <paramref name="address"/> lie inside the game's code.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="length">How many bytes.</param>
    /// <returns>Whether they do.</returns>
    internal bool InCode(nint address, int length)
        => address != 0 && this.layout is { } sections && sections.Text.Contains(address, length);

    /// <summary>
    /// Passes a vtable through only if all of it lies inside the game's .rdata; its slots may be read only then.
    /// </summary>
    /// <typeparam name="T">The ClientStructs vtable struct.</typeparam>
    /// <param name="vtable">The vtable, null if unresolved.</param>
    /// <returns>The vtable, or null.</returns>
    internal T* VirtualTable<T>(T* vtable)
        where T : unmanaged
        => this.InReadOnlyData((nint)vtable, sizeof(T)) ? vtable : null;

    /// <summary>
    /// Gets whether <paramref name="length"/> bytes at <paramref name="address"/> lie inside the game's .rdata.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <param name="length">How many bytes.</param>
    /// <returns>Whether they do.</returns>
    internal bool InReadOnlyData(nint address, int length)
        => address != 0 && this.layout is { } sections && sections.RData.Contains(address, length);

    /// <summary>
    /// Accepts a function read from a vtable slot.
    /// </summary>
    /// <param name="member">The slot, as written in the log.</param>
    /// <param name="feature">What is off without it, in the user's language.</param>
    /// <param name="checkedVtable">The vtable after <see cref="VirtualTable{T}"/>.</param>
    /// <param name="slotValue">The slot's value; ignored when <paramref name="checkedVtable"/> is null.</param>
    /// <returns>The function, or 0 if it was left out.</returns>
    internal nint Slot(string member, string feature, void* checkedVtable, nint slotValue)
        => checkedVtable == null
               ? this.Skip(member, feature, "its vtable is unresolved or not in the game's read-only data")
               : this.Function(member, feature, slotValue);

    /// <summary>
    /// Keeps a group only if every member was accepted: hooking part of it is worse than none.
    /// </summary>
    /// <param name="feature">What is off without the group, in the user's language.</param>
    /// <param name="group">The members and their checked addresses.</param>
    /// <returns>Whether all of them may be hooked.</returns>
    internal bool AllOrNone(string feature, params ReadOnlySpan<(string Member, nint Address)> group)
    {
        var missing = new List<string>();
        foreach (var (member, address) in group)
        {
            if (address == 0)
                missing.Add(member);
        }

        if (missing.Count == 0)
            return true;

        var reason = $"it works only together with {string.Join(", ", missing)}, which is not hooked";
        foreach (var (member, address) in group)
        {
            if (address != 0)
                this.Skip(member, feature, reason);
        }

        return false;
    }

    /// <summary>
    /// Records that an address is left out.
    /// </summary>
    /// <param name="member">The address, as written in the log.</param>
    /// <param name="feature">What is off without it, in the user's language.</param>
    /// <param name="reason">Why, for the log.</param>
    /// <returns>Always 0.</returns>
    internal nint Skip(string member, string feature, string reason)
    {
        try
        {
            Log.Information("{Service:l}.{Member:l}: {Reason:l}; not hooked, {Feature:l} is off this session", this.Service, member, reason, feature);
        }
        catch
        {
            // The record below is what matters.
        }

        this.skipped.Add(member);
        if (!this.features.Contains(feature))
            this.features.Add(feature);
        return 0;
    }

    /// <summary>
    /// Builds the service's notice.
    /// </summary>
    /// <param name="tail">The closing sentence instead of <see cref="DefaultChatTail"/>, or null.</param>
    /// <returns>The line, or null if nothing is off.</returns>
    internal string? FormatChatLine(string? tail = null)
        => this.features.Count == 0 ? null : string.Format(ChatLineFormat, string.Join("、", this.features), tail ?? DefaultChatTail);

    /// <summary>
    /// Tells the user once which of this service's features are off. Nothing if none are.
    /// </summary>
    /// <param name="tail">The closing sentence instead of <see cref="DefaultChatTail"/>, or null.</param>
    internal void ReportDisabled(string? tail = null)
    {
        if (this.FormatChatLine(tail) is { } line)
            FeatureDisabledEcho.Report(EchoKeyPrefix + this.Service, line);
    }
}
