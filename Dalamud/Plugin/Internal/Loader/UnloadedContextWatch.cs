using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Loader;
using System.Threading;

using Dalamud.Logging.Internal;

namespace Dalamud.Plugin.Internal.Loader;

/// <summary>
/// Logs whether unloaded plugin load contexts are eventually collected. Never forces a collection and never polls:
/// the watched contexts are only looked at when some plugin loads or unloads.
/// </summary>
/// <remarks>
/// Holds only weak references. A context still alive is logged once it has survived <see cref="MinimumFullCollections"/>
/// full collections, and again only after another one, so that a context that is merely not collected yet stays quiet.
/// Never throws: it runs inside plugin loading and unloading, which must not fail because of it.
/// </remarks>
internal static class UnloadedContextWatch
{
    /// <summary>Full collections a context may survive before it is reported as still in memory.</summary>
    public const int MinimumFullCollections = 3;

    private static readonly ModuleLog Log = new("PLUGINALC");
    private static readonly Watch Shared = new();

    private static int failureWarned;

    /// <summary>Starts watching a context that is about to be unloaded.</summary>
    /// <param name="context">The context.</param>
    public static void Track(AssemblyLoadContext context) => Shared.Track(context);

    /// <summary>Logs every watched context that was collected (and stops watching it), or that is still alive after enough full collections.</summary>
    /// <returns>What was logged.</returns>
    public static List<Observation> Report() =>
        Shared.ReportWith(static context => UnloadedContextHolders.Census(context, UnloadedContextHolders.Sources.FromServices()));

    private static void WarnOnce(Exception ex)
    {
        if (Interlocked.Exchange(ref failureWarned, 1) == 0)
            Log.Warning(ex, "Could not watch unloaded plugin contexts");
    }

    private static List<string> TryCensus(Func<AssemblyLoadContext, List<string>> census, AssemblyLoadContext context)
    {
        try
        {
            return census(context);
        }
        catch (Exception ex)
        {
            WarnOnce(ex);
            return [$"(census failed: {ex.GetType().Name})"];
        }
    }

    /// <summary>One logged look at an unloaded context.</summary>
    /// <param name="Name">The context's name (the plugin's assembly name).</param>
    /// <param name="Collected">Whether the context has been collected.</param>
    /// <param name="SinceUnload">Time since it was unloaded.</param>
    /// <param name="FullCollections">Full (generation 2) collections since it was unloaded.</param>
    /// <param name="Holders">What Dalamud saw holding it, listed the first time it was reported alive; otherwise null.</param>
    public readonly record struct Observation(
        string Name, bool Collected, TimeSpan SinceUnload, int FullCollections, IReadOnlyList<string>? Holders = null);

    /// <summary>One list of watched contexts. Dalamud uses a single shared one; tests make their own so they cannot see each other's contexts.</summary>
    internal sealed class Watch
    {
        private readonly List<Entry> entries = [];

        /// <summary>Starts watching a context that is about to be unloaded.</summary>
        /// <param name="context">The context.</param>
        public void Track(AssemblyLoadContext context)
        {
            try
            {
                var entry = new Entry(context.Name ?? "?", new WeakReference(context, trackResurrection: true), Stopwatch.GetTimestamp(), GC.CollectionCount(2));
                lock (this.entries)
                    this.entries.Add(entry);
            }
            catch (Exception ex)
            {
                WarnOnce(ex);
            }
        }

        /// <summary>As <see cref="UnloadedContextWatch.Report"/>, listing what holds a context the first time it is reported alive with <paramref name="census"/>.</summary>
        /// <param name="census">Lists the holders of a context still in memory.</param>
        /// <returns>What was logged.</returns>
        public List<Observation> ReportWith(Func<AssemblyLoadContext, List<string>> census)
        {
            try
            {
                return this.ReportCore(census);
            }
            catch (Exception ex)
            {
                WarnOnce(ex);
                return [];
            }
        }

        private List<Observation> ReportCore(Func<AssemblyLoadContext, List<string>> census)
        {
            var now = Stopwatch.GetTimestamp();
            var fullCollections = GC.CollectionCount(2);
            var observations = new List<Observation>();
            List<(int Index, WeakReference Context)>? firstAlive = null;
            lock (this.entries)
            {
                foreach (var entry in this.entries)
                {
                    var collected = !entry.Context.IsAlive;
                    var since = fullCollections - entry.FullCollectionsAtUnload;
                    if (!collected && (since < MinimumFullCollections || fullCollections == entry.ReportedAt))
                        continue;

                    if (!collected && entry.ReportedAt < 0)
                        (firstAlive ??= []).Add((observations.Count, entry.Context));
                    entry.ReportedAt = fullCollections;
                    entry.Collected = collected;
                    var elapsed = TimeSpan.FromSeconds(Math.Round(Stopwatch.GetElapsedTime(entry.UnloadedAt, now).TotalSeconds));
                    observations.Add(new Observation(entry.Name, collected, elapsed, since));
                }

                this.entries.RemoveAll(e => e.Collected);
            }

            // Outside the lock: a census walks other services' state and may take a while.
            foreach (var (index, weak) in firstAlive ?? [])
            {
                if (weak.Target is AssemblyLoadContext context)
                    observations[index] = observations[index] with { Holders = TryCensus(census, context) };
            }

            foreach (var o in observations)
            {
                if (o.Collected)
                {
                    Log.Information("{Context}, unloaded {Elapsed} ago, has been collected ({FullCollections} full GCs since)", o.Name, o.SinceUnload, o.FullCollections);
                }
                else
                {
                    Log.Information(
                        "{Context}, unloaded {Elapsed} ago, is still in memory after {FullCollections} full GCs; something still references it",
                        o.Name,
                        o.SinceUnload,
                        o.FullCollections);
                    if (o.Holders is { Count: > 0 } holders)
                        Log.Information("{Context} still referenced by: {Holders:l}", o.Name, string.Join("; ", holders));
                    else if (o.Holders != null)
                        Log.Information("{Context} is {NoHolder:l}", o.Name, UnloadedContextHolders.NoVisibleHolder);
                }
            }

            return observations;
        }
    }

    private sealed class Entry(string name, WeakReference context, long unloadedAt, int fullCollectionsAtUnload)
    {
        public string Name => name;

        public WeakReference Context => context;

        public long UnloadedAt => unloadedAt;

        public int FullCollectionsAtUnload => fullCollectionsAtUnload;

        public int ReportedAt { get; set; } = -1;

        public bool Collected { get; set; }
    }
}
