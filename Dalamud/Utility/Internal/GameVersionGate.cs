using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Dalamud.Common;
using Dalamud.Common.Game;
using Dalamud.Logging.Internal;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Asks before starting Dalamud on a game version it was not verified against.
/// </summary>
/// <remarks>
/// The list is <see cref="FileName"/> next to Dalamud.dll: one version per line, written exactly as in ffxivgame.ver;
/// <c>#</c> starts a comment. A missing, unreadable or empty list and an unknown game version start Dalamud as before,
/// with a warning: the gate itself must never be the reason Dalamud does not run.
/// </remarks>
internal static class GameVersionGate
{
    /// <summary>The list file, in Dalamud's working directory.</summary>
    internal const string FileName = "verified-game-versions.txt";

    /// <summary>The message box title.</summary>
    internal const string PromptCaption = "台服 Dalamud：遊戲版本未驗證";

    private const int ShownVersions = 3;

    private static readonly ModuleLog Log = new("VERSIONGATE");

    private static readonly Regex VersionLine = new(@"^\d{4}\.\d{2}\.\d{2}\.\d{4}\.\d{4}$", RegexOptions.CultureInvariant);

    /// <summary>What the gate found.</summary>
    internal enum Verdict
    {
        /// <summary>The game version is in the list.</summary>
        Verified,

        /// <summary>The list is usable and the game version is not in it.</summary>
        NotVerified,

        /// <summary>The game version is missing or incomplete.</summary>
        VersionUnknown,

        /// <summary>The list file does not exist.</summary>
        ListMissing,

        /// <summary>The list file could not be read.</summary>
        ListUnreadable,

        /// <summary>The list file has no valid version line.</summary>
        ListEmpty,
    }

