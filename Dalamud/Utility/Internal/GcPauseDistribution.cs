using System.Text;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Counts collections by their longest pause, separately for each <see cref="GameScreen"/>.
/// </summary>
/// <remarks>
/// Not thread safe: one thread adds, describes and resets. Buckets are &lt;10, 10-17, 17-33, 33-50, 50-100 and
/// &gt;=100 ms; a pause exactly on an edge goes to the upper bucket. Each sighting also says how many collections
/// happened since the previous one, so coverage is seen / collections.
/// </remarks>
internal sealed class GcPauseDistribution
{
    /// <summary>Number of buckets.</summary>
    public const int BucketCount = 6;

    private const int ScreenCount = 3;

    private static readonly long[] BucketEnds =
    [
        TimeSpan.FromMilliseconds(10).Ticks,
        TimeSpan.FromMilliseconds(17).Ticks,
        TimeSpan.FromMilliseconds(33).Ticks,
        TimeSpan.FromMilliseconds(50).Ticks,
        TimeSpan.FromMilliseconds(100).Ticks,
    ];

    private readonly int[] counts = new int[ScreenCount * BucketCount];
    private readonly long[] seen = new long[ScreenCount];
    private readonly long[] collections = new long[ScreenCount];

    /// <summary>Gets the bucket a pause belongs to.</summary>
    /// <param name="pause">The longest pause of one collection.</param>
    /// <returns>0 to <see cref="BucketCount"/> - 1.</returns>
    public static int BucketOf(TimeSpan pause)
    {
        for (var i = 0; i < BucketEnds.Length; i++)
        {
            if (pause.Ticks < BucketEnds[i])
                return i;
        }

        return BucketEnds.Length;
    }

    /// <summary>Adds one collection that was seen.</summary>
    /// <param name="screen">The screen at the time it was seen.</param>
    /// <param name="longestPause">Its longest pause.</param>
    /// <param name="collectionsSincePrevious">Collections since the previous sighting, this one included; at least 1.</param>
    public void Add(GameScreen screen, TimeSpan longestPause, long collectionsSincePrevious)
    {
        var s = ScreenIndex(screen);
        this.counts[(s * BucketCount) + BucketOf(longestPause)]++;
        this.seen[s]++;
        this.collections[s] += Math.Max(1, collectionsSincePrevious);
    }

    /// <summary>Gets the count of one bucket.</summary>
    /// <param name="screen">The screen.</param>
    /// <param name="bucket">The bucket.</param>
    /// <returns>The count.</returns>
    public int Count(GameScreen screen, int bucket) => this.counts[(ScreenIndex(screen) * BucketCount) + bucket];

    /// <summary>Gets how many collections were seen on a screen.</summary>
    /// <param name="screen">The screen.</param>
    /// <returns>The count.</returns>
    public long Seen(GameScreen screen) => this.seen[ScreenIndex(screen)];

    /// <summary>Gets how many collections happened on a screen, seen or not.</summary>
    /// <param name="screen">The screen.</param>
    /// <returns>The count.</returns>
    public long Collections(GameScreen screen) => this.collections[ScreenIndex(screen)];

    /// <summary>Describes one screen for the log, like <c>0 / 3 / 1 / 0 / 0 / 0 (seen 4 of 5 collections)</c>.</summary>
    /// <param name="screen">The screen.</param>
    /// <returns>The text.</returns>
    public string Describe(GameScreen screen)
    {
        var s = ScreenIndex(screen);
        var text = new StringBuilder();
        for (var i = 0; i < BucketCount; i++)
        {
            if (i > 0)
                text.Append(" / ");
            text.Append(this.counts[(s * BucketCount) + i]);
        }

        return text.Append(" (seen ").Append(this.seen[s]).Append(" of ").Append(this.collections[s]).Append(" collections)").ToString();
    }

    /// <summary>Clears every count.</summary>
    public void Reset()
    {
        Array.Clear(this.counts);
        Array.Clear(this.seen);
        Array.Clear(this.collections);
    }

    private static int ScreenIndex(GameScreen screen) => screen switch
    {
        GameScreen.Loading => 1,
        GameScreen.InGame => 2,
        _ => 0,
    };
}
