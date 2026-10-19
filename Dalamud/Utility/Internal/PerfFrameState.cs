using System.Threading;

namespace Dalamud.Utility.Internal;

/// <summary>What the game showed at the start of a framework update.</summary>
internal enum GameScreen
{
    /// <summary>Not logged in: title screen, character select, or not known yet.</summary>
    Title = 0,

    /// <summary>A loading screen (BetweenAreas or BetweenAreas51), login included.</summary>
    Loading = 1,

    /// <summary>Logged in and not loading.</summary>
    InGame = 2,
}

/// <summary>
/// The game screen as of the latest framework update, for diagnostics running elsewhere.
/// </summary>
/// <remarks>
/// Only the framework thread writes it, once per update; any thread may read it. Readers never touch game memory.
/// </remarks>
internal static class PerfFrameState
{
    private static int screen;

    /// <summary>Gets the screen published by the latest framework update.</summary>
    public static GameScreen Screen => (GameScreen)Volatile.Read(ref screen);

    /// <summary>Publishes this update's screen. Framework thread only.</summary>
    /// <param name="value">The screen.</param>
    public static void Publish(GameScreen value) => Volatile.Write(ref screen, (int)value);
}
