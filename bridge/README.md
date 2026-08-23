# RimWorld MCP Bridge

此模組只在 `run_test_cycle` 啟動 RimWorld 時啟用。它讀取一次性環境變數 token，將 Verse 的 warning／error 以 token 驗證的 JSON 傳送到 localhost；連線失敗會被忽略，不影響遊戲。

建置前需設定 `RIMWORLD_MANAGED_DIR`，例如 Windows：

```powershell
$env:RIMWORLD_MANAGED_DIR='C:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed'
dotnet build .\Source\RimWorldMcp.Bridge.csproj -c Release
```
