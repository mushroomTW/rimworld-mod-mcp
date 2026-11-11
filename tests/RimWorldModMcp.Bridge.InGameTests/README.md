# Bridge 遊戲內整合測試

本測試專案透過 [SmashPhil DevTools](https://github.com/SmashPhil/DevTools) 在 RimWorld 1.7（.NET 10）真實遊戲環境中驗證 `bridge/` 的行為。Headless 單元測試通常會 stub 掉 Unity 與遊戲引擎，無法驗證 Harmony Patch 是否確實掛載、載入時機是否正確，以及即時取樣的值是否與遊戲底層數據完全吻合。

本測試專案**僅供內部開發與除錯**：未包含在 `RimWorldModMcp.slnx` 方案中，亦不隨工具發佈。專案刻意放置於 `bridge/` 目錄之外，避免 Server 將其隨 `bridge/**` 內嵌打包至發佈執行檔中。

## 目錄結構

- `Source/`：測試程式碼（以 `net10.0` 為目標框架），建置產物輸出至 `Mod/1.7/Assemblies/`。
- `Mod/`：提供給 `run_test_cycle` 載入的受測 Mod 目錄：
  - `About/About.xml`：Mod 定義（支援 RimWorld 1.7，相依 `brrainz.harmony`，並透過 `loadBefore` 排在 `rimworldmodmcp.bridge` 之前）。
  - `Configs/UnitTestConfig.xml`：DevTools 測試組態。
  - `1.7/Assemblies/`：包含 `DevTools.dll` 與 `RimWorldModMcp.Bridge.InGameTests.dll`（不納入版本控制）。
- `Third-Party/DevTools/`：SmashPhil DevTools 原始碼（本地保留，不納入版本控制）。

由於 Bridge 內部型別皆宣告為 `internal`，測試專案一律透過反射與 Harmony Probe 存取，不直接加入對 Bridge DLL 的專案參考。

## 受測的 Bridge 版本來源

`run_test_cycle` 載入的是 MCP Server 內嵌之 `bridge/Source/` 原始碼在暫存快取目錄中所編譯出的組件，而非 repo 工作區當下的即時檔案。因此若修改了 `bridge/` 的程式碼，必須先重新發佈或建置 Server，整合測試才會套用最新版本的 Bridge。

## 建置方式

測試專案已設定 ProjectReference 連動 `Third-Party/DevTools`，並在建置完成後自動將 `DevTools.dll` 與測試組件一併複製至 `Mod/1.7/Assemblies/`：

### Windows (PowerShell)

```powershell
dotnet build .\tests\RimWorldModMcp.Bridge.InGameTests\Source -c Release
```

### macOS / Linux (Bash)

```bash
dotnet build ./tests/RimWorldModMcp.Bridge.InGameTests/Source -c Release
```

## 執行測試

### 1. 透過 MCP 工具啟動測試

呼叫 MCP 工具 `run_test_cycle`，並帶入 DevTools CLI 參數（JSON 工具引數）：

```json
{
  "path": "<repo_root>/tests/RimWorldModMcp.Bridge.InGameTests/Mod",
  "extra_args": "--pid rimworldmodmcp.bridge.ingametests --test --exit"
}
```

測試執行結果會記錄在 **`Test.log`**（詳細測試結果報告）與 **`Player.log`**。測試完成後遊戲會自動退出（`--exit`）。

### 2. 透過遊戲內 DevTools 介面執行

在遊戲中開啟開發者模式（Developer Mode），點擊上方除錯工具列的 **DevTools** 圖示，選擇 `RimWorld MCP Bridge - In-Game Tests` 的 **Test Explorer**，即可互動式挑選、執行測試或檢視結果。

## 測試案例與場景支援

本測試套件使用 DevTools 的 `TestType` 生命週期機制：

- `BridgePatchTests`（`TestType.MainMenu`）：在主選單驗證 Verse 日誌與 `Root.Update` 的 Harmony Patch 是否確實掛載，以及日誌轉發與去重邏輯。
- `BridgeStartupTests`（`TestType.MainMenu`）：驗證 Bridge 啟動公告連線訊息、已載入 Mod 清單與啟動效能回報。
- `GameStateTests`（`TestType.Playing`）：DevTools 會自動生成地圖並進入遊戲中，驗證 Bridge 在主執行緒的狀態取樣、活動視窗監測、Tick、暫停狀態與殖民者數量回報。

## 預期診斷訊息

測試案例 `ForwardsEveryLogEntryPoint` 透過 `[WarningsAllowed]` 與 `[ErrorsAllowed]` 允許刻意發出的測試訊息。在執行測試時，`list_test_diagnostics` 中出現兩筆包含 `(intentional)` 的錯誤紀錄屬於正常預期行為。
