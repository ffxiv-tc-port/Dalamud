using System.Net.Http;

namespace Dalamud.Networking.Http;

/// <summary>
/// How a <see cref="SegmentedDownloader.DownloadAsync"/> call ended.
/// </summary>
internal enum SegmentedDownloadOutcome
{
    /// <summary>
    /// Every byte arrived and is in <see cref="SegmentedDownloadResult.Data"/>.
    /// </summary>
    Completed,

    /// <summary>
    /// The server ignored the range and is sending the whole file in <see cref="SegmentedDownloadResult.WholeResponse"/>.
    /// </summary>
    WholeResponse,

    /// <summary>
    /// The first response cannot be split; nothing of the file was read.
    /// </summary>
    NotSupported,

    /// <summary>
    /// The download stopped part way through; what had arrived was discarded.
    /// </summary>
    GaveUp,
}

/// <summary>
/// Counters of one segmented download, for the log.
/// </summary>
/// <param name="Connections">The most requests that were in flight at once.</param>
/// <param name="Requests">All requests sent, link refreshes included.</param>
/// <param name="Retries">Failed requests that were tried again.</param>
/// <param name="LinkRefreshes">Times a rejected download link was replaced by a new one.</param>
/// <param name="Splits">Times an idle connection took over half of another's remaining range.</param>
/// <param name="BytesReceived">Bytes of the file received.</param>
internal readonly record struct SegmentedDownloadStats(
    int Connections, int Requests, int Retries, int LinkRefreshes, int Splits, long BytesReceived);

/// <summary>
/// The result of <see cref="SegmentedDownloader.DownloadAsync"/>.
/// </summary>
internal sealed class SegmentedDownloadResult
{
    private SegmentedDownloadResult(
        SegmentedDownloadOutcome outcome,
        byte[]? data,
        HttpResponseMessage? wholeResponse,
        string? reason,
        Exception? exception,
        SegmentedDownloadStats stats)
    {
        this.Outcome = outcome;
        this.Data = data;
        this.WholeResponse = wholeResponse;
        this.Reason = reason;
        this.Exception = exception;
        this.Stats = stats;
    }

    /// <summary>
    /// Gets how the download ended.
    /// </summary>
    public SegmentedDownloadOutcome Outcome { get; }

    /// <summary>
    /// Gets the file, when <see cref="Outcome"/> is <see cref="SegmentedDownloadOutcome.Completed"/>.
    /// </summary>
    public byte[]? Data { get; }

    /// <summary>
    /// Gets the unread whole-file response, when <see cref="Outcome"/> is <see cref="SegmentedDownloadOutcome.WholeResponse"/>.
    /// The caller must read and dispose it.
    /// </summary>
    public HttpResponseMessage? WholeResponse { get; }

    /// <summary>
    /// Gets why the file was not downloaded in ranges. Never contains a URL.
    /// </summary>
    public string? Reason { get; }

    /// <summary>
    /// Gets the exception behind <see cref="Reason"/>, if there was one.
    /// </summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Gets the counters of the attempt.
    /// </summary>
    public SegmentedDownloadStats Stats { get; }

    /// <summary>
    /// Creates a result for a finished download.
    /// </summary>
    /// <param name="data">The file.</param>
    /// <param name="stats">The counters.</param>
    /// <returns>The result.</returns>
    internal static SegmentedDownloadResult Completed(byte[] data, SegmentedDownloadStats stats)
        => new(SegmentedDownloadOutcome.Completed, data, null, null, null, stats);

    /// <summary>
    /// Creates a result handing over a whole-file response.
    /// </summary>
    /// <param name="response">The unread response.</param>
    /// <returns>The result.</returns>
    internal static SegmentedDownloadResult Whole(HttpResponseMessage response)
        => new(SegmentedDownloadOutcome.WholeResponse, null, response, null, null, new SegmentedDownloadStats(1, 1, 0, 0, 0, 0));

    /// <summary>
    /// Creates a result for a first response that cannot be split.
    /// </summary>
    /// <param name="reason">Why.</param>
    /// <param name="exception">The exception behind it, if any.</param>
    /// <returns>The result.</returns>
    internal static SegmentedDownloadResult NotSupported(string reason, Exception? exception)
        => new(SegmentedDownloadOutcome.NotSupported, null, null, reason, exception, new SegmentedDownloadStats(1, 1, 0, 0, 0, 0));

    /// <summary>
    /// Creates a result for a download that stopped part way through.
    /// </summary>
    /// <param name="reason">Why.</param>
    /// <param name="exception">The exception behind it, if any.</param>
    /// <param name="stats">The counters up to that point.</param>
    /// <returns>The result.</returns>
    internal static SegmentedDownloadResult GaveUp(string reason, Exception? exception, SegmentedDownloadStats stats)
        => new(SegmentedDownloadOutcome.GaveUp, null, null, reason, exception, stats);
}
