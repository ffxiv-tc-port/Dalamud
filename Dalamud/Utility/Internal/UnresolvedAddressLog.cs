using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Dalamud.Logging.Internal;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Logs a failure caused by a ClientStructs address this game version does not have once per plugin and address
/// instead of on every frame, and tells the user once per plugin in chat.
/// </summary>
/// <remarks>
/// Only changes how a caught exception is logged: callers keep doing whatever they did after catching it.
/// <see cref="TryAbsorb"/> may be called from any thread and never throws.
/// At most <see cref="Capacity"/> plugin/address pairs are tracked; once full, further new pairs are only counted.
/// </remarks>
internal sealed class UnresolvedAddressLog
{
    /// <summary>How many plugin/address pairs (and plugin display names) are kept.</summary>
    internal const int Capacity = 512;

    /// <summary>Prefix of the <see cref="FeatureDisabledEcho"/> key; the plugin's internal name follows.</summary>
    internal const string EchoKeyPrefix = "UnresolvedAddress:";

    // ClientStructs' InteropGenerator.Runtime.ThrowHelper.ThrowNullAddress: "Address for {Type.Member} is null. The resolver ...".
    private const string MessagePrefix = "Address for ";
    private const string MessageNullMarker = " is null.";

    private const string DalamudLabel = "Dalamud";
    private const int MaxExceptionsInspected = 32;

    private static readonly ModuleLog Log = new("UNRESOLVED");

    private readonly object gate = new();
    private readonly Dictionary<(string Owner, string Address), long> counts = new();
    private readonly HashSet<string> echoedOwners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> displayNames = new(StringComparer.Ordinal);
    private readonly int capacity;
    private readonly Action<string, string> echo;

    private long untracked;
    private bool overflowStarted;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnresolvedAddressLog"/> class.
    /// </summary>
    /// <param name="capacity">How many plugin/address pairs to track.</param>
    /// <param name="echo">Queues a chat line under a key; see <see cref="FeatureDisabledEcho.Report"/>.</param>
    internal UnresolvedAddressLog(int capacity, Action<string, string> echo)
    {
        this.capacity = capacity;
        this.echo = echo;
    }

    /// <summary>Gets the process-wide instance.</summary>
    internal static UnresolvedAddressLog Instance { get; } = new(Capacity, FeatureDisabledEcho.Report);

    /// <summary>Gets how many failures arrived for pairs that were new after the table was full.</summary>
    internal long UntrackedCount
    {
        get
        {
            lock (this.gate)
                return this.untracked;
        }
    }

    /// <summary>
    /// Gets the ClientStructs names (<c>Type.Member</c>) of the unresolved addresses an exception was thrown for.
    /// </summary>
    /// <remarks>
    /// True only if every failure inside <paramref name="ex"/> is ClientStructs' "Address for X is null"
    /// <see cref="InvalidOperationException"/>. <see cref="AggregateException"/>s are searched through, and so is the
    /// inner exception of any other exception (TargetInvocationException, TypeInitializationException and similar wrappers).
    /// </remarks>
    /// <param name="ex">The caught exception.</param>
    /// <param name="names">The distinct names, in the order found.</param>
    /// <returns>Whether the exception is entirely made of unresolved-address failures.</returns>
    internal static bool TryGetUnresolvedAddresses(Exception ex, [NotNullWhen(true)] out List<string>? names)
    {
        names = null;
        var pending = new Stack<Exception>();
        pending.Push(ex);
        var inspected = 0;
        while (pending.TryPop(out var current))
        {
            if (++inspected > MaxExceptionsInspected)
            {
                names = null;
                return false;
            }

            if (current is AggregateException aggregate)
            {
                var inner = aggregate.InnerExceptions;
                if (inner.Count == 0)
                {
                    names = null;
                    return false;
                }

                for (var i = inner.Count - 1; i >= 0; i--)
                    pending.Push(inner[i]);
                continue;
            }

            if (TryParseAddressName(current, out var name))
            {
                names ??= [];
                if (!names.Contains(name))
                    names.Add(name);
                continue;
            }

            if (current.InnerException is { } wrapped)
            {
                pending.Push(wrapped);
                continue;
            }

            names = null;
            return false;
        }

        return names != null;
    }

    /// <summary>
    /// Reads the address name out of one ClientStructs unresolved-address exception.
    /// </summary>
    /// <param name="ex">The exception; inner exceptions are not looked at.</param>
    /// <param name="name">The name, <c>Type.Member</c>.</param>
    /// <returns>Whether <paramref name="ex"/> is exactly that exception.</returns>
    internal static bool TryParseAddressName(Exception ex, [NotNullWhen(true)] out string? name)
    {
        name = null;
        if (ex.GetType() != typeof(InvalidOperationException))
            return false;

        var message = ex.Message;
        if (!message.StartsWith(MessagePrefix, StringComparison.Ordinal))
            return false;

        var end = message.IndexOf(MessageNullMarker, MessagePrefix.Length, StringComparison.Ordinal);
        if (end <= MessagePrefix.Length)
            return false;

        var candidate = message.AsSpan(MessagePrefix.Length, end - MessagePrefix.Length);
        var dot = candidate.LastIndexOf('.');
        if (dot <= 0 || dot == candidate.Length - 1)
            return false;

        foreach (var c in candidate)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
                return false;
        }

