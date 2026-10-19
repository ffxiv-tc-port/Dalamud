<!-- ffxiv-tc-port 繁體中文說明開始 -->
# Dalamud(台服 fork)

Dalamud 是 FFXIV 的插件開發框架，注入遊戲行程後提供 hook、遊戲資料存取與插件執行環境，
本艦隊所有插件都跑在它上面。

## 台服 fork 的目的

以 yanmucorp 的中文客戶端相容 fork 為基底（yanmucorp 已處理陸服／台服客戶端與 goatcorp 上游
的結構差異），我們在上面另外做台服適配、私有插件庫支援、穩定性加固與效能診斷。

## 與上游的差異

- **介面繁體中文化**：`loc/dalamud_tw.json` 是 Dalamud 本體介面的台灣用語翻譯；標題畫面、視窗出錯的
  重試提示、更新結果等訊息也改成繁體中文。
- **私有插件庫的 GitHub 權杖**：設定頁可填 GitHub 存取權杖（以 Windows 使用者的 DPAPI 加密保存），
  只在請求 `ffxiv-tc-port` 底下的 `raw.githubusercontent.com`／`api.github.com` 時帶上；插件庫、圖示與
  插件下載都能認證，插件也能用 `IDalamudPluginInterface.CreateGitHubHttpClient()` 取用。
- **插件下載與更新**：分段多連線下載（預設 8 條，某段斷線只重下那一段）；顯示下載進度與速度，
  關掉安裝器後畫面角落也看得到；不再被固定逾時砍斷；「全部更新」正確列出失敗的插件與原因、
  已是最新的不算失敗；兩輪更新同時跑時不再刪掉剛更新好的檔案。
- **更新日誌**：改讀本機 manifest 與插件庫的發布時間，合併顯示插件庫上尚未更新的新版本，時間用本地時區。
- **市場上傳的世界判定守衛**：玩家世界以場上實體為準（台服併服角色的大廳欄位會殘留舊世界）；
  拿不到角色、世界是 0 或是已停運的世界就不上傳那一次，並在 log 留下 `[MbUploadGuard]` 診斷行。
- **`[PerfDiag]` 效能診斷**：記錄遊戲卡頓時的記憶體回收、各插件的記憶體配置量與登入時各插件的耗時，
  每小時寫進 log（關遊戲時補印未滿一小時的那段）；整個行程依執行緒與型別的配置取樣在設定裡
  手動開啟，預設關。
- **聊天欄 Tab 補完**：台服客戶端把補完函式內聯了，改從遊戲自己讀偏移並在對不上時停用，插件指令照樣補得出來。
- **原生層加固**：右鍵選單、名牌、飛字、座標轉換、SeString 繪製、伺服器資訊列、標題畫面等多處
  原生指標補判空；addon 與角色名字的讀取夾在緩衝區內；交換鏈與 ReShade 相關 detour 補 hook 欄位防護。
- **執行緒與視窗**：聊天輸出與快顯通知佇列改成執行緒安全；恢復真實的主執行緒判定；單一視窗出錯
  不再連累同一個視窗系統的其他視窗，出錯畫面 1 秒後自動重試。
- **插件卸載**：卸載或更新後，舊版不再因用過 JSON、`TypeDescriptor` 或自訂 Excel 表型別而整份留在記憶體裡。
- **多處降低記憶體用量**：每幀的插件更新、視窗繪製、IPC 呼叫、圖示取用、狀態列、副本狀態與條件判斷等
  不再每次配置。
- **log**：保留策略撞上限不再靜默停寫、歷史 log 以時間戳累積；插件清單變動時的 TROUBLESHOOTING 行瘦身。
- **子模組與 CI**：所有子模組改指 `ffxiv-tc-port` 的鏡像；移除從上游繼承、對我們無用的 workflow。

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
