using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Serilog;

namespace Dalamud.Logging.Retention;

/// <summary>
/// Class implementing retention behaviour for log files.
/// </summary>
/// <remarks>
/// <para>Log retention is split across two layers that must not fight over the same files:</para>
/// <list type="number">
/// <item>
/// <description>
/// <b>While the game is running</b>, Serilog owns the "live" files. It writes to
/// <c>dalamud.log</c> and, once that reaches <see cref="MaxLogSizeBytes"/>, rolls forward into
/// <c>dalamud_001.log</c>, <c>dalamud_002.log</c>, ... keeping at most
/// <see cref="RetainedLiveFileCountLimit"/> of them. See <c>EntryPoint.InitLogging</c>.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>At startup</b>, this class renames every live file left over from the previous session
/// into a timestamped archive (<c>dalamud.20260803-142530.old.log</c>) and prunes the archive
/// down to a fixed budget. Because the live files are moved out of the way, Serilog always
/// restarts its sequence at a fresh <c>dalamud.log</c> - so in a normal session
/// <c>dalamud.log</c> is the one and only log file, exactly as before.
/// </description>
/// </item>
/// </list>
/// <para>
/// This replaces the previous behaviour, which kept a single <c>dalamud.old.log</c> and therefore
/// discarded the second-to-last session on every launch.
/// </para>
/// </remarks>
internal abstract class RetentionBehaviour
{
    /// <summary>
    /// Size at which Serilog rolls the live log file over to the next sequence number.
    /// </summary>
    /// <remarks>
    /// Kept at the historical 100MB so that an ordinary session still produces exactly one
    /// <c>dalamud.log</c> and nothing about the user-visible layout changes. What did change is
    /// that <c>rollOnFileSizeLimit</c> is now enabled: without it Serilog simply <i>stops writing</i>
    /// when this limit is hit - no rollover, no exception, no warning - which is how a crash can end
    /// up with hours of missing log.
    /// </remarks>
    public const long MaxLogSizeBytes = 100 * 1024 * 1024;

    /// <summary>
    /// How many live log files Serilog may keep for the current session.
    /// </summary>
    /// <remarks>
    /// Bounds the running session at <see cref="MaxLogSizeBytes"/> * this = 400MB. On a machine that
    /// produces ~100MB of log in six hours that is roughly a full day of continuous play, which is
    /// far more than any crash investigation needs while still being a hard cap.
    /// </remarks>
    public const int RetainedLiveFileCountLimit = 4;

    /// <summary>
    /// Timestamp format embedded in archived log file names. Chosen so that a plain alphabetical
    /// sort in Explorer is also a chronological sort.
    /// </summary>
    private const string ArchiveTimestampFormat = "yyyyMMdd-HHmmss";

    /// <summary>
    /// Apply the specified retention behaviour to log files.
    /// </summary>
    /// <param name="logFile">The regular log file path.</param>
    /// <param name="rolloverFile">The legacy single rollover "old" log file path.</param>
    public abstract void Apply(FileInfo logFile, FileInfo rolloverFile);

