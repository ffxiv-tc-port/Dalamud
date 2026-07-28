using System.Collections.Generic;
using System.Linq;
using System.Numerics;

using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Configuration.Internal;
using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Interface.Animation.EasingFunctions;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.FontIdentifier;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.ManagedFontAtlas.Internals;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.AutoUpdate;
using Dalamud.Plugin.Services;
using Dalamud.Storage.Assets;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace Dalamud.Interface.Internal.Windows;

/// <summary>
/// For major updates, an in-game Changelog window.
/// </summary>
internal sealed class ChangelogWindow : Window, IDisposable
{
    private const string WarrantsChangelogForMajorMinor = "10.0.";

    private const string ChangeLog =
        @"• 更新了 Dalamud 以相容版本 7.0
• 進行了大量幕後更改，使 Dalamud 和插件更加穩定可靠
• 添加了開發人員可以利用的新功能
• 重新設計了 Dalamud / 插件安裝程式 UI
• 將 Roaming 資料夾重新放回了 AppData 中
";

    private static readonly TimeSpan TitleScreenWaitTime = TimeSpan.FromSeconds(0.5f);

    private readonly TitleScreenMenuWindow tsmWindow;

    private readonly GameGui gameGui;

    private readonly DisposeSafety.ScopedFinalizer scopedFinalizer = new();
    private readonly IFontAtlas privateAtlas;
    private readonly Lazy<IFontHandle> bannerFont;
    private readonly Lazy<IDalamudTextureWrap> apiBumpExplainerTexture;
    private readonly Lazy<IDalamudTextureWrap> logoTexture;

    private readonly InOutCubic windowFade = new(TimeSpan.FromSeconds(2.5f))
    {
        Point1 = Vector2.Zero,
        Point2 = new Vector2(2f),
    };

    private readonly InOutCubic bodyFade = new(TimeSpan.FromSeconds(0.8f))
    {
        Point1 = Vector2.Zero,
        Point2 = Vector2.One,
    };

    private readonly InOutCubic titleFade = new(TimeSpan.FromSeconds(0.5f))
    {
        Point1 = Vector2.Zero,
        Point2 = Vector2.One,
    };

    private readonly InOutCubic fadeOut = new(TimeSpan.FromSeconds(0.5f))
    {
        Point1 = Vector2.One,
        Point2 = Vector2.Zero,
    };

    private State state = State.WindowFadeIn;

    private bool needFadeRestart = false;

    private bool isFadingOutForStateChange = false;
    private State? stateAfterFadeOut;

    private AutoUpdateBehavior? chosenAutoUpdateBehavior;

    private Dictionary<string, int> currentFtueLevels = new();

    private DateTime? isEligibleSince;
    private bool openedThroughEligibility;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChangelogWindow"/> class.
    /// </summary>
    /// <param name="tsmWindow">TSM window.</param>
    /// <param name="fontAtlasFactory">An instance of <see cref="FontAtlasFactory"/>.</param>
    /// <param name="assets">An instance of <see cref="DalamudAssetManager"/>.</param>
    /// <param name="gameGui">An instance of <see cref="GameGui"/>.</param>
    /// <param name="framework">An instance of <see cref="Framework"/>.</param>
    public ChangelogWindow(
        TitleScreenMenuWindow tsmWindow,
        FontAtlasFactory fontAtlasFactory,
        DalamudAssetManager assets,
        GameGui gameGui,
        Framework framework)
        : base("What's new in Dalamud?##ChangelogWindow", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse, true)
    {
        this.gameGui = gameGui;

        this.tsmWindow = tsmWindow;
        this.Namespace = "DalamudChangelogWindow";
        this.privateAtlas = this.scopedFinalizer.Add(
            fontAtlasFactory.CreateFontAtlas(this.Namespace, FontAtlasAutoRebuildMode.Async));
        this.bannerFont = new(
            () => this.scopedFinalizer.Add(
                this.privateAtlas.NewGameFontHandle(new(GameFontFamilyAndSize.MiedingerMid18))));

        this.apiBumpExplainerTexture = new(() => assets.GetDalamudTextureWrap(DalamudAsset.ChangelogApiBumpIcon));
        this.logoTexture = new(() => assets.GetDalamudTextureWrap(DalamudAsset.Logo));
        
        // If we are going to show a changelog, make sure we have the font ready, otherwise it will hitch
        if (WarrantsChangelog())
            _ = this.bannerFont.Value;

        framework.Update += this.FrameworkOnUpdate;
        this.scopedFinalizer.Add(() => framework.Update -= this.FrameworkOnUpdate);
    }

