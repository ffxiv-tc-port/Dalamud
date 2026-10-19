using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using CheapLoc;

namespace Dalamud.Plugin.Internal.Types;

/// <summary>
/// Builds the text and progress fraction shown while plugins download and install.
/// </summary>
/// <remarks>
/// Not thread-safe: a display belongs to one consumer, which refreshes and reads it from one thread at a time.
/// Its inputs, <see cref="PluginDownloadProgress"/> and <see cref="PluginUpdateBatchProgress"/>, may be read from any thread.
/// </remarks>
internal sealed class PluginUpdateProgressDisplay
{
    /// <summary>
    /// The shortest time between two rebuilds of the text, unless a refresh is forced.
    /// </summary>
    public const long RefreshIntervalMs = 500;

    private const long StalledAfterMs = 5000;
    private const int MaxDownloadLines = 3;

    // A plugin's share of the batch bar: the download fills most of it, loading the new version the rest.
    private const double DownloadShare = 0.9;

    private readonly Dictionary<string, PluginDownloadProgress> batchDownloads = new();
    private readonly List<string> lines = new();
    private long lastRefreshTicks;
    private bool hasRefreshed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginUpdateProgressDisplay"/> class.
    /// </summary>
    /// <param name="batch">The batch to summarise, or <c>null</c> to only show the downloads in flight.</param>
    public PluginUpdateProgressDisplay(PluginUpdateBatchProgress? batch = null)
    {
        this.Batch = batch;
    }

    /// <summary>
    /// Gets the batch this display summarises, if any.
    /// </summary>
    public PluginUpdateBatchProgress? Batch { get; }

    /// <summary>
    /// Gets the lines built by the last refresh.
    /// </summary>
    public IReadOnlyList<string> Lines => this.lines;

    /// <summary>
    /// Gets the lines built by the last refresh as one string.
    /// </summary>
    public string Text => string.Join("\n", this.lines);

    /// <summary>
    /// Gets the overall progress from 0 to 1, or <c>null</c> when it cannot be told.
    /// </summary>
    public float? Fraction { get; private set; }

    /// <summary>
    /// Rebuilds the text from the downloads in flight, at most once per <see cref="RefreshIntervalMs"/>.
    /// </summary>
    /// <param name="activeDownloads">The downloads in flight, from <see cref="PluginManager.ActiveDownloads"/>.</param>
    /// <param name="force">Whether to rebuild even if the last rebuild was recent.</param>
    /// <returns>Whether the text was rebuilt.</returns>
    public bool Refresh(IReadOnlyList<PluginDownloadProgress> activeDownloads, bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && this.hasRefreshed && now - this.lastRefreshTicks < RefreshIntervalMs)
            return false;