    /// <summary>
    /// Decides whether Dalamud may start, asking the user if the game version is not verified. Never throws.
    /// </summary>
    /// <param name="info">The start info; its working directory holds the list.</param>
    /// <param name="askLoadAnyway">Shows the question (text, caption); true means "load anyway".</param>
    /// <returns>Whether to start Dalamud.</returns>
    internal static bool Check(DalamudStartInfo info, Func<string, string, bool> askLoadAnyway)
    {
        try
        {
            var listPath = Path.Combine(info.WorkingDirectory ?? string.Empty, FileName);
            var evaluation = Evaluate(info.GameVersion, () => File.ReadAllText(listPath));
            return Apply(evaluation, listPath, askLoadAnyway);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "The version gate failed; starting normally");
            return true;
        }
    }

    /// <summary>
    /// Reads the list text.
    /// </summary>
    /// <param name="text">The file contents.</param>
    /// <returns>The valid versions in file order, and the raw lines that are neither blank, comments nor versions.</returns>
    internal static (List<string> Versions, List<string> InvalidLines) Parse(string text)
    {
        var versions = new List<string>();
        var invalid = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0)
                line = line[..hash];

            line = line.Trim().Trim('\uFEFF').Trim();
            if (line.Length == 0)
                continue;

            if (VersionLine.IsMatch(line))
                versions.Add(line);
            else
                invalid.Add(raw.TrimEnd('\r'));
        }

        return (versions, invalid);
    }

    /// <summary>
    /// Compares the game version with the list. Never throws for a list that cannot be read.
    /// </summary>
    /// <param name="current">The running game version.</param>
    /// <param name="readList">Returns the list text; may throw.</param>
    /// <returns>The evaluation.</returns>
    internal static Evaluation Evaluate(GameVersion? current, Func<string> readList)
    {
        var currentText = current is { Year: >= 0, Month: >= 0, Day: >= 0, Major: >= 0, Minor: >= 0 }
                              ? current.ToString()
                              : null;
        if (currentText == null)
            return new(Verdict.VersionUnknown, null, [], [], null);

        string text;
        try
        {
            text = readList();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(Verdict.ListMissing, currentText, [], [], ex);
        }
        catch (Exception ex)
        {
            return new(Verdict.ListUnreadable, currentText, [], [], ex);
        }

        var (versions, invalid) = Parse(text);
        if (versions.Count == 0)
            return new(Verdict.ListEmpty, currentText, versions, invalid, null);

        var verdict = versions.Contains(currentText, StringComparer.Ordinal) ? Verdict.Verified : Verdict.NotVerified;
        return new(verdict, currentText, versions, invalid, null);
    }

    /// <summary>
    /// Builds the question shown for an unverified version.
    /// </summary>
    /// <param name="current">The running game version.</param>
    /// <param name="verified">The verified versions.</param>
    /// <returns>The message box text.</returns>
    internal static string FormatPrompt(string current, IReadOnlyCollection<string> verified)
    {
        var newest = verified.Distinct(StringComparer.Ordinal)
                             .OrderByDescending(v => v, StringComparer.Ordinal)
                             .ToList();
        var shown = string.Join("、", newest.Take(ShownVersions));
        if (newest.Count > ShownVersions)
            shown += $" 等 {newest.Count} 個";

        return $"台服 Dalamud 尚未驗證遊戲版本 {current}（已驗證：{shown}）。\n" +
               "改版後遊戲內部位置可能已變，直接載入可能讓遊戲崩潰。\n\n" +
               "否（建議）：這次不載入 Dalamud 與插件，遊戲照常；更新 Dalamud 後即可恢復。\n" +
               "是：仍要載入（風險自負）。\n\n" +
               $"Dalamud has not been verified against game version {current}. Choose No to skip Dalamud and plugins for this session.";
    }

    /// <summary>
    /// Logs the evaluation and, for an unverified version, asks the user.
    /// </summary>
    /// <param name="evaluation">The evaluation.</param>
    /// <param name="listPath">The list file, for the log.</param>
    /// <param name="askLoadAnyway">Shows the question (text, caption); true means "load anyway". A throw counts as "no".</param>
    /// <returns>Whether to start Dalamud.</returns>
    internal static bool Apply(Evaluation evaluation, string listPath, Func<string, string, bool> askLoadAnyway)
    {
        if (evaluation.InvalidLines.Count > 0)
        {
            Log.Warning(
                "Ignored {Count} line(s) of {Path} that are not versions like 2026.09.14.0000.0000: {Lines}",
                evaluation.InvalidLines.Count,
                listPath,
                string.Join(" | ", evaluation.InvalidLines));
        }

        switch (evaluation.Verdict)
        {
            case Verdict.Verified:
                Log.Information("Game version {Version} is verified; starting normally", evaluation.Current!);
                return true;

            case Verdict.VersionUnknown:
                Log.Warning("The game version is unknown; the gate is skipped and Dalamud starts normally");
                return true;

            case Verdict.ListMissing:
            case Verdict.ListUnreadable:
            case Verdict.ListEmpty:
                Log.Warning(
                    evaluation.Error,
                    "The verified version list {Path} is unusable ({Verdict}); the gate is skipped and game version {Version} starts normally",
                    listPath,
                    evaluation.Verdict,
                    evaluation.Current!);
                return true;
        }

        Log.Warning(
            "Game version {Version} is not verified (verified: {Verified}); asking whether to load Dalamud",
            evaluation.Current!,
            string.Join(", ", evaluation.Verified));

        bool loadAnyway;
        try
        {
            loadAnyway = askLoadAnyway(FormatPrompt(evaluation.Current!, evaluation.Verified), PromptCaption);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not ask; treating it as no");
            loadAnyway = false;
        }

        if (loadAnyway)
        {
            Log.Warning("The user chose to load Dalamud on unverified game version {Version} (override for this session only)", evaluation.Current!);
            return true;
        }

        Log.Warning("Dalamud and plugins are not loaded this session: game version {Version} is not verified", evaluation.Current!);
        return false;
    }

    /// <summary>The result of <see cref="Evaluate"/>.</summary>
    /// <param name="Verdict">What was found.</param>
    /// <param name="Current">The game version as written in the list, or null if unknown.</param>
    /// <param name="Verified">The valid versions in the list.</param>
    /// <param name="InvalidLines">Lines that were neither blank, comments nor versions.</param>
    /// <param name="Error">Why the list could not be read, if so.</param>
    internal sealed record Evaluation(
        Verdict Verdict,
        string? Current,
        IReadOnlyList<string> Verified,
        IReadOnlyList<string> InvalidLines,
        Exception? Error);
}
