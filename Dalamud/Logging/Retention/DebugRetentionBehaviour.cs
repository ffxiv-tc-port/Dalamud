using System.IO;

namespace Dalamud.Logging.Retention;

/// <summary>
/// Class implementing log retention behaviour for debug builds.
/// </summary>
internal class DebugRetentionBehaviour : RetentionBehaviour
{
    /// <summary>
    /// Total size budget for archived logs. Smaller than the release budget because a development
    /// checkout is rebuilt and relaunched constantly, so the archive would otherwise fill up with
    /// dozens of near-identical short sessions.
    /// </summary>
    private const long MaxArchivedBytes = 128L * 1024 * 1024;

    /// <summary>
    /// Maximum number of archived logs to keep.
    /// </summary>
    private const int MaxArchivedCount = 10;

    /// <inheritdoc/>
    public override void Apply(FileInfo logFile, FileInfo rolloverFile)
    {
        ArchiveAndPrune(logFile, rolloverFile, MaxArchivedBytes, MaxArchivedCount);
    }
}
