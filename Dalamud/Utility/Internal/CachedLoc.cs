using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;

using CheapLoc;

namespace Dalamud.Utility.Internal;

/// <summary>
/// What <see cref="Loc.Localize(string, string, Assembly)"/> gives for Dalamud's own strings, kept per key while
/// Dalamud's loc table stays the same. Each CheapLoc lookup builds an <see cref="AssemblyName"/>; files whose labels
/// are read every frame route <c>Loc.Localize</c> here with a <c>using Loc = ...</c> alias. Any thread.
/// </summary>
/// <remarks>
/// The answer depends only on the key, the fallback text and the table CheapLoc holds for Dalamud. CheapLoc never
/// edits a table in place (every setup stores a new one), so a kept answer is valid while that table is the same
/// object. If CheapLoc's table store cannot be found, every call goes to CheapLoc as before.
/// </remarks>
internal static class CachedLoc
{
    private static readonly Assembly Self = typeof(CachedLoc).Assembly;
    private static readonly string SelfName = Self.GetName().Name!;

    private static readonly Dictionary<string, Dictionary<string, LocEntry>>? Tables =
        typeof(Loc).GetField("LocData", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
            as Dictionary<string, Dictionary<string, LocEntry>>;

    private static readonly ConcurrentDictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    /// <summary>The localized text for <paramref name="key"/>, as CheapLoc gives it for Dalamud.</summary>
    /// <param name="key">The string key.</param>
    /// <param name="fallBack">The text used when the table has no entry for the key.</param>
    /// <returns>The localized text, the fallback, or <c>#</c> and the key.</returns>
    /// <remarks>Named like CheapLoc's: its exporter collects calls to a <c>Localize</c> with two string literals.</remarks>
    public static string Localize(string key, string fallBack)
    {
        var tables = Tables;
        if (tables == null || key == null)
            return Loc.Localize(key!, fallBack, Self);

        // The table is read before CheapLoc reads it: one swapped in meanwhile makes the next call ask again.
        tables.TryGetValue(SelfName, out var table);
        if (Entries.TryGetValue(key, out var entry) && ReferenceEquals(entry.Table, table) &&
            string.Equals(entry.FallBack, fallBack, StringComparison.Ordinal))
        {
            return entry.Value;
        }

        var value = Loc.Localize(key, fallBack, Self);
        Entries[key] = new Entry(table, fallBack, value);
        return value;
    }

    private sealed record Entry(Dictionary<string, LocEntry>? Table, string FallBack, string Value);
}
