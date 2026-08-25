# RimWorld MCP Bridge

此模組只在 `run_test_cycle` 啟動 RimWorld 時啟用。它讀取一次性的環境變數 token，把 Verse 的 warning／error 以 token 驗證過的 NDJSON 送到 localhost 的診斷 daemon；連線失敗會被忽略，不影響遊戲。

沒有設定 `RIMWORLD_MOD_MCP_BRIDGE_TOKEN` 時，Bridge 完全休眠——不掛任何 Harmony patch。因此即使它留在 Mods 目錄裡，正常遊玩也不受影響。

## 為什麼維持 net472

RimWorld 跑在 Unity Mono 上，只能載入 `net472` 的組件。MCP server 本身是 `net10.0`，兩者在同一個 solution 裡並存但目標框架不同。

`DiagnosticPayload` 刻意不相依 RimWorld、Harmony 或 `System.Text.Json`——把 JSON 序列化的相依塞進遊戲會多帶 DLL 進 `Assemblies/`，在 Unity Mono 下有與其他 Mod 組件衝突的風險。它的逐字元轉義邏輯由 net10 的測試專案連結同一份原始碼直接測試。

## 建置

建置前需設定 `RIMWORLD_MANAGED_DIR`，例如 Windows：

```powershell
$env:RIMWORLD_MANAGED_DIR='C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed'
dotnet build .\Source\RimWorldModMcp.Bridge.csproj -c Release
```
