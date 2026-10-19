using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Dalamud.Interface.Internal.Windows.PluginInstaller;

/// <summary>
/// Sorts a list in place into the order <c>OrderBy(keySelector)</c> gives: keys compared with
/// <see cref="Comparer{T}.Default"/> for strings, ties kept in list order. Reuses its buffers between calls.
/// </summary>
/// <remarks>Not thread-safe; one instance per caller thread.</remarks>
/// <typeparam name="T">The element type.</typeparam>
internal sealed class StableStringKeySorter<T>
    where T : class
{
    private readonly Comparison<int> compareIndices;

    private string?[] keys = [];
    private int[] order = [];
    private T?[] elements = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="StableStringKeySorter{T}"/> class.
    /// </summary>
    public StableStringKeySorter()
    {
        this.compareIndices = this.CompareIndices;
    }

    /// <summary>
    /// Sorts <paramref name="list"/> by the key of each element, computed once per element.
    /// </summary>
    /// <param name="list">The list to sort.</param>
    /// <param name="keySelector">The key of an element.</param>
    public void Sort(List<T> list, Func<T, string?> keySelector)
    {
        var count = list.Count;
        if (count < 2)
            return;

        if (this.order.Length < count)
        {
            var size = Math.Max(count, this.order.Length * 2);
            this.keys = new string?[size];
            this.order = new int[size];
            this.elements = new T?[size];
        }

        var span = CollectionsMarshal.AsSpan(list);
        try
        {
            for (var i = 0; i < count; i++)
            {
                this.keys[i] = keySelector(span[i]);
                this.order[i] = i;
                this.elements[i] = span[i];
            }

            this.order.AsSpan(0, count).Sort(this.compareIndices);

            for (var i = 0; i < count; i++)
                span[i] = this.elements[this.order[i]]!;
        }
        finally
        {
            // Holding the elements past the call would keep removed plugins alive.
            Array.Clear(this.keys, 0, count);
            Array.Clear(this.elements, 0, count);
        }
    }

    private int CompareIndices(int x, int y)
    {
        var result = Comparer<string?>.Default.Compare(this.keys[x], this.keys[y]);
        return result != 0 ? result : x.CompareTo(y);
    }
}
