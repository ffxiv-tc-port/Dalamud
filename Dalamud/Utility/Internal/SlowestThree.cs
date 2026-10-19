namespace Dalamud.Utility.Internal;

/// <summary>
/// The three longest named durations offered so far, longest first; on a tie the one offered first ranks higher.
/// </summary>
/// <remarks>A value type that allocates nothing; copy it to rank more entries without touching the original.</remarks>
internal struct SlowestThree
{
    private string? name0;
    private string? name1;
    private string? name2;
    private long ticks0;
    private long ticks1;
    private long ticks2;

    /// <summary>Gets how many entries are held, at most 3.</summary>
    public int Count { get; private set; }

    /// <summary>Gets an entry, 0 being the longest.</summary>
    /// <param name="index">0 to <see cref="Count"/> - 1.</param>
    /// <returns>The entry.</returns>
    public readonly (string Name, long Ticks) this[int index] => index switch
    {
        0 when this.Count > 0 => (this.name0!, this.ticks0),
        1 when this.Count > 1 => (this.name1!, this.ticks1),
        2 when this.Count > 2 => (this.name2!, this.ticks2),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>Offers one duration.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="ticks">Its length.</param>
    public void Offer(string name, long ticks)
    {
        var count = this.Count;
        if (count == 3 && ticks <= this.ticks2)
            return;

        if (count == 0 || ticks > this.ticks0)
        {
            (this.name2, this.ticks2) = (this.name1, this.ticks1);
            (this.name1, this.ticks1) = (this.name0, this.ticks0);
            (this.name0, this.ticks0) = (name, ticks);
        }
        else if (count == 1 || ticks > this.ticks1)
        {
            (this.name2, this.ticks2) = (this.name1, this.ticks1);
            (this.name1, this.ticks1) = (name, ticks);
        }
        else
        {
            (this.name2, this.ticks2) = (name, ticks);
        }

        if (count < 3)
            this.Count = count + 1;
    }
}