        this.hasRefreshed = true;
        this.lastRefreshTicks = now;
        this.Rebuild(activeDownloads, now);
        return true;
    }

    private static double DownloadedFraction(PluginDownloadProgress download)
    {
        var total = download.TotalBytes;
        return total > 0 ? Math.Clamp(download.BytesReceived / (double)total, 0, 1) : 0;
    }

    private static long Remaining(PluginDownloadProgress download)
    {
        var total = download.TotalBytes;
        return total > 0 ? Math.Max(0, total - download.BytesReceived) : long.MaxValue;
    }

    private static long EffectiveSpeed(PluginDownloadProgress download, long now)
        => now - download.LastDataTicks >= StalledAfterMs ? 0 : download.BytesPerSecond;

    private static string FormatMegabytes(long bytes)
        => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

    private static string FormatSpeed(long bytesPerSecond)
        => bytesPerSecond >= 1024 * 1024
               ? (bytesPerSecond / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s"
               : (bytesPerSecond / 1024).ToString(CultureInfo.InvariantCulture) + " KB/s";

    private static string FormatSize(long received, long total, bool totalKnown)
        => totalKnown
               ? string.Format(Locs.SizeKnown, FormatMegabytes(received), FormatMegabytes(total))
               : string.Format(Locs.SizeUnknown, FormatMegabytes(received));

    private static string FormatDownload(PluginDownloadProgress download, long now)
    {
        var quietMs = now - download.LastDataTicks;
        if (!download.HasResponse && quietMs < StalledAfterMs)
            return string.Format(Locs.ConnectingLine, download.Name);

        var received = download.BytesReceived;
        var total = download.TotalBytes;
        return string.Format(
            Locs.DownloadLine,
            download.Name,
            FormatSize(received, total, total > 0),
            FormatStatus(download, received, total, quietMs));
    }

    private static string FormatStatus(PluginDownloadProgress download, long received, long total, long quietMs)
    {
        if (quietMs >= StalledAfterMs)
            return string.Format(Locs.Stalled, quietMs / 1000);

        var speed = download.BytesPerSecond;
        if (total <= 0 || speed <= 0)
            return FormatSpeed(speed);

        var seconds = Math.Max(0, total - received) / (double)speed;
        var eta = seconds < 60
                      ? Locs.EtaUnderMinute
                      : string.Format(Locs.EtaMinutes, (long)Math.Ceiling(seconds / 60));
        return string.Format(Locs.SpeedWithEta, FormatSpeed(speed), eta);
    }

    private static string FormatAggregate(List<PluginDownloadProgress> downloads, long now)
    {
        long received = 0, total = 0, speed = 0;
        var totalKnown = true;
        foreach (var download in downloads)
        {
            received += download.BytesReceived;
            speed += EffectiveSpeed(download, now);

            var size = download.TotalBytes;
            if (size > 0)
                total += size;
            else
                totalKnown = false;
        }

        return string.Format(
            Locs.BatchDownloading,
            downloads.Count,
            FormatSize(received, total, totalKnown),
            FormatSpeed(speed));
    }

    private void Rebuild(IReadOnlyList<PluginDownloadProgress> activeDownloads, long now)
    {
        var downloads = new List<PluginDownloadProgress>();
        foreach (var download in activeDownloads)
        {
            if (this.Batch is not null)
            {
                if (!this.Batch.Contains(download.InternalName))
                    continue;

                // Kept after the download ends, so its share of the bar does not drop back while the plugin loads.
                this.batchDownloads[download.InternalName] = download;
            }

            downloads.Add(download);
        }

        // Largest remaining first: that download decides when everything is done.
        downloads.Sort((a, b) =>
        {
            var byRemaining = Remaining(b).CompareTo(Remaining(a));
            return byRemaining != 0 ? byRemaining : string.CompareOrdinal(a.Name, b.Name);
        });

        var processed = this.Batch?.SnapshotProcessed();

        this.lines.Clear();
        if (this.Batch is not null && processed is not null)
        {
            this.lines.Add(string.Format(Locs.BatchCompleted, processed.Count, this.Batch.Total));
            if (downloads.Count > 1)
                this.lines.Add(FormatAggregate(downloads, now));
        }

        foreach (var download in downloads.Take(MaxDownloadLines))
            this.lines.Add(FormatDownload(download, now));

        if (downloads.Count > MaxDownloadLines)
            this.lines.Add(string.Format(Locs.MoreDownloads, downloads.Count - MaxDownloadLines));

        if (processed is not null)
        {
            // Downloaded but not finished yet: the old version is being swapped for the new one.
            var installing = this.batchDownloads.Values
                                 .Where(x => !processed.Contains(x.InternalName) && !downloads.Contains(x))
                                 .Select(x => x.Name)
                                 .ToList();
            if (installing.Count > 0)
                this.lines.Add(string.Format(Locs.Installing, string.Join(", ", installing)));
        }

        this.Fraction = this.ComputeFraction(downloads, processed);
    }

    private float? ComputeFraction(List<PluginDownloadProgress> downloads, HashSet<string>? processed)
    {
        if (this.Batch is null || processed is null)
        {
            return downloads.Count == 1 && downloads[0].TotalBytes > 0
                       ? (float)DownloadedFraction(downloads[0])
                       : null;
        }

        if (this.Batch.Total <= 0)
            return null;

        double done = processed.Count;
        foreach (var download in this.batchDownloads.Values)
        {
            if (!processed.Contains(download.InternalName))
                done += DownloadShare * DownloadedFraction(download);
        }

        return (float)Math.Clamp(done / this.Batch.Total, 0, 1);
    }

    private static class Locs
    {
        public static string BatchCompleted => Loc.Localize("PluginProgressBatchCompleted", "已完成 {0} / {1} 個插件");

        public static string BatchDownloading => Loc.Localize("PluginProgressBatchDownloading", "正在下載 {0} 個插件：{1}（{2}）");

        public static string DownloadLine => Loc.Localize("PluginProgressDownloadLine", "{0}：{1}（{2}）");

        public static string SizeKnown => Loc.Localize("PluginProgressSizeKnown", "{0} / {1} MB");

        public static string SizeUnknown => Loc.Localize("PluginProgressSizeUnknown", "已下載 {0} MB");

        public static string SpeedWithEta => Loc.Localize("PluginProgressSpeedWithEta", "{0}，{1}");

        public static string EtaMinutes => Loc.Localize("PluginProgressEtaMinutes", "約剩 {0} 分鐘");

        public static string EtaUnderMinute => Loc.Localize("PluginProgressEtaUnderMinute", "剩不到 1 分鐘");

        public static string Stalled => Loc.Localize("PluginProgressStalled", "已 {0} 秒沒有收到資料");

        public static string ConnectingLine => Loc.Localize("PluginProgressConnectingLine", "{0}：連線中");

        public static string MoreDownloads => Loc.Localize("PluginProgressMoreDownloads", "……另有 {0} 個正在下載");

        // Same key and fallback as the auto-updater used before, so the existing translation still applies.
        public static string Installing => Loc.Localize("AutoUpdateUpdating", "Updating {0}...");
    }
}
