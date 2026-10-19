using System;
using System.Collections.Generic;

namespace Dalamud.Game.Network.Internal.MarketBoardUploaders;

/// <summary>
/// Decides which world a market board upload is attributed to, and keeps the diagnostics bounded.
/// </summary>
/// <remarks>
/// Contract: never touches game memory (the caller reads the worlds and passes them in); callable from
/// any thread; the log delegate is always invoked outside the lock. Lobby values only feed diagnostics.
/// </remarks>
internal sealed class MarketBoardUploadGuard
{
    /// <summary>Fixed prefix of every line this class writes.</summary>
    public const string LogPrefix = "[MbUploadGuard]";

    /// <summary>Same (kind, reason) skip is written at most once per this many milliseconds.</summary>
    public const long SkipLogWindowMs = 60_000;

    /// <summary>Upper bound of distinct world combinations written per session.</summary>
    public const int MaxWorldCombos = 64;

    private const int KindCount = 3;
    private const int ReasonCount = 5;

    private readonly Func<long> clockMs;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly SkipStat[] skips = new SkipStat[KindCount * ReasonCount];
    private readonly long[] passed = new long[KindCount];
    private readonly HashSet<Worlds> seenCombos = [];
    private bool comboOverflowLogged;
    private bool disabledLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarketBoardUploadGuard"/> class.
    /// </summary>
    /// <param name="clockMs">Monotonic clock in milliseconds.</param>
    /// <param name="log">Receives each finished diagnostic line.</param>
    public MarketBoardUploadGuard(Func<long> clockMs, Action<string> log)
    {
        this.clockMs = clockMs;
        this.log = log;
    }

    /// <summary>The three upload paths.</summary>
    public enum Kind
    {
        /// <summary>Listings and sale history of one item.</summary>
        Listings = 0,

        /// <summary>Market tax rates.</summary>
        Tax = 1,

        /// <summary>Removal of one listing after a purchase.</summary>
        Purchase = 2,
    }

    /// <summary>Why an upload is not sent.</summary>
    public enum SkipReason
    {
        /// <summary>The upload goes ahead.</summary>
        None = 0,

        /// <summary>There is no local player object.</summary>
        NoLocalPlayer = 1,

        /// <summary>The local player object reports world 0.</summary>
        PlayerWorldZero = 2,

        /// <summary>The local player object reports a world that no longer exists.</summary>
        RetiredWorld = 3,

        /// <summary>Reading the worlds threw.</summary>
        ResolveFailed = 4,
    }

    /// <summary>
    /// Whether the world has been shut down.
    /// </summary>
    /// <param name="worldId">World row id.</param>
    /// <returns>True for a world that must never receive uploads.</returns>
    public static bool IsRetiredWorld(uint worldId)
    {
        // 台服「拉姆」已停運併入迦樓羅，那裡不可能再有新的掛售。
        // 刻意不用 World 表的 IsPublic 判：台服整張表都是 False。
        return worldId is 4034;
    }

    /// <summary>
    /// Pure decision: the upload world is the local player's world or nothing.
    /// </summary>
    /// <param name="worlds">Worlds as read by the caller.</param>
    /// <returns><see cref="SkipReason.None"/> when the upload may use <see cref="Worlds.PlayerWorld"/>.</returns>
    public static SkipReason Decide(in Worlds worlds)
    {
        if (!worlds.HasPlayer)
            return SkipReason.NoLocalPlayer;
        if (worlds.PlayerWorld == 0)
            return SkipReason.PlayerWorldZero;
        return IsRetiredWorld(worlds.PlayerWorld) ? SkipReason.RetiredWorld : SkipReason.None;
    }

    /// <summary>
    /// Decides one upload and writes the diagnostics that go with the decision.
    /// </summary>
    /// <param name="kind">Upload path.</param>
    /// <param name="itemId">Item id, 0 when the path has none.</param>
    /// <param name="worlds">Worlds as read by the caller.</param>
    /// <param name="worldId">World to attribute the upload to.</param>
    /// <returns>False when the upload must be skipped.</returns>
    public bool TryResolve(Kind kind, uint itemId, in Worlds worlds, out uint worldId)
    {
        var reason = Decide(worlds);
        if (reason != SkipReason.None)
        {
            worldId = 0;
            this.NoteSkip(kind, itemId, reason, worlds, null);
            return false;
        }

        worldId = worlds.PlayerWorld;

        string line = null;
        lock (this.gate)
        {
            this.passed[(int)kind]++;
            if (this.seenCombos.Count < MaxWorldCombos)
            {
                if (this.seenCombos.Add(worlds))
                {
                    var mismatch = worlds.LobbyWorld != worlds.PlayerWorld || worlds.LobbyHome != worlds.PlayerHome;
                    line = $"{LogPrefix} world kind={kind} item={Item(itemId)} uploadWorld={worlds.PlayerWorld} " +
                           $"lobbyWorld={worlds.LobbyWorld} playerHome={worlds.PlayerHome} lobbyHome={worlds.LobbyHome} " +
                           $"lobbyLoggedIn={(worlds.LobbyLoggedIn ? 1 : 0)} lobbyMismatch={(mismatch ? 1 : 0)} " +
                           "(first time this combination is seen this session)";
                }
            }
            else if (!this.comboOverflowLogged && !this.seenCombos.Contains(worlds))
            {
                this.comboOverflowLogged = true;
                line = $"{LogPrefix} world combination table full ({MaxWorldCombos}); " +
                       "further combinations are not logged this session";
            }
        }

        if (line != null)
            this.log(line);
        return true;
    }

