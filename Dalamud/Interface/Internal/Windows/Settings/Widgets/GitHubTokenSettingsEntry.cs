using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

using CheapLoc;

using Dalamud.Bindings.ImGui;
using Dalamud.Configuration.Internal;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Networking.Http;
using Dalamud.Utility;
using Dalamud.Utility.Internal;

namespace Dalamud.Interface.Internal.Windows.Settings.Widgets;

/// <summary>
/// Settings entry for the GitHub token used to reach this organisation's private repositories.
/// </summary>
/// <remarks>
/// The entry holds the token in plaintext only while the settings window is open; the configuration
/// stores it encrypted. Nothing here may log or print the token itself.
/// </remarks>
[SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Internals")]
internal class GitHubTokenSettingsEntry : SettingsEntry
{
    private const string TestUrl = "https://api.github.com/repos/ffxiv-tc-port/DalamudPluginsTC";

    private string token = string.Empty;
    private string loadedToken = string.Empty;

    private string testStatus = string.Empty;
    private TestState testState = TestState.None;

    public GitHubTokenSettingsEntry()
    {
        this.Name = LazyLoc.Localize("DalamudSettingsGitHubToken", "GitHub 存取權杖");
    }

    private enum TestState
    {
        None,
        Running,
        Ok,
        Failed,
    }

    public override void Load()
    {
        this.loadedToken = Service<DalamudConfiguration>.Get().GitHubToken ?? string.Empty;
        this.token = this.loadedToken;
    }

    public override void Save()
    {
        // Re-encrypting an unchanged token would churn the config file for nothing, and would also
        // wipe a blob that merely failed to decrypt on this machine.
        if (this.token == this.loadedToken)
            return;

        Service<DalamudConfiguration>.Get().GitHubToken = this.token.IsNullOrWhitespace() ? null : this.token;
        this.loadedToken = this.token;
    }

    public override void Draw()
    {
        ImGui.Text(Loc.Localize("DalamudSettingsGitHubTokenHeader", "GitHub 存取權杖"));

        ImGui.TextColoredWrapped(
            ImGuiColors.DalamudGrey,
            Loc.Localize(
                "DalamudSettingsGitHubTokenHint",
                "用於存取 ffxiv-tc-port 組織的私有倉庫（插件庫列表、圖示與插件下載）。\n" +
                "只會附加在 raw.githubusercontent.com 與 api.github.com 且屬於本組織的網址上，其他網站一律不會送出。\n" +
                "權杖以 Windows 帳號金鑰加密後存放，換帳號或換電腦需要重新填寫。"));

        ImGui.InputText("##githubToken", ref this.token, 512, ImGuiInputTextFlags.Password);

        using (ImRaii.Disabled(this.testState == TestState.Running))
        {
            if (ImGui.Button(Loc.Localize("DalamudSettingsGitHubTokenTest", "測試權杖")))
                Task.Run(() => this.TestTokenAsync(this.token));
        }

        if (this.testStatus.Length == 0)
            return;

        var color = this.testState switch
        {
            TestState.Running => ImGuiColors.DalamudYellow,
            TestState.Ok => ImGuiColors.ParsedGreen,
            TestState.Failed => ImGuiColors.DalamudRed,
            _ => ImGuiColors.DalamudWhite,
        };

        ImGui.TextColoredWrapped(color, this.testStatus);
    }

    private async Task TestTokenAsync(string candidate)
    {
        this.testState = TestState.Running;
        this.testStatus = Loc.Localize("DalamudSettingsGitHubTokenTesting", "測試中……");

        try
        {
            // The real handler is used so the host/owner whitelist is exercised, not re-implemented.
            using var client = new HttpClient(new GitHubAuthHandler(new SocketsHttpHandler(), () => candidate))
            {
                Timeout = TimeSpan.FromSeconds(15),
            };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Dalamud", Util.AssemblyVersion));

            using var response = await client.GetAsync(TestUrl);
            var code = (int)response.StatusCode;

            this.testState = response.StatusCode == HttpStatusCode.OK ? TestState.Ok : TestState.Failed;
            this.testStatus = response.StatusCode switch
            {
                HttpStatusCode.OK => Loc.Localize("DalamudSettingsGitHubTokenOk", "可用（200）"),
                HttpStatusCode.Unauthorized => Loc.Localize("DalamudSettingsGitHubTokenUnauthorized", "權杖無效或已過期（401）"),
                HttpStatusCode.NotFound => Loc.Localize("DalamudSettingsGitHubTokenNotFound", "權杖沒有這個倉庫的讀取權限（404）"),
                _ => string.Format(Loc.Localize("DalamudSettingsGitHubTokenOther", "未預期的回應（{0}）"), code),
            };
        }
        catch (Exception ex)
        {
            this.testState = TestState.Failed;
            this.testStatus = string.Format(
                Loc.Localize("DalamudSettingsGitHubTokenError", "連線失敗：{0}"), ex.GetType().Name);
        }
    }
}
