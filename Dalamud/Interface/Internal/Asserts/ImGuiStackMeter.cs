using System.Threading;

namespace Dalamud.Interface.Internal.Asserts;

/// <summary>
/// Running totals of how one plugin's UiBuilder draws left ImGui's stacks: per stack, how much deeper or shallower
/// they were afterwards and in how many draws.
/// </summary>
/// <remarks>
/// One writer, the thread drawing the plugin, which publishes the draw count before the stack totals; readers on
/// other threads read the stack totals first. Totals only grow. <see cref="Record"/> allocates nothing.
/// </remarks>
internal sealed class ImGuiStackMeter
{
    /// <summary>Length of the array <see cref="ReadTotals"/> fills.</summary>
    public const int TotalsLength = (3 * ImGuiStackDepths.Count) + 2;

    private readonly long[] pushed = new long[ImGuiStackDepths.Count];
    private readonly long[] popped = new long[ImGuiStackDepths.Count];
    private readonly long[] changedDraws = new long[ImGuiStackDepths.Count];
    private long draws;
    private long uncompared;
    private volatile bool retired;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImGuiStackMeter"/> class. Use <see cref="ImGuiStackReport.CreateMeter"/>.
    /// </summary>
    /// <param name="pluginName">Internal name of the plugin.</param>
    internal ImGuiStackMeter(string pluginName)
    {
        this.PluginName = pluginName;
    }

    /// <summary>Gets the internal name of the plugin.</summary>
    public string PluginName { get; }

    /// <summary>Gets a value indicating whether the owner is gone and the meter only waits for its last read.</summary>
    public bool IsRetired => this.retired;

    /// <summary>Gets or sets the totals the report has already counted. Only the report's thread uses it.</summary>
    internal long[]? Reported { get; set; }

    /// <summary>
    /// Counts one draw: what changed between the capture before it and the capture after it.
    /// </summary>
    /// <param name="before">Captured just before the plugin's draw handlers ran.</param>
    /// <param name="after">Captured just after they returned.</param>
    public void Record(in ImGuiStackDepths before, in ImGuiStackDepths after)
    {
        Volatile.Write(ref this.draws, this.draws + 1);

        // Another context, or none: nothing in the two captures belongs together.
        if (before.Context == 0 || before.Context != after.Context)
        {
            Volatile.Write(ref this.uncompared, this.uncompared + 1);
            return;
        }

        // ID stacks are per window; two different windows' stacks say nothing about each other.
        var sameWindow = before.CurrentWindow != 0 && before.CurrentWindow == after.CurrentWindow;
        for (var i = 0; i < ImGuiStackDepths.Count; i++)
        {
            var delta = after[i] - before[i];
            if (delta == 0 || (i == ImGuiStackDepths.Id && !sameWindow))
                continue;

            if (delta > 0)
                Volatile.Write(ref this.pushed[i], this.pushed[i] + delta);
            else
                Volatile.Write(ref this.popped[i], this.popped[i] - delta);
            Volatile.Write(ref this.changedDraws[i], this.changedDraws[i] + 1);
        }
    }

    /// <summary>
    /// Marks the meter as no longer used; the next report reads it one last time and drops it.
    /// </summary>
    public void Retire() => this.retired = true;

    /// <summary>
    /// Copies the totals: per stack the depth added, the depth removed and the draws that changed it, then the
    /// draws and the draws that could not be compared.
    /// </summary>
    /// <param name="into">At least <see cref="TotalsLength"/> long.</param>
    internal void ReadTotals(Span<long> into)
    {
        const int n = ImGuiStackDepths.Count;
        for (var i = 0; i < n; i++)
        {
            into[i] = Volatile.Read(ref this.pushed[i]);
            into[n + i] = Volatile.Read(ref this.popped[i]);
            into[(2 * n) + i] = Volatile.Read(ref this.changedDraws[i]);
        }

        into[(3 * n) + 1] = Volatile.Read(ref this.uncompared);
        into[3 * n] = Volatile.Read(ref this.draws);
    }
}
