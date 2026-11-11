# RimWorld MCP Bridge

此模組為 RimWorld 遊戲內 Harmony 擴充套件，專門在 `run_test_cycle` 啟動受測遊戲時啟用。它透過一次性環境變數 token 驗證，將 Verse 的 Warning 與 Error 訊息格式化為 NDJSON 串流推送至本機（localhost）的診斷 daemon；若連線失敗會自動忽略，不干擾遊戲正常運行。

在未設定 `RIMWORLD_MOD_MCP_BRIDGE_TOKEN` 的一般遊戲環境下，Bridge 會完全休眠——不掛載任何 Harmony patch。因此即便 Bridge 留在使用者的 Mods 目錄中，正常遊玩與日常存檔亦完全不受影響。

## 目標框架

只支援 RimWorld 1.7（不支援 1.6）。1.7 的遊戲改跑在 .NET 10 上，Bridge 以 `net10.0` 為目標框架，參考 `RIMWORLD_MANAGED_DIR`（1.7 安裝根目錄）下的 `Assembly-CSharp.dll` 與 `UnityStubs.dll`，並引入 net10 版 Harmony。MCP server 本身亦為 `net10.0`，兩者在同一 solution 中並存。

`DiagnosticPayload` 與 `GameStatePayload` 採用 .NET 10 執行期內建的 `System.Text.Json`（`Utf8JsonWriter`）進行高效能、零配置的 NDJSON 序列化，並透過 `[ThreadStatic]` 重用緩衝區與 writer，避免錯誤風暴期間頻繁配置記憶體。由於 `System.Text.Json` 已屬於 .NET 10 共享架構，因此不需在 `Assemblies/` 額外夾帶任何第三方 JSON 函式庫，消除組件相依衝突的隱患。

同時，Payload 邏輯刻意不相依 RimWorld 與 Harmony，可由 `tests/RimWorldModMcp.PlatformContracts.Tests` 以原始碼連結直接進行單元測試，無須本機遊戲安裝環境。

## 遊戲狀態

除了捕獲錯誤與警告外，Bridge 亦透過 Harmony postfix 掛載在 `Root.Update` 上，每秒對遊戲狀態進行一次取樣（包含 `ProgramState`、地圖載入狀態、tick、暫停狀態、時間流速、`LongEventHandler` 忙碌狀態、開啟的視窗型別、殖民者人數與遊戲版本）；有狀態變更即時發送，無變更時每 5 秒送出一次心跳。這使 Agent 能精確掌握遊戲生命週期，避免在場景初始化或對話框阻擋時做出錯誤判斷。

## 就地建置與組件參考

雖然一般 Mod 專案已改由 `Krafs.Rimworld.Ref` NuGet 套件提供 API 參考，但 Bridge 作為深入遊戲內部的測試工具，必須直接存取遊戲執行期的內部型別與最新成員，因此採「就地建置」機制：Server 內嵌 `bridge/` 的 `About/` 與 `Source/` 原始檔，於 `run_test_cycle` 執行時解開至快取目錄，並直接參考本機遊戲組件進行編譯。`BridgeBuilder` 亦保留 `bridge/Prebuilt/` 目錄支援分支，日後若有預編譯二進位需求可直接接軌。

## 手動建置

若需手動建置 Bridge，必須設定 `RIMWORLD_MANAGED_DIR` 指向 RimWorld 1.7 安裝根目錄（1.7 組件位於根目錄，不再有 `RimWorldWin64_Data\Managed`）：

### Windows (PowerShell)

```powershell
$env:RIMWORLD_MANAGED_DIR='C:\Program Files (x86)\Steam\steamapps\common\RimWorld'
dotnet build .\bridge\Source\RimWorldModMcp.Bridge.csproj -c Release
```

### macOS / Linux (Bash)

```bash
export RIMWORLD_MANAGED_DIR="$HOME/.steam/steam/steamapps/common/RimWorld"
dotnet build ./bridge/Source/RimWorldModMcp.Bridge.csproj -c Release
```

未設定 `RIMWORLD_MANAGED_DIR` 時，建置目標會主動中斷並提示缺少變數。為避免干擾無遊戲環境的 CI 與一般開發流程，Bridge 在 `RimWorldModMcp.slnx` 中預設標記為不建置，CI 亦不包含此專案。