    private enum State
    {
        WindowFadeIn,
        ExplainerIntro,
        ExplainerApiBump,
        AskAutoUpdate,
        Links,
    }

    /// <summary>
    /// Check if a changelog should be shown.
    /// </summary>
    /// <returns>True if a changelog should be shown.</returns>
    public static bool WarrantsChangelog()
    {
        var configuration = Service<DalamudConfiguration>.Get();
        var pm = Service<PluginManager>.GetNullable();
        var pmWantsChangelog = pm?.InstalledPlugins.Any() ?? true;
        return (string.IsNullOrEmpty(configuration.LastChangelogMajorMinor) ||
                (!WarrantsChangelogForMajorMinor.StartsWith(configuration.LastChangelogMajorMinor) &&
                 Util.AssemblyVersion.StartsWith(WarrantsChangelogForMajorMinor))) && pmWantsChangelog;
    }

    /// <inheritdoc/>
    public override void OnOpen()
    {
        Service<DalamudInterface>.Get().SetCreditsDarkeningAnimation(true);
        this.tsmWindow.AllowDrawing = false;

        _ = this.bannerFont;

        this.isFadingOutForStateChange = false;
        this.stateAfterFadeOut = null;

        this.state = State.WindowFadeIn;
        this.windowFade.Reset();
        this.bodyFade.Reset();
        this.titleFade.Reset();
        this.fadeOut.Reset();
        this.needFadeRestart = true;

        this.chosenAutoUpdateBehavior = null;

        this.currentFtueLevels = Service<DalamudConfiguration>.Get().SeenFtueLevels;

        base.OnOpen();
    }

    /// <inheritdoc/>
    public override void OnClose()
    {
        base.OnClose();

        this.tsmWindow.AllowDrawing = true;
        Service<DalamudInterface>.Get().SetCreditsDarkeningAnimation(false);

        var configuration = Service<DalamudConfiguration>.Get();

        if (this.chosenAutoUpdateBehavior.HasValue)
        {
            configuration.AutoUpdateBehavior = this.chosenAutoUpdateBehavior.Value;
        }

        configuration.SeenFtueLevels = this.currentFtueLevels;
        configuration.QueueSave();
    }

    /// <inheritdoc/>
    public override void PreDraw()
    {
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f);

        base.PreDraw();

        if (this.needFadeRestart)
        {
            this.windowFade.Restart();
            this.titleFade.Restart();
            this.needFadeRestart = false;
        }

        this.windowFade.Update();
        this.titleFade.Update();
        this.fadeOut.Update();
        ImGui.SetNextWindowBgAlpha(Math.Clamp(this.windowFade.EasedPoint.X, 0, 0.9f));

        this.Size = new Vector2(900, 400);
        this.SizeCondition = ImGuiCond.Always;

