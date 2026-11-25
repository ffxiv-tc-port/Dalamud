using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Dalamud.Game.Network.Internal.MarketBoardUploaders;

/// <summary>
/// Counts what was actually sent to the market board data site and writes one summary line per interval.
/// </summary>
/// <remarks>
/// Contract: callable from any thread; the log delegate is always invoked outside the lock; an interval
/// starts at its first counted event, so an interval with nothing in it never produces a line.
/// Only world ids and counts are kept; nothing about the character.
/// </remarks>
internal sealed class MarketBoardUploadStats
{
    /// <summary>Fixed prefix of every line this class writes.</summary>
    public const string LogPrefix = "[MbUploadStats]";

    /// <summary>A summary line is written this many milliseconds after the first event of an interval.</summary>
    public const long IntervalMs = 600_000;

    /// <summary>Upper bound of distinct worlds listed in one line; the rest are summed.</summary>
    public const int MaxWorldsListed = 32;

    private const int KindCount = 3;
    private const int ReasonCount = 5;

    private readonly Func<long> clockMs;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly long[] ok = new long[KindCount];
    private readonly long[] failed = new long[KindCount];
    private readonly long[] skipped = new long[ReasonCount];
    private readonly HashSet<uint> items = [];
    private readonly SortedDictionary<uint, long> worlds = [];
    private long disabled;
    private long countMismatch;
    private long windowStartMs;
    private bool pending;

    // long.MaxValue＝目前沒有待寫的區間；每幀只讀這一個欄位。
    private long dueMs = long.MaxValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarketBoardUploadStats"/> class.
    /// </summary>
    /// <param name="clockMs">Monotonic clock in milliseconds.</param>
    /// <param name="log">Receives each finished summary line.</param>
    public MarketBoardUploadStats(Func<long> clockMs, Action<string> log)
    {
        this.clockMs = clockMs;
        this.log = log;
    }

    /// <summary>
    /// Records one finished upload.
    /// </summary>
    /// <param name="kind">Upload path.</param>
    /// <param name="itemId">Item id, 0 when the path has none.</param>
    /// <param name="worldId">World the upload was attributed to.</param>
    /// <param name="succeeded">Whether the site answered with a success status.</param>
    public void NoteUpload(MarketBoardUploadGuard.Kind kind, uint itemId, uint worldId, bool succeeded)
    {
        var k = (int)kind;
        if ((uint)k >= KindCount)
            return;

        lock (this.gate)
        {
            this.Touch();
            if (succeeded)
                this.ok[k]++;
            else
                this.failed[k]++;

            if (kind == MarketBoardUploadGuard.Kind.Listings && itemId != 0)
                this.items.Add(itemId);

            this.worlds.TryGetValue(worldId, out var n);
            this.worlds[worldId] = n + 1;
        }
    }

    /// <summary>
    /// Records one upload the guard did not let through.
    /// </summary>
    /// <param name="reason">Why it was skipped.</param>
    public void NoteSkip(MarketBoardUploadGuard.SkipReason reason)
    {
        var r = (int)reason;
        if (r <= 0 || r >= ReasonCount)
            return;

        lock (this.gate)
        {
            this.Touch();
            this.skipped[r]++;
        }
    }

    /// <summary>
    /// Records one upload not attempted because uploading is turned off.
    /// </summary>
    public void NoteDisabled()
    {
        lock (this.gate)
        {
            this.Touch();
            this.disabled++;
        }
    }

    /// <summary>
    /// Records one item whose listings did not add up to the announced count and was not uploaded.
    /// </summary>
    public void NoteCountMismatch()
    {
        lock (this.gate)
        {
            this.Touch();
            this.countMismatch++;
        }
    }

    /// <summary>
    /// Writes the summary once the interval is due; cheap when nothing is pending.
    /// </summary>
    public void Tick()
    {
        if (Volatile.Read(ref this.dueMs) == long.MaxValue)
            return;
        if (this.clockMs() < Volatile.Read(ref this.dueMs))
            return;

        var line = this.TakeLine(false);
        if (line != null)
            this.log(line);
    }