    /// <summary>
    /// Records one skipped upload; same (kind, reason) is written at most once per window.
    /// </summary>
    /// <param name="kind">Upload path.</param>
    /// <param name="itemId">Item id, 0 when the path has none.</param>
    /// <param name="reason">Why it is skipped.</param>
    /// <param name="worlds">Worlds as read by the caller.</param>
    /// <param name="error">Exception type name for <see cref="SkipReason.ResolveFailed"/>, else null.</param>
    public void NoteSkip(Kind kind, uint itemId, SkipReason reason, in Worlds worlds, string error)
    {
        string line = null;
        lock (this.gate)
        {
            ref var stat = ref this.skips[((int)kind * ReasonCount) + (int)reason];
            var now = this.clockMs();
            stat.Total++;
            if (stat.Total > 1 && now - stat.LastLogMs < SkipLogWindowMs)
            {
                stat.Suppressed++;
            }
            else
            {
                line = $"{LogPrefix} skip kind={kind} item={Item(itemId)} reason={reason} " +
                       $"playerWorld={PlayerValue(worlds, worlds.PlayerWorld)} lobbyWorld={worlds.LobbyWorld} " +
                       $"playerHome={PlayerValue(worlds, worlds.PlayerHome)} lobbyHome={worlds.LobbyHome} " +
                       $"lobbyLoggedIn={(worlds.LobbyLoggedIn ? 1 : 0)} suppressed={stat.Suppressed} total={stat.Total}" +
                       (error == null ? string.Empty : $" error={error}");
                stat.LastLogMs = now;
                stat.Suppressed = 0;
            }
        }

        if (line != null)
            this.log(line);
    }

    /// <summary>
    /// Records that uploading is turned off; written once per session.
    /// </summary>
    public void NoteDisabled()
    {
        lock (this.gate)
        {
            if (this.disabledLogged)
                return;
            this.disabledLogged = true;
        }

        this.log($"{LogPrefix} market board upload is turned off in Dalamud settings; " +
                 "nothing is uploaded (logged once per session)");
    }

    /// <summary>
    /// Session totals, including skips that were suppressed after the last written line.
    /// </summary>
    /// <returns>One line, or null when nothing was ever resolved.</returns>
    public string BuildSummary()
    {
        lock (this.gate)
        {
            var reasons = new long[ReasonCount];
            long any = 0;
            for (var i = 0; i < this.skips.Length; i++)
            {
                reasons[i % ReasonCount] += this.skips[i].Total;
                any += this.skips[i].Total;
            }

            for (var i = 0; i < KindCount; i++)
                any += this.passed[i];

            if (any == 0)
                return null;

            return $"{LogPrefix} summary passed=Listings:{this.passed[0]},Tax:{this.passed[1]},Purchase:{this.passed[2]} " +
                   $"skipped=NoLocalPlayer:{reasons[1]},PlayerWorldZero:{reasons[2]},RetiredWorld:{reasons[3]}," +
                   $"ResolveFailed:{reasons[4]} worldCombos={this.seenCombos.Count}";
        }
    }

    private static string Item(uint itemId) => itemId == 0 ? "none" : itemId.ToString();

    private static string PlayerValue(in Worlds worlds, uint value) => worlds.HasPlayer ? value.ToString() : "none";

    /// <summary>
    /// World ids at the moment an upload is resolved.
    /// </summary>
    /// <param name="HasPlayer">Whether a local player object exists.</param>
    /// <param name="PlayerWorld">Current world on the local player object; 0 without one.</param>
    /// <param name="PlayerHome">Home world on the local player object; 0 without one.</param>
    /// <param name="LobbyLoggedIn">Lobby agent's logged-in flag.</param>
    /// <param name="LobbyWorld">Current world in the lobby data (diagnostics only).</param>
    /// <param name="LobbyHome">Home world in the lobby data (diagnostics only).</param>
    public readonly record struct Worlds(
        bool HasPlayer,
        uint PlayerWorld,
        uint PlayerHome,
        bool LobbyLoggedIn,
        uint LobbyWorld,
        uint LobbyHome);

    private struct SkipStat
    {
        public long Total;
        public long Suppressed;
        public long LastLogMs;
    }
}
