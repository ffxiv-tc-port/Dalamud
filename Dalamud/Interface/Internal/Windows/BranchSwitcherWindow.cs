using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Newtonsoft.Json;

namespace Dalamud.Interface.Internal.Windows;

/// <summary>
/// Window responsible for switching Dalamud beta branches.
/// </summary>
public class BranchSwitcherWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="BranchSwitcherWindow"/> class.
    /// </summary>
    public BranchSwitcherWindow()
        : base("Branch Switcher", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.ShowCloseButton = true;
        this.RespectCloseHotkey = true;
    }

    /// <inheritdoc/>
    public override void Draw()
    {
        ImGui.Text("本功能在 Dalamud (Soil) 中不可用, 请关闭此窗口");
    }

    private class VersionEntry
    {
        [JsonProperty("key")]
        public string? Key { get; set; }

        [JsonProperty("track")]
        public string? Track { get; set; }

        [JsonProperty("assemblyVersion")]
        public string? AssemblyVersion { get; set; }

        [JsonProperty("runtimeVersion")]
        public string? RuntimeVersion { get; set; }

        [JsonProperty("runtimeRequired")]
        public bool RuntimeRequired { get; set; }

        [JsonProperty("supportedGameVer")]
        public string? SupportedGameVer { get; set; }

        [JsonProperty("downloadUrl")]
        public string? DownloadUrl { get; set; }

        [JsonProperty("gitSha")]
        public string? GitSha { get; set; }
    }
}
