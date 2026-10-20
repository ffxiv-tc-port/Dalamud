using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Native message boxes for decisions made before Dalamud's services start.
/// </summary>
/// <remarks>
/// Called on Dalamud's own startup thread, never the game's main thread. Ownerless on purpose: a box owned by the game
/// window would disable that window until it is answered. A failed MessageBox call counts as "no".
/// </remarks>
internal static class StartupDialog
{
    private const MESSAGEBOX_STYLE OnTop = MESSAGEBOX_STYLE.MB_TOPMOST | MESSAGEBOX_STYLE.MB_SETFOREGROUND;

    /// <summary>
    /// Asks a yes/no question with "no" as the default button.
    /// </summary>
    /// <param name="text">The question.</param>
    /// <param name="caption">The title.</param>
    /// <returns>Whether the user chose yes.</returns>
    internal static bool AskYesNoDefaultNo(string text, string caption)
        => Windows.Win32.PInvoke.MessageBox(
               HWND.Null,
               text,
               caption,
               MESSAGEBOX_STYLE.MB_YESNO | MESSAGEBOX_STYLE.MB_DEFBUTTON2 | MESSAGEBOX_STYLE.MB_ICONWARNING | OnTop)
           == MESSAGEBOX_RESULT.IDYES;

    /// <summary>
    /// Shows an error with a single OK button.
    /// </summary>
    /// <param name="text">The message.</param>
    /// <param name="caption">The title.</param>
    internal static void ShowError(string text, string caption)
        => _ = Windows.Win32.PInvoke.MessageBox(
               HWND.Null,
               text,
               caption,
               MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONERROR | OnTop);
}
