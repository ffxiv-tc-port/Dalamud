using System.Globalization;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Collects the gen0 size right before each gen0 collection, which is how far gen0 got to grow (its budget).
/// </summary>
/// <remarks>
/// Not thread safe: one thread adds, describes and resets. Fixed storage, so adding never allocates.
/// The median is the lower one, at 1 MB resolution (sizes from 255 MB up share the last bucket); min and max are exact.
/// </remarks>
internal sealed class Gen0SizeStats
{
    /// <summary>Number of 1 MB buckets.</summary>
    public const int BucketCount = 256;

    private const long BytesPerMegabyte = 1024 * 1024;

    private readonly int[] buckets = new int[BucketCount];
    private long min = long.MaxValue;
    private long max;

    /// <summary>Gets how many sizes were added since the last reset.</summary>
    public int Count { get; private set; }

    /// <summary>Adds the gen0 size before one gen0 collection.</summary>
    /// <param name="bytes">The size in bytes; negative counts as 0.</param>
    public void Add(long bytes)
    {
        bytes = Math.Max(0, bytes);
        this.buckets[(int)Math.Min(bytes / BytesPerMegabyte, BucketCount - 1)]++;
        this.min = Math.Min(this.min, bytes);
        this.max = Math.Max(this.max, bytes);
        this.Count++;
    }

    /// <summary>Gets the 1 MB bucket holding the lower median.</summary>
    /// <returns>The bucket, which is its lower edge in MB; -1 when nothing was added.</returns>
    public int MedianBucket()
    {
        if (this.Count == 0)
            return -1;

        var target = (this.Count + 1) / 2;
        var seen = 0;
        for (var i = 0; i < BucketCount; i++)
        {
            seen += this.buckets[i];
            if (seen >= target)
                return i;
        }

        return BucketCount - 1;
    }

    /// <summary>Describes the sizes for the log, like <c>median 47-48 MB, min 12.3 MB, max 58.1 MB, 812 samples</c>.</summary>
    /// <returns>The text; says so when nothing was added, never a made-up 0.</returns>
    public string Describe()
    {
        var median = this.MedianBucket();
        if (median < 0)
            return "none seen, 0 samples";

        var medianText = median == BucketCount - 1
                             ? $"{median} MB or more"
                             : $"{median}-{median + 1} MB";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"median {medianText}, min {this.min / (double)BytesPerMegabyte:0.0} MB, max {this.max / (double)BytesPerMegabyte:0.0} MB, {this.Count} samples");
    }

    /// <summary>Forgets every size.</summary>
    public void Reset()
    {
        Array.Clear(this.buckets);
        this.min = long.MaxValue;
        this.max = 0;
        this.Count = 0;
    }
}
