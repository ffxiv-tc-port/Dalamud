using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Running total of the bytes one kind of plugin callback allocated on the thread that ran it.
/// </summary>
/// <remarks>
/// One writer: only the thread raising the callback calls <see cref="End"/>, so the total needs no lock and is
/// published with <see cref="Volatile"/> for readers on other threads. The registry is copy-on-write.
/// Nested measurements on one thread are exclusive: what an inner callback allocated counts only for the inner one.
/// Diagnostic for users to report back: who allocates enough to drive the GC.
/// </remarks>
internal sealed class PluginAllocationMeter
{
    /// <summary>Callback name of the plugin's Draw event.</summary>
    public const string Draw = "Draw";

    /// <summary>Callback name of the plugin's Framework Update event.</summary>
    public const string Update = "Update";

    /// <summary>Callback name of the plugin's addon lifecycle listeners.</summary>
    public const string AddonEvents = "AddonEvents";

    /// <summary>Callback name of the plugin's chat message handlers.</summary>
    public const string Chat = "Chat";

    private static readonly object RegistryLock = new();
    private static PluginAllocationMeter[] registry = [];

    // Bytes this thread already handed to a meter; an enclosing measurement subtracts what changed in between.
    [ThreadStatic]
    private static long attributedOnThread;

    private long bytes;
    private volatile bool retired;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginAllocationMeter"/> class and registers it.
    /// </summary>
    /// <param name="pluginName">Internal name of the plugin.</param>
    /// <param name="callback">Which callback this meter measures: <see cref="Draw"/>, <see cref="Update"/>,
    /// <see cref="AddonEvents"/> or <see cref="Chat"/>.</param>
    public PluginAllocationMeter(string pluginName, string callback)
    {
        this.PluginName = pluginName;
        this.Callback = callback;
        lock (RegistryLock)
            Volatile.Write(ref registry, [.. registry, this]);
    }

    /// <summary>Gets the registered meters. A published array is never changed afterwards.</summary>
    public static PluginAllocationMeter[] Registered => Volatile.Read(ref registry);

    /// <summary>Gets the internal name of the plugin.</summary>
    public string PluginName { get; }

    /// <summary>Gets the callback this meter measures.</summary>
    public string Callback { get; }

    /// <summary>Gets the bytes counted so far.</summary>
    public long Bytes => Volatile.Read(ref this.bytes);

    /// <summary>Gets a value indicating whether the owner is gone and the meter only waits for its last read.</summary>
    public bool IsRetired => this.retired;

    /// <summary>
    /// Takes the reading to pass to <see cref="End"/>, on the thread that is about to run the callback.
    /// </summary>
    /// <returns>The reading.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Reading Begin() => new(GC.GetAllocatedBytesForCurrentThread(), attributedOnThread);

    /// <summary>
    /// Removes the given retired meters from the registry.
    /// </summary>
    /// <param name="meters">Meters whose final total has been read.</param>
    public static void Unregister(IReadOnlyCollection<PluginAllocationMeter> meters)
    {
        if (meters.Count == 0)
            return;

        lock (RegistryLock)
            Volatile.Write(ref registry, registry.Where(x => !meters.Contains(x)).ToArray());
    }

    /// <summary>
    /// Adds what this thread allocated since <paramref name="begin"/>, minus what nested measurements took.
    /// Only the thread that called <see cref="Begin"/>.
    /// </summary>
    /// <param name="begin">The value <see cref="Begin"/> returned.</param>
    public void End(Reading begin)
    {
        var total = GC.GetAllocatedBytesForCurrentThread() - begin.Allocated;
        var nested = attributedOnThread - begin.Attributed;
        attributedOnThread = begin.Attributed + total;
        Volatile.Write(ref this.bytes, this.bytes + (total - nested));
    }

    /// <summary>
    /// Marks the meter as no longer used; the next report reads it one last time and unregisters it.
    /// </summary>
    public void Retire() => this.retired = true;

    /// <summary>
    /// What <see cref="Begin"/> saw on its thread.
    /// </summary>
    public readonly struct Reading
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Reading"/> struct.
        /// </summary>
        /// <param name="allocated">Bytes the thread had allocated.</param>
        /// <param name="attributed">Bytes the thread had handed to meters.</param>
        public Reading(long allocated, long attributed)
        {
            this.Allocated = allocated;
            this.Attributed = attributed;
        }

        /// <summary>Gets the bytes the thread had allocated.</summary>
        public long Allocated { get; }

        /// <summary>Gets the bytes the thread had handed to meters.</summary>
        public long Attributed { get; }
    }
}
