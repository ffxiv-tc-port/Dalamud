using System.IO;

namespace Dalamud.Logging.Retention;

/// <summary>
/// Class implementing log retention behaviour for release builds.
/// </summary>
internal class ReleaseRetentionBehaviour : RetentionBehaviour
{
    /// <summary>
    /// Total size budget for archived (previous session) logs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Archiving is a rename, not a copy, so a single very long session can hand the archive up to
    /// <see cref="RetentionBehaviour.MaxLogSizeBytes"/> *
    /// <see cref="RetentionBehaviour.RetainedLiveFileCountLimit"/> (400MB) in one go. 512MB
    /// therefore guarantees the last session is always kept whole, while normal sessions - which
    /// are a few MB to a few tens of MB - accumulate up to <see cref="MaxArchivedCount"/> of them.
    /// </para>
    /// <para>
    /// Worst case on disk is 400MB live + 512MB archived, i.e. slightly under 1GB. The previous
    /// behaviour capped out at 110MB but only ever retained a single past session, which is what
    /// this trades away.
    /// </para>
    /// </remarks>
    private const long MaxArchivedBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Maximum number of archived logs to keep, whichever of this and
    /// <see cref="MaxArchivedBytes"/> runs out first.
    /// </summary>
    private const int MaxArchivedCount = 20;

    /// <inheritdoc/>
    public override void Apply(FileInfo logFile, FileInfo rolloverFile)
    {
        ArchiveAndPrune(logFile, rolloverFile, MaxArchivedBytes, MaxArchivedCount);
    }
}
