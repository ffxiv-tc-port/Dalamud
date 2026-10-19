namespace Dalamud.Networking.Http;

/// <summary>
/// Sizes and timings of <see cref="SegmentedDownloader"/>. Only tests change them.
/// </summary>
internal sealed record SegmentedDownloadOptions
{
    /// <summary>
    /// Gets the options every real download uses.
    /// </summary>
    public static SegmentedDownloadOptions Default { get; } = new();

    /// <summary>
    /// Gets the size of the range the first request asks for; a smaller file arrives in that one response.
    /// </summary>
    public long FirstRangeBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Gets the size the rest of the file is cut into before any worker starts.
    /// </summary>
    public long SegmentBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Gets the smallest piece an idle worker splits off another worker's range; ranges under twice this stay whole.
    /// </summary>
    public long MinSplitBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// Gets how long one request may go without receiving anything, headers included, before it is retried.
    /// </summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets how many times in a row one range may fail without making progress before the download gives up.
    /// </summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// Gets the wait after a range's first failure; it doubles with each further failure in a row.
    /// </summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Gets how many new download links may be fetched in a row without any data arriving in between.
    /// </summary>
    public int MaxRefreshesWithoutProgress { get; init; } = 2;

    /// <summary>
    /// Gets how often progress is reported and idle connections are put to work.
    /// </summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Gets how soon a range must be expected to finish, at its measured rate, for it not to be split.
    /// </summary>
    public TimeSpan NoSplitWithin { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the largest file downloaded this way; larger ones fall back to a plain download.
    /// </summary>
    public long MaxTotalBytes { get; init; } = 1024L * 1024 * 1024;

    /// <summary>
    /// Gets the size of each worker's read buffer.
    /// </summary>
    public int ChunkBytes { get; init; } = 64 * 1024;
}
