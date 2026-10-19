using System.Threading;

namespace Dalamud.Plugin.Internal.Types;

/// <summary>
/// Live state of one plugin download, for progress displays and the download log.
/// </summary>
/// <remarks>
/// Only the download loop writes, and it never runs concurrently with itself; any thread may read.
/// Every published value is read and written atomically, so readers never see a torn number.
/// </remarks>
internal sealed class PluginDownloadProgress
{
    private const int SpeedSampleCapacity = 10;
    private const long SpeedSampleSpacingMs = 1000;
    private const long MinimumSpeedSpanMs = 250;
    private const long LogIntervalMs = 30_000;

    private readonly long[] sampleTicks = new long[SpeedSampleCapacity];
    private readonly long[] sampleBytes = new long[SpeedSampleCapacity];
    private int sampleCount;
    private int sampleNewest = -1;

    private long nextLogAtMs = LogIntervalMs;
    private long logWindowStartTicks;
    private long logWindowStartBytes;
    private long longestPauseMs;

    private long totalBytes = -1;
    private long bytesReceived;
    private long lastDataTicks;
    private long bytesPerSecond;
    private int hasResponse;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginDownloadProgress"/> class.
    /// </summary>
    /// <param name="internalName">The internal name of the plugin being downloaded.</param>
    /// <param name="name">The display name of the plugin being downloaded.</param>
    public PluginDownloadProgress(string internalName, string name)
    {
        this.InternalName = internalName;
        this.Name = name;
        this.StartTicks = Environment.TickCount64;
        this.lastDataTicks = this.logWindowStartTicks = this.StartTicks;
        this.AddSpeedSample(this.StartTicks, 0);
    }

    /// <summary>
    /// Gets the internal name of the plugin being downloaded.
    /// </summary>
    public string InternalName { get; }

    /// <summary>
    /// Gets the display name of the plugin being downloaded.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the <see cref="Environment.TickCount64"/> value at which the download started.
    /// </summary>
    public long StartTicks { get; }

    /// <summary>
    /// Gets a value indicating whether the server has answered with its response headers.
    /// </summary>
    public bool HasResponse => Volatile.Read(ref this.hasResponse) != 0;

    /// <summary>
    /// Gets the size announced by the server, or -1 when it did not send a Content-Length.
    /// </summary>
    public long TotalBytes => Interlocked.Read(ref this.totalBytes);

    /// <summary>
    /// Gets the number of bytes received so far.
    /// </summary>
    public long BytesReceived => Interlocked.Read(ref this.bytesReceived);

    /// <summary>
    /// Gets the <see cref="Environment.TickCount64"/> value of the last received data, or of the start if none yet.
    /// </summary>
    public long LastDataTicks => Interlocked.Read(ref this.lastDataTicks);

    /// <summary>
    /// Gets the transfer rate over about the last ten seconds, as of the last received data.
    /// </summary>
    public long BytesPerSecond => Interlocked.Read(ref this.bytesPerSecond);

    /// <summary>
    /// Records that the response headers arrived.
    /// </summary>
    /// <param name="contentLength">The Content-Length of the response, if any.</param>
    internal void OnResponse(long? contentLength)
    {
        if (contentLength is > 0)
            Interlocked.Exchange(ref this.totalBytes, contentLength.Value);

        Volatile.Write(ref this.hasResponse, 1);
    }

    /// <summary>
    /// Records a received chunk.
    /// </summary>
    /// <param name="count">The number of bytes in the chunk.</param>
    /// <param name="logSample">When a log line is due, the statistics since the previous one.</param>
    /// <returns>Whether a periodic progress log line is due.</returns>
    internal bool OnData(int count, out LogSample logSample)
    {
        var now = Environment.TickCount64;
        var received = this.bytesReceived + count;
        this.longestPauseMs = Math.Max(this.longestPauseMs, now - this.lastDataTicks);

        Interlocked.Exchange(ref this.bytesReceived, received);
        Interlocked.Exchange(ref this.lastDataTicks, now);

        if (now - this.sampleTicks[this.sampleNewest] >= SpeedSampleSpacingMs)
            this.AddSpeedSample(now, received);

        var oldest = (this.sampleNewest - this.sampleCount + 1 + SpeedSampleCapacity) % SpeedSampleCapacity;
        var span = now - this.sampleTicks[oldest];
        if (span >= MinimumSpeedSpanMs)
            Interlocked.Exchange(ref this.bytesPerSecond, (received - this.sampleBytes[oldest]) * 1000 / span);

        var elapsed = now - this.StartTicks;
        if (elapsed < this.nextLogAtMs)
        {
            logSample = default;
            return false;
        }

        logSample = new LogSample(
            elapsed,
            now - this.logWindowStartTicks,
            received - this.logWindowStartBytes,
            this.longestPauseMs);

        while (this.nextLogAtMs <= elapsed)
            this.nextLogAtMs += LogIntervalMs;

        this.logWindowStartTicks = now;
        this.logWindowStartBytes = received;
        this.longestPauseMs = 0;
        return true;
    }

    /// <summary>
    /// Forgets the bytes received so far, because the download starts over from the first byte.
    /// The periodic log keeps its schedule; the size stays known until a new response says otherwise.
    /// </summary>
    internal void OnRestart()
    {
        var now = Environment.TickCount64;
        Interlocked.Exchange(ref this.bytesReceived, 0);
        Interlocked.Exchange(ref this.bytesPerSecond, 0);
        Interlocked.Exchange(ref this.lastDataTicks, now);
        Volatile.Write(ref this.hasResponse, 0);

        this.sampleCount = 0;
        this.sampleNewest = -1;
        this.AddSpeedSample(now, 0);
        this.logWindowStartBytes = 0;
    }

    private void AddSpeedSample(long ticks, long bytes)
    {
        this.sampleNewest = (this.sampleNewest + 1) % SpeedSampleCapacity;
        this.sampleTicks[this.sampleNewest] = ticks;
        this.sampleBytes[this.sampleNewest] = bytes;
        this.sampleCount = Math.Min(this.sampleCount + 1, SpeedSampleCapacity);
    }

    /// <summary>
    /// Statistics for one periodic download log line.
    /// </summary>
    /// <param name="ElapsedMs">Time since the download started.</param>
    /// <param name="WindowMs">Time since the previous log line, or since the start for the first one.</param>
    /// <param name="WindowBytes">Bytes received within that window.</param>
    /// <param name="LongestPauseMs">The longest gap between two chunks within that window.</param>
    internal readonly record struct LogSample(long ElapsedMs, long WindowMs, long WindowBytes, long LongestPauseMs);
}