        name = candidate.ToString();
        return true;
    }

    /// <summary>
    /// Formats the chat line for a plugin, or for Dalamud itself.
    /// </summary>
    /// <param name="displayName">The plugin's display name, or null for Dalamud.</param>
    /// <param name="address">The first unresolved address, <c>Type.Member</c>.</param>
    /// <returns>The line.</returns>
    internal static string FormatChatLine(string? displayName, string address)
        => displayName == null
               ? $"Dalamud：有內建功能因遊戲改版找不到遊戲位址（{address}）而暫停，請等 Dalamud 更新。詳情請輸入 /xllog 查看。"
               : $"Dalamud：「{displayName}」插件有功能因遊戲改版找不到遊戲位址（{address}）而暫停，請等插件更新。詳情請輸入 /xllog 查看。";

    /// <summary>
    /// Remembers the name a plugin shows to the user, for the chat line. Any thread.
    /// </summary>
    /// <param name="internalName">The plugin's internal name.</param>
    /// <param name="displayName">The plugin's display name.</param>
    internal void RememberDisplayName(string? internalName, string? displayName)
    {
        if (internalName == null || string.IsNullOrWhiteSpace(displayName))
            return;

        if (this.displayNames.Count < this.capacity || this.displayNames.ContainsKey(internalName))
            this.displayNames[internalName] = displayName;
    }

    /// <summary>
    /// Logs <paramref name="ex"/> the first time it is an unresolved-address failure for this plugin and address.
    /// </summary>
    /// <param name="ex">The caught exception.</param>
    /// <param name="plugin">Internal name of the plugin whose code threw, or null for Dalamud.</param>
    /// <param name="where">What was running, for the log.</param>
    /// <returns>False if the caller should log <paramref name="ex"/> as it always did; true if it was handled here.</returns>
    internal bool TryAbsorb(Exception ex, string? plugin, string where)
    {
        try
        {
            if (!TryGetUnresolvedAddresses(ex, out var names))
                return false;

            var result = this.Record(plugin, names);
            var label = plugin ?? DalamudLabel;
            if (result.Fresh != null)
            {
                Log.Error(
                    ex,
                    "[{Plugin}] {Where}: ClientStructs address {Addresses} is unresolved in this game version. Logged once per plugin and address; repeats are only counted",
                    label,
                    where,
                    string.Join(", ", result.Fresh));
            }

            if (result.EchoAddress != null)
            {
                var display = plugin == null ? null : this.displayNames.TryGetValue(plugin, out var shown) ? shown : plugin;
                this.echo(EchoKeyPrefix + (plugin ?? "<" + DalamudLabel + ">"), FormatChatLine(display, result.EchoAddress));
            }

            if (result.OverflowStarted)
                Log.Error("More than {Capacity} plugin/address pairs failed to resolve; new pairs are only counted from now on", this.capacity);

            return true;
        }
        catch (Exception inner)
        {
            try
            {
                Log.Error(inner, "Could not record an unresolved-address failure");
            }
            catch
            {
                // Logging is all that is left to try.
            }

            return false;
        }
    }

    /// <summary>
    /// Counts one failure.
    /// </summary>
    /// <param name="plugin">Internal name of the plugin, or null for Dalamud.</param>
    /// <param name="names">The unresolved addresses it was thrown for.</param>
    /// <returns>What is new: addresses to log, the address to put in the plugin's chat line, the table filling up.</returns>
    internal RecordResult Record(string? plugin, IReadOnlyList<string> names)
    {
        var owner = plugin ?? string.Empty;
        List<string>? fresh = null;
        string? echoAddress = null;
        var overflowNow = false;
        lock (this.gate)
        {
            foreach (var name in names)
            {
                var key = (owner, name);
                if (this.counts.TryGetValue(key, out var count))
                {
                    this.counts[key] = count + 1;
                }
                else if (this.counts.Count >= this.capacity)
                {
                    this.untracked++;
                    overflowNow |= !this.overflowStarted;
                    this.overflowStarted = true;
                }
                else
                {
                    this.counts[key] = 1;
                    (fresh ??= []).Add(name);
                    if (echoAddress == null && this.echoedOwners.Add(owner))
                        echoAddress = name;
                }
            }
        }

        return new RecordResult(fresh, echoAddress, overflowNow);
    }

    /// <summary>
    /// Gets how many failures were counted for one plugin and address.
    /// </summary>
    /// <param name="plugin">Internal name of the plugin, or null for Dalamud.</param>
    /// <param name="address">The address name.</param>
    /// <returns>The count; 0 if the pair is not tracked.</returns>
    internal long CountOf(string? plugin, string address)
    {
        lock (this.gate)
            return this.counts.TryGetValue((plugin ?? string.Empty, address), out var count) ? count : 0;
    }

    /// <summary>What <see cref="Record"/> found new.</summary>
    /// <param name="Fresh">Addresses not seen before for this plugin, or null.</param>
    /// <param name="EchoAddress">The address for the plugin's one chat line, or null if it already has one.</param>
    /// <param name="OverflowStarted">Whether this failure is the first one the full table could not track.</param>
    internal readonly record struct RecordResult(List<string>? Fresh, string? EchoAddress, bool OverflowStarted);
}
