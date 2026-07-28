using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Dalamud.Interface.Internal.Windows;

/// <summary>
/// For major updates, an in-game Changelog window.
/// </summary>
internal sealed class ToSWindow : Window, IDisposable
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ChangelogWindow"/> class.
    /// </summary>
    public ToSWindow()
        : base("Dalamud 用户协议", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse)
    {
        this.IsOpen = false;
    }

    /// <inheritdoc/>
    public override void Draw()
    {
    }

    /// <summary>
    /// Dispose this window.
    /// </summary>
    public void Dispose()
    {
    }
}