    /// <summary>
    /// Move the previous session's log files into the timestamped archive, then prune the archive
    /// back to the given budget.
    /// </summary>
    /// <param name="logFile">The regular log file, e.g. <c>dalamud.log</c>.</param>
    /// <param name="legacyRolloverFile">
    /// The single <c>dalamud.old.log</c> written by older Dalamud versions. It is folded into the
    /// archive once, so that upgrading does not silently orphan it.
    /// </param>
    /// <param name="maxArchivedBytes">Total size budget for all archived logs.</param>
    /// <param name="maxArchivedCount">Maximum number of archived logs to keep.</param>
    protected static void ArchiveAndPrune(
        FileInfo logFile, FileInfo legacyRolloverFile, long maxArchivedBytes, int maxArchivedCount)
    {
        try
        {
            var dir = logFile.Directory;
            if (dir is null)
                return;

            if (!dir.Exists)
                dir.Create();

            // "dalamud" and ".log". Note that logFile may also be "dalamud-<logName>.log".
            var stem = Path.GetFileNameWithoutExtension(logFile.Name);
            var ext = logFile.Extension;
            var escapedStem = Regex.Escape(stem);
            var escapedExt = Regex.Escape(ext);

            // Files Serilog owns while the game runs: dalamud.log, dalamud_001.log, ...
            // Deliberately strict: dalamud.boot.log, dalamud.injector.log and the crash handler's
            // dalamud_appcrash_<stamp>_<pid>.log all start with "dalamud" and end with ".log", and
            // must never be swept up by this.
            var livePattern = new Regex(
                $"^{escapedStem}(_[0-9]{{3,}})?{escapedExt}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            // Archives produced by earlier runs of this method.
            var archivePattern = new Regex(
                $"^{escapedStem}\\.[0-9]{{8}}-[0-9]{{6}}(_[0-9]+)?\\.old{escapedExt}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            // 1. Fold last session's live files into the archive, oldest first so that the archive
            //    timestamps stay in the order the data was actually written.
            foreach (var file in EnumerateMatching(dir, stem, ext, livePattern)
                                 .OrderBy(x => x.LastWriteTimeUtc))
            {
                ArchiveOne(file, dir, stem, ext);
            }

            // 2. Fold the pre-existing single .old.log from older Dalamud versions in as well.
            legacyRolloverFile.Refresh();
            if (legacyRolloverFile.Exists)
                ArchiveOne(legacyRolloverFile, dir, stem, ext);

            // 3. Prune newest-first: keep archives until either budget is exhausted, drop the rest.
            var archives = EnumerateMatching(dir, stem, ext, archivePattern)
                           .OrderByDescending(x => x.LastWriteTimeUtc)
                           .ToList();

            long runningBytes = 0;
            for (var i = 0; i < archives.Count; i++)
            {
                runningBytes += archives[i].Length;
                if (i < maxArchivedCount && runningBytes <= maxArchivedBytes)
                    continue;

                try
                {
                    archives[i].Delete();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to prune archived log {File}", archives[i].FullName);
                }
            }
        }
        catch (Exception ex)
        {
            // Never let log housekeeping take the session down with it.
            Log.Error(ex, "Log retention failed");
        }
    }

    private static IEnumerable<FileInfo> EnumerateMatching(
        DirectoryInfo dir, string stem, string ext, Regex pattern)
    {
        FileInfo[] candidates;
        try
        {
            candidates = dir.GetFiles($"{stem}*{ext}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to enumerate log directory {Dir}", dir.FullName);
            return Array.Empty<FileInfo>();
        }

        return candidates.Where(x => pattern.IsMatch(x.Name));
    }

    private static void ArchiveOne(FileInfo file, DirectoryInfo dir, string stem, string ext)
    {
        try
        {
            file.Refresh();
            if (!file.Exists)
                return;

            // An empty leftover carries no information, and leaving it behind would make Serilog
            // resume into it instead of starting a clean sequence.
            if (file.Length == 0)
            {
                file.Delete();
                return;
            }

            var stamp = file.LastWriteTime.ToString(ArchiveTimestampFormat, CultureInfo.InvariantCulture);
            // '_' rather than '-' as the collision separator, so that an ordinal sort still puts
            // the un-suffixed name first ('.' 0x2E < '_' 0x5F). Collisions only happen when one
            // session rolled several times within the same second; the timestamp is taken from the
            // file's own last-write time, so sorting by date is always correct regardless.
            var target = Path.Combine(dir.FullName, $"{stem}.{stamp}.old{ext}");
            for (var n = 1; File.Exists(target) && n < 1000; n++)
                target = Path.Combine(dir.FullName, $"{stem}.{stamp}_{n}.old{ext}");

            if (File.Exists(target))
                return;

            file.MoveTo(target);
        }
        catch (Exception ex)
        {
            // Most likely another instance still holds the file. Leaving it in place is safe:
            // Serilog will simply append to it and its size limit still applies.
            Log.Error(ex, "Failed to archive log {File}", file.FullName);
        }
    }
}