        // Center the window on the main viewport
        var viewportPos = ImGuiHelpers.MainViewport.Pos;
        var viewportSize = ImGuiHelpers.MainViewport.Size;
        var windowSize = this.Size!.Value * ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowPos(new Vector2(viewportPos.X + viewportSize.X / 2 - windowSize.X / 2, viewportPos.Y + viewportSize.Y / 2 - windowSize.Y / 2));
        // ImGui.SetNextWindowPos(new Vector2(viewportSize.X / 2 - windowSize.X / 2, viewportSize.Y / 2 - windowSize.Y / 2));
    }

    /// <inheritdoc/>
    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);

        this.ResetMovieTimer();

        base.PostDraw();
    }

    /// <inheritdoc/>
    public override void Draw()
    {
        void Dismiss()
        {
            var configuration = Service<DalamudConfiguration>.Get();
            configuration.LastChangelogMajorMinor = WarrantsChangelogForMajorMinor;
            configuration.QueueSave();
        }

        var windowSize = ImGui.GetWindowSize();

        var dummySize = 10 * ImGuiHelpers.GlobalScale;
        ImGui.Dummy(new Vector2(dummySize));
        ImGui.SameLine();

        var logoContainerSize = new Vector2(windowSize.X * 0.2f - dummySize, windowSize.Y);
        using (var child = ImRaii.Child("###logoContainer"u8, logoContainerSize, false))
        {
            if (!child)
                return;

            var logoSize = new Vector2(logoContainerSize.X);

            // Center the logo in the container
            ImGui.SetCursorPos(new Vector2(logoContainerSize.X / 2 - logoSize.X / 2, logoContainerSize.Y / 2 - logoSize.Y / 2));

            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, Math.Clamp(this.windowFade.EasedPoint.X - 0.5f, 0f, 1f)))
                ImGui.Image(this.logoTexture.Value.Handle, logoSize);
        }

        ImGui.SameLine();
        ImGui.Dummy(new Vector2(dummySize));
        ImGui.SameLine();

        using (var child = ImRaii.Child("###textContainer"u8, new Vector2((windowSize.X * 0.8f) - dummySize * 4, windowSize.Y), false))
        {
            if (!child)
                return;

            ImGuiHelpers.ScaledDummy(20);

            var titleFadeVal = this.isFadingOutForStateChange ? this.fadeOut.EasedPoint.X : this.titleFade.EasedPoint.X;
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, Math.Clamp(titleFadeVal, 0f, 1f)))
            {
                using var font = this.bannerFont.Value.Push();

                switch (this.state)
                {
                    case State.WindowFadeIn:
                    case State.ExplainerIntro:
                        ImGuiHelpers.CenteredText("全新改进版本");
                        break;

                    case State.ExplainerApiBump:
                        ImGuiHelpers.CenteredText("插件更新");
                        break;

                    case State.AskAutoUpdate:
                        ImGuiHelpers.CenteredText("自动更新");
                        break;

                    case State.Links:
                        ImGuiHelpers.CenteredText("尽情享受!");
                        break;
                }
            }

            ImGuiHelpers.ScaledDummy(8);

            if (this.state == State.WindowFadeIn && this.windowFade.EasedPoint.X > 1.5f)
            {
                this.state = State.ExplainerIntro;
                this.bodyFade.Restart();
            }

            if (this.isFadingOutForStateChange && this.fadeOut.IsDone)
            {
                this.state = this.stateAfterFadeOut ?? throw new Exception("State after fade out is null");

                this.bodyFade.Restart();
                this.titleFade.Restart();

                this.isFadingOutForStateChange = false;
                this.stateAfterFadeOut = null;
            }

            this.bodyFade.Update();
            var bodyFadeVal = this.isFadingOutForStateChange ? this.fadeOut.EasedPoint.X : this.bodyFade.EasedPoint.X;
            using (ImRaii.PushStyle(ImGuiStyleVar.Alpha, Math.Clamp(bodyFadeVal, 0, 1f)))
            {
                void GoToNextState(State nextState)
                {
                    this.isFadingOutForStateChange = true;
                    this.stateAfterFadeOut = nextState;

                    this.fadeOut.Restart();
                }

                bool DrawNextButton(State nextState)
                {
                    // Draw big, centered next button at the bottom of the window
                    var buttonHeight = 30 * ImGuiHelpers.GlobalScale;
                    var buttonText = "Next";
                    var buttonWidth = ImGui.CalcTextSize(buttonText).X + 40 * ImGuiHelpers.GlobalScale;
                    ImGui.SetCursorPosY(windowSize.Y - buttonHeight - (20 * ImGuiHelpers.GlobalScale));
                    ImGuiHelpers.CenterCursorFor((int)buttonWidth);

                    if (ImGui.Button(buttonText, new Vector2(buttonWidth, buttonHeight)) && !this.isFadingOutForStateChange)
                    {
                        GoToNextState(nextState);
                        return true;
                    }

                    return false;
                }

                switch (this.state)
                {
                    case State.WindowFadeIn:
                    case State.ExplainerIntro:
                        ImGui.TextWrapped($"欢迎使用 Dalamud v{Util.GetScmVersion()}!");
                        ImGuiHelpers.ScaledDummy(5);
                        ImGui.TextWrapped(ChangeLog);
                        ImGuiHelpers.ScaledDummy(5);
                        ImGui.TextWrapped("本更新日志简要概述此版本最重要的变更"u8);
                        ImGui.TextWrapped("点击下一步查看插件更新指南"u8);

                        DrawNextButton(State.ExplainerApiBump);
                        break;

                    case State.ExplainerApiBump:
                        ImGui.TextWrapped("请注意! 由于本次更新变更，所有插件需要更新并已自动禁用"u8);
                        ImGui.TextWrapped("这是游戏更新的正常流程"u8);
                        ImGuiHelpers.ScaledDummy(5);
                        ImGui.TextWrapped("要更新插件，请打开插件安装器并点击'更新插件'，更新后的插件会自动重新启用"u8);
                        ImGuiHelpers.ScaledDummy(5);
                        ImGui.TextWrapped("请注意并非所有插件都已适配新版本"u8);
                        ImGui.TextWrapped("如果'已安装插件'标签中显示红色叉号，表示该插件尚未可用"u8);

                        ImGuiHelpers.ScaledDummy(15);

                        ImGuiHelpers.CenterCursorFor(this.apiBumpExplainerTexture.Value.Width);
                        ImGui.Image(
                            this.apiBumpExplainerTexture.Value.Handle,
                            this.apiBumpExplainerTexture.Value.Size);

                        if (!this.currentFtueLevels.TryGetValue(FtueLevels.AutoUpdate.Name, out var autoUpdateLevel) || autoUpdateLevel < FtueLevels.AutoUpdate.AutoUpdateInitial)
                        {
                            if (DrawNextButton(State.AskAutoUpdate))
                            {
                                this.currentFtueLevels[FtueLevels.AutoUpdate.Name] = FtueLevels.AutoUpdate.AutoUpdateInitial;
                            }
                        }
                        else
                        {
                            DrawNextButton(State.Links);
                        }

                        break;

                    case State.AskAutoUpdate:
                        ImGui.TextColoredWrapped(ImGuiColors.DalamudWhite, Loc.Localize("DalamudSettingsAutoUpdateHint",
                                                "Dalamud 可自动更新插件，确保最新功能和修复" +
                                                "在此选择自动更新的时间和方式"));
                        ImGuiHelpers.ScaledDummy(2);

                        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, Loc.Localize("DalamudSettingsAutoUpdateDisclaimer1",
                                                                "可通过插件列表中的更新按钮手动更新" +
                                                                "也可右键特定插件选择'始终自动更新'"));
                        ImGui.TextColoredWrapped(ImGuiColors.DalamudGrey, Loc.Localize("DalamudSettingsAutoUpdateDisclaimer2",
                                                                "Dalamud 仅在空闲时通知更新"));

                        ImGuiHelpers.ScaledDummy(15);

                        bool DrawCenteredButton(string text, float height)
                        {
                            var buttonHeight = height * ImGuiHelpers.GlobalScale;
                            var buttonWidth = ImGui.CalcTextSize(text).X + 50 * ImGuiHelpers.GlobalScale;
                            ImGuiHelpers.CenterCursorFor((int)buttonWidth);

                            return ImGui.Button(text, new Vector2(buttonWidth, buttonHeight)) &&
                                   !this.isFadingOutForStateChange;
                        }

                        using (ImRaii.PushColor(ImGuiCol.Button, ImGuiColors.DPSRed))
                        {
                            if (DrawCenteredButton("開啟自動更新", 30))
                            {
                                this.chosenAutoUpdateBehavior = AutoUpdateBehavior.UpdateMainRepo;
                                GoToNextState(State.Links);
                            }
                        }

                        ImGuiHelpers.ScaledDummy(2);

                        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 1))
                        using (var buttonColor = ImRaii.PushColor(ImGuiCol.Button, Vector4.Zero))
                        {
                            buttonColor.Push(ImGuiCol.Border, ImGuiColors.DalamudGrey3);
                            if (DrawCenteredButton("關閉自動更新", 25))
                            {
                                this.chosenAutoUpdateBehavior = AutoUpdateBehavior.OnlyNotify;
                                GoToNextState(State.Links);
                            }
                        }

                        break;

                    case State.Links:
                        ImGui.TextWrapped("如果你注意到任何問題或需要幫助，請查看常見問題解答，並在需要幫助的情況下在我們的 Discord 上聯繫我們。"u8);
                        ImGui.TextWrapped("祝你享受遊戲和 Dalamud 的時光！"u8);

                        ImGuiHelpers.ScaledDummy(45);

                        bool CenteredIconButton(FontAwesomeIcon icon, string text)
                        {
                            var buttonWidth = ImGuiComponents.GetIconButtonWithTextWidth(icon, text);
                            ImGuiHelpers.CenterCursorFor((int)buttonWidth);
                            return ImGuiComponents.IconButtonWithText(icon, text);
                        }

                        if (CenteredIconButton(FontAwesomeIcon.Download, "打开插件安装器"))
                        {
                            Service<DalamudInterface>.Get().OpenPluginInstaller();
                            this.IsOpen = false;
                            Dismiss();
                        }

                        ImGuiHelpers.ScaledDummy(5);

                        ImGuiHelpers.CenterCursorFor(
                            (int)(ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.Globe, "查看 FAQ") +
                            ImGuiComponents.GetIconButtonWithTextWidth(FontAwesomeIcon.LaughBeam, "加入我們的 Discord") +
                            (5 * ImGuiHelpers.GlobalScale) +
                            (ImGui.GetStyle().ItemSpacing.X * 4)));
                        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Globe, "常见问题"))
                        {
                            Util.OpenLink("https://info.atmoomen.top/");
                        }

                        ImGui.SameLine();
                        ImGuiHelpers.ScaledDummy(5);
                        ImGui.SameLine();

                        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.LaughBeam, "加入我們的 Discord"))
                        {
                            Util.OpenLink("https://discord.gg/dailyroutines");
                        }

                        ImGuiHelpers.ScaledDummy(5);

                        if (CenteredIconButton(FontAwesomeIcon.Heart, "支持我們"))
                        {
                            Util.OpenLink("https://info.atmoomen.top/");
                        }

                        var buttonHeight = 30 * ImGuiHelpers.GlobalScale;
                        var buttonText = "關閉";
                        var buttonWidth = ImGui.CalcTextSize(buttonText).X + 40 * ImGuiHelpers.GlobalScale;
                        ImGui.SetCursorPosY(windowSize.Y - buttonHeight - (20 * ImGuiHelpers.GlobalScale));
                        ImGuiHelpers.CenterCursorFor((int)buttonWidth);

                        if (ImGui.Button(buttonText, new Vector2(buttonWidth, buttonHeight)))
                        {
                            this.IsOpen = false;
                            Dismiss();
                        }

                        break;
                }
            }

            // Draw close button in the top right corner
            ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 100f);
            var btnAlpha = Math.Clamp(this.windowFade.EasedPoint.X - 0.5f, 0f, 1f);
            ImGui.PushStyleColor(ImGuiCol.Button, ImGuiColors.DPSRed.WithAlpha(btnAlpha).Desaturate(0.3f));
            ImGui.PushStyleColor(ImGuiCol.Text, ImGuiColors.DalamudWhite.WithAlpha(btnAlpha));

            var childSize = ImGui.GetWindowSize();
            var closeButtonSize = 15 * ImGuiHelpers.GlobalScale;
            ImGui.SetCursorPos(new Vector2(childSize.X - closeButtonSize - (10 * ImGuiHelpers.GlobalScale), 10 * ImGuiHelpers.GlobalScale));
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Times))
            {
                Dismiss();
                this.IsOpen = false;
            }

            ImGui.PopStyleColor(2);
            ImGui.PopStyleVar();

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("不关心此内容"u8);
            }
        }
    }

    /// <summary>
    /// Dispose this window.
    /// </summary>
    public void Dispose()
    {
        this.scopedFinalizer.Dispose();
    }

    private void FrameworkOnUpdate(IFramework unused)
    {
        if (!WarrantsChangelog())
            return;

        if (this.IsOpen)
            return;

        if (this.openedThroughEligibility)
            return;

        var isEligible = this.gameGui.GetAddonByName("_TitleMenu", 1) != IntPtr.Zero;

        var charaSelect = this.gameGui.GetAddonByName("CharaSelect", 1);
        var charaMake = this.gameGui.GetAddonByName("CharaMake", 1);
        var titleDcWorldMap = this.gameGui.GetAddonByName("TitleDCWorldMap", 1);
        if (charaMake != IntPtr.Zero || charaSelect != IntPtr.Zero || titleDcWorldMap != IntPtr.Zero)
            isEligible = false;

        if (this.isEligibleSince == null && isEligible)
        {
            this.isEligibleSince = DateTime.Now;
        }
        else if (this.isEligibleSince != null && !isEligible)
        {
            this.isEligibleSince = null;
        }

        if (this.isEligibleSince != null && DateTime.Now - this.isEligibleSince > TitleScreenWaitTime)
        {
            this.IsOpen = true;
            this.openedThroughEligibility = true;
        }
    }

    private unsafe void ResetMovieTimer()
    {
        var uiModule = UIModule.Instance();
        if (uiModule == null)
            return;

        var agentModule = uiModule->GetAgentModule();
        if (agentModule == null)
            return;

        var agentLobby = agentModule->GetAgentLobby();
        if (agentLobby == null)
            return;

        agentLobby->IdleTime = 0;
    }

    private static class FtueLevels
    {
        public static class AutoUpdate
        {
            public const string Name = "AutoUpdate";
            public const int AutoUpdateInitial = 1;
        }
    }
}
