# RimWorld MCP Bridge

此模組只在 `run_test_cycle` 啟動 RimWorld 時啟用。它讀取一次性的環境變數 token，把 Verse 的 warning／error 以 token 驗證過的 NDJSON 送到 localhost 的診斷 daemon；連線失敗會被忽略，不影響遊戲。

沒有設定 `RIMWORLD_MOD_MCP_BRIDGE_TOKEN` 時，Bridge 完全休眠——不掛任何 Harmony patch。因此即使它留在 Mods 目錄裡，正常遊玩也不受影響。

## 目標框架

只支援 RimWorld 1.7（不支援 1.6）。1.7 的遊戲跑在 .NET 10 上，Bridge 以 `net10.0` 為目標，參考 `RIMWORLD_MANAGED_DIR`（1.7 安裝根目錄）下的 `Assembly-CSharp.dll` 與 `UnityStubs.dll`，Harmony 帶入 net10 版。MCP server 本身也是 `net10.0`，兩者在同一個 solution 裡並存。

`DiagnosticPayload` 刻意不相依 RimWorld、Harmony 或 `System.Text.Json`——把 JSON 序列化的相依塞進遊戲會多帶 DLL 進 `Assemblies/`，有與其他 Mod 組件衝突的風險。它的逐字元轉義邏輯由 net10 的測試專案連結同一份原始碼直接測試。

## 遊戲狀態

除了錯誤與警告，Bridge 以 Harmony postfix 掛在 `Root.Update` 上，每秒取樣一次遊戲狀態（`ProgramState`、地圖是否載入、tick、暫停、時間流速、`LongEventHandler` 是否忙碌、開啟的視窗型別、殖民者人數、遊戲版本），有變化就送，無變化每 5 秒送一次心跳。`GameStatePayload` 與 `DiagnosticPayload` 一樣不相依 RimWorld，由測試專案以原始碼連結驗證。

## 預編譯與就地建置

目前不預編譯，也不使用 NuGet 的公開參考組件（`Krafs.Rimworld.Ref` 沒有 1.7 版）：Server 只內嵌 `About/` 與 `Source/`，`run_test_cycle` 時把 `Source/` 複製到快取目錄，參考本機遊戲組件就地建置。`BridgeBuilder` 仍保留使用 `bridge/Prebuilt/`（DLL 加上記錄遊戲 major.minor 的 `game-version.txt`）的分支，日後恢復預編譯時可直接接回。

## 手動建置

用本機遊戲組件建置時需設定 `RIMWORLD_MANAGED_DIR`，例如 Windows：

```powershell
$env:RIMWORLD_MANAGED_DIR='C:\Program Files (x86)\Steam\steamapps\common\RimWorld'
dotnet build .\Source\RimWorldModMcp.Bridge.csproj -c Release
```

1.7 的組件就在安裝根目錄（沒有 `RimWorldWin64_Data\Managed`），`RIMWORLD_MANAGED_DIR` 直接指向安裝根目錄。

沒設 `RIMWORLD_MANAGED_DIR` 時建置會直接報錯並說明原因。Bridge 因此不參與 solution 建置（`.slnx` 裡標成不建置），CI 也不建它。
