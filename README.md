<!-- ffxiv-tc-port 繁體中文說明開始 -->
# Dalamud(台服 fork)

Dalamud 是 FFXIV 的插件開發框架，注入遊戲行程後提供 hook、遊戲資料存取與插件執行環境，
本艦隊所有插件都跑在它上面。

## 台服 fork 的目的

以 yanmucorp 的中文客戶端相容 fork 為基底（yanmucorp 已處理陸服/台服客戶端與 goatcorp 上游
的結構差異），我們在上面另外做了：

- **`loc/dalamud_tw.json`**：Dalamud 本體 UI 的 zh-TW 繁體中文翻譯（491 個匯出的 CheapLoc key
  全數翻完 + 8 條沿用自 Crowdin tw 檔），來源優先序為 Crowdin 既有繁中 → 簡中檔 OpenCC 轉換 →
  手翻，並統一成台灣用語。
- **多處核心裸指標解參補判空**：`ContextMenu` detour（`AgentContext.CurrentContextMenu`）、
  `SeStringCreatorWidget` 等原生路徑補上判空，避免 `try/catch` 攔不到的 AccessViolation。
- **玩家世界改以場上實體為準**：修正台服併服角色的大廳欄位殘留舊世界、導致市場匿名上傳被
  標到已停運伺服器的問題。
- 更新日誌時間改用本地時區顯示（原本用 UTC 差值會多算一個時區）。
- 全部子模組改指向 `ffxiv-tc-port` 鏡像。

## 與上游的差異

以上各項，另有零星建置修正與 API13 相容性調整；未逐一列出全部 commit。

## 誰在用它

全艦隊每個插件都透過這個執行期框架載入與建置，不適用逐一列消費端。

---

以下為上游原始 README，內容未經修改：

<!-- ffxiv-tc-port 繁體中文說明結束 -->

# Dalamud [![Discord Shield](https://discordapp.com/api/guilds/581875019861328007/widget.png?style=shield)](https://discord.gg/3NMcUV5)

<p align="center">
  <img src="https://raw.githubusercontent.com/ottercorp/DalamudAssets/master/UIRes/logo.png" alt="Dalamud" width="200"/>
</p>

Dalamud is a plugin development framework for FFXIV that provides access to game data and native interoperability with the game itself to add functionality and quality-of-life.

It is meant to be used in conjunction with [XIVLauncherCN](https://github.com/ottercorp/FFXIVQuickLauncher), which manages and launches Dalamud for you. __It is generally not recommended for users to try to run Dalamud manually as there are multiple dependencies and assumed folder paths.__

## Hold Up!

If you are just trying to **use** Dalamud, you don't need to do anything on this page - please [download XIVLauncher](https://ottercorp.github.io/) from its official page and follow the setup instructions.

## Building and testing locally

Please check the [docs page on building Dalamud](https://dalamud.dev/building) for more information and required dependencies.

## Plugin development
Dalamud features a growing API for in-game plugin development with game data and chat access and overlays.
Please see our [Developer FAQ](https://ottercorp.github.io/faq/development) and the [API documentation](https://dalamud.dev) for more details.

If you need any support regarding the API or usage of Dalamud, please [join our discord server](https://discord.gg/3NMcUV5).

<br>

Thanks to Mino, whose work has made this possible!

## Components & Pipeline

These components are used in order to load Dalamud into a target process.
Dalamud can be loaded via DLL injection, or by rewriting a process' entrypoint.

| Name                          | Purpose                                                                                                                      |
|-------------------------------|------------------------------------------------------------------------------------------------------------------------------|
| *Dalamud.Injector.Boot* (C++) | Loads the .NET Core runtime into a process via hostfxr and kicks off Dalamud.Injector                                        |
| *Dalamud.Injector* (C#)       | Performs DLL injection on the target process                                                                                 |
| *Dalamud.Boot* (C++)          | Loads the .NET Core runtime into the active process and kicks off Dalamud, or rewrites a target process' entrypoint to do so |
| *Dalamud* (C#)                | Core API, game bindings, plugin framework                                                                                    |
| *Dalamud.CorePlugin* (C#)     | Testbed plugin that can access Dalamud internals, to prototype new Dalamud features                                          |

<br>

##### Final Fantasy XIV © 2010-2021 SQUARE ENIX CO., LTD. All Rights Reserved. We are not affiliated with SQUARE ENIX CO., LTD. in any way.