    /// <summary>
    /// Writes whatever is still pending, regardless of the interval.
    /// </summary>
    public void Flush()
    {
        var line = this.TakeLine(true);
        if (line != null)
            this.log(line);
    }

    /// <summary>
    /// Builds the summary line and starts a new interval.
    /// </summary>
    /// <param name="force">Take the line even if the interval is not due yet.</param>
    /// <returns>The line, or null when nothing is pending or it is not due.</returns>
    public string? TakeLine(bool force)
    {
        lock (this.gate)
        {
            if (!this.pending)
                return null;

            var now = this.clockMs();
            if (!force && now < this.dueMs)
                return null;

            var line = this.BuildLine(now - this.windowStartMs);
            Array.Clear(this.ok);
            Array.Clear(this.failed);
            Array.Clear(this.skipped);
            this.items.Clear();
            this.worlds.Clear();
            this.disabled = 0;
            this.countMismatch = 0;
            this.pending = false;
            Volatile.Write(ref this.dueMs, long.MaxValue);
            return line;
        }
    }

    private static string Pair(long ok, long failed) => failed == 0 ? $"{ok}" : $"{ok}（失敗 {failed}）";

    private void Touch()
    {
        if (this.pending)
            return;

        this.pending = true;
        this.windowStartMs = this.clockMs();
        Volatile.Write(ref this.dueMs, this.windowStartMs + IntervalMs);
    }

    private string BuildLine(long spanMs)
    {
        const int listings = (int)MarketBoardUploadGuard.Kind.Listings;
        const int tax = (int)MarketBoardUploadGuard.Kind.Tax;
        const int purchase = (int)MarketBoardUploadGuard.Kind.Purchase;

        var sb = new StringBuilder(256);
        sb.Append(LogPrefix)
          .Append(" 市場資料上傳統計（")
          .Append(spanMs < 60_000 ? "不到 1" : (spanMs / 60_000).ToString()).Append(" 分鐘內）：")
          .Append("道具 成功 ").Append(this.ok[listings]).Append("／失敗 ").Append(this.failed[listings])
          .Append("（不同道具 ").Append(this.items.Count).Append("）")
          .Append("、稅率 ").Append(Pair(this.ok[tax], this.failed[tax]))
          .Append("、刪單 ").Append(Pair(this.ok[purchase], this.failed[purchase]))
          .Append("；掛單筆數不符而放棄 ").Append(this.countMismatch);

        long skipTotal = this.disabled;
        for (var i = 1; i < ReasonCount; i++)
            skipTotal += this.skipped[i];

        sb.Append("；未上傳 ").Append(skipTotal);
        if (skipTotal > 0)
        {
            var parts = new List<string>(ReasonCount);
            AddPart(parts, "無角色", this.skipped[(int)MarketBoardUploadGuard.SkipReason.NoLocalPlayer]);
            AddPart(parts, "世界為 0", this.skipped[(int)MarketBoardUploadGuard.SkipReason.PlayerWorldZero]);
            AddPart(parts, "已停運世界", this.skipped[(int)MarketBoardUploadGuard.SkipReason.RetiredWorld]);
            AddPart(parts, "讀取世界失敗", this.skipped[(int)MarketBoardUploadGuard.SkipReason.ResolveFailed]);
            AddPart(parts, "設定已關閉上傳", this.disabled);
            sb.Append('（').Append(string.Join("、", parts)).Append('）');
        }

        sb.Append("；世界 ");
        if (this.worlds.Count == 0)
        {
            sb.Append("無");
        }
        else
        {
            var listed = 0;
            long rest = 0;
            foreach (var (world, count) in this.worlds)
            {
                if (listed >= MaxWorldsListed)
                {
                    rest += count;
                    continue;
                }

                if (listed > 0)
                    sb.Append(' ');
                sb.Append(world).Append('×').Append(count);
                listed++;
            }

            if (rest > 0)
                sb.Append(" 其他×").Append(rest);
        }

        return sb.ToString();

        static void AddPart(List<string> parts, string name, long count)
        {
            if (count > 0)
                parts.Add($"{name} {count}");
        }
    }
}
