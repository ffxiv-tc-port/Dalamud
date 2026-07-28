using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration.Internal;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace Dalamud.Interface.Internal.Windows.Settings.Widgets;

[SuppressMessage("StyleCop.CSharp.DocumentationRules", "SA1600:Elements should be documented", Justification = "Internals")]
public class ProxySettingsEntry : SettingsEntry
{
    private bool useManualProxy;
    private string proxyProtocol = string.Empty;
    private string proxyHost = string.Empty;
    private int proxyPort;

    private int proxyProtocolIndex;
    private string proxyStatus = "Unknown";
    private readonly string[] proxyProtocols = ["http", "https", "socks5"];
    
    private string selfTestUrl = "https://raw.githubusercontent.com/AtmoOmen/Dalamud/master/global.json";

    public override void Load()
    {
        
        this.useManualProxy = Service<DalamudConfiguration>.Get().UseManualProxy;
        this.proxyProtocol = Service<DalamudConfiguration>.Get().ProxyProtocol;
        this.proxyHost = Service<DalamudConfiguration>.Get().ProxyHost;
        this.proxyPort = Service<DalamudConfiguration>.Get().ProxyPort;
        this.proxyProtocolIndex = Array.IndexOf(this.proxyProtocols, this.proxyProtocol);
        if (this.proxyProtocolIndex == -1)
            this.proxyProtocolIndex = 0;
    }

    public override void Save()
    {
        Service<DalamudConfiguration>.Get().UseManualProxy = this.useManualProxy;
        Service<DalamudConfiguration>.Get().ProxyProtocol = this.proxyProtocol;
        Service<DalamudConfiguration>.Get().ProxyHost = this.proxyHost;
        Service<DalamudConfiguration>.Get().ProxyPort = this.proxyPort;
    }

    public override void Draw()
    {
        ImGui.Text("代理設置");
        
        ImGui.TextColoredWrapped(ImGuiColors.DalamudRed, "设置 Dalamud 所使用的网络代理, 会影响到插件库的连接, 保存后重启游戏生效");
        
        ImGui.Checkbox("手動配置代理", ref this.useManualProxy);
        
        if (this.useManualProxy)
        {
            ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, "若不清楚下方选项的具体作用, 请勿修改");
            
            ImGui.Text("協議");
            
            ImGui.SameLine();
            ImGui.Combo("##proxyProtocol", ref this.proxyProtocolIndex, this.proxyProtocols, this.proxyProtocols.Length);
            
            ImGui.Text("地址");
            
            ImGui.SameLine();
            ImGui.InputText("##proxyHost", ref this.proxyHost, 100);
            
            ImGui.Text("端口");
            
            ImGui.SameLine();
            ImGui.InputInt("##proxyPort", ref this.proxyPort);
            
            this.proxyProtocol = this.proxyProtocols[this.proxyProtocolIndex];
        }

        using (ImRaii.Disabled(this.proxyStatus == "測試中"))
        {
            if (ImGui.Button("測試網站連通姓"))
            {
                Task.Run(() => TestUrlConnectivityAsync(this.selfTestUrl));
            }
        }
        
        ImGui.InputText("###SelfTestUrl", ref this.selfTestUrl, 2056);

        var proxyStatusColor = ImGuiColors.DalamudWhite;
        switch (this.proxyStatus)
        {
            case "測試中":
                proxyStatusColor = ImGuiColors.DalamudYellow;
                break;
            case "有效":
                proxyStatusColor = ImGuiColors.ParsedGreen;
                break;
            case "無效":
                proxyStatusColor = ImGuiColors.DalamudRed;
                break;
        }

        ImGui.TextColored(proxyStatusColor, $"代理測試結果： {this.proxyStatus}");
    }

    private async Task TestUrlConnectivityAsync(string url = "https://raw.githubusercontent.com/AtmoOmen/Dalamud/master/global.json")
    {
        try
        {
            this.proxyStatus = "測試中";
        
            var handler = new HttpClientHandler();
            if (this.useManualProxy)
            {
                handler.UseProxy = true;
                handler.Proxy    = new WebProxy($"{this.proxyProtocol}://{this.proxyHost}:{this.proxyPort}", true);
            }
            else
            {
                handler.UseProxy = false;
            }

            using var httpClient = new HttpClient(handler);
            httpClient.Timeout = TimeSpan.FromSeconds(3);
            
            var response = await httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();
            
            this.proxyStatus = "有效";
        }
        catch
        {
            this.proxyStatus = $"無效";
        }
    }
}

