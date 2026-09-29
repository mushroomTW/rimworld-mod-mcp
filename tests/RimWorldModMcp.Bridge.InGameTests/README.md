# Bridge 遊戲內整合測試

以 [RimTest Redux](https://github.com/ilyvion/rimtest-redux)（`ilyvion.rimtestredux`，RimWorld 1.6）在真正的遊戲裡測 `bridge/`。headless 測試把 Unity 與遊戲都 stub 掉了，驗證不到 patch 是否真的套上、載入時機，以及取樣值是否等於遊戲當下的值。

只供開發：不在 `RimWorldModMcp.slnx` 裡，也不隨工具發佈。刻意放在 `bridge/` 之外，因為 Server 會把 `bridge/**` 整個內嵌進執行檔。

## 結構

- `Source/`：測試原始碼，建置輸出到 `Mod/Assemblies/`（不進版控）。
- `Mod/`：交給 `run_test_cycle` 的 Mod 目錄。相依 `brrainz.harmony`、`ilyvion.Laboratory`、`ilyvion.rimtestredux`（Workshop 3762405308），並以 `loadBefore` 排在 `rimworldmodmcp.bridge` 之前，讓 probe 在 Bridge 建構式執行前就位。

Bridge 的型別幾乎都是 internal，測試一律用反射與 Harmony probe 存取，不參考 Bridge DLL。

## 受測的是哪一份 Bridge

`run_test_cycle` 載入的是 MCP server 旁的 `bridge/Prebuilt/`（遊戲 major.minor 相同時），不是 `bridge/Source/` 的即時建置。改了 Bridge 之後，要先重新發佈 server，測試才會看到新版本。

## 建置與執行

```powershell
dotnet build .\tests\RimWorldModMcp.Bridge.InGameTests\Source -c Release
```

Workshop 不在預設 Steam 路徑時，加上 `-p:RimWorldWorkshopDir=<...\workshop\content\294100>`。

接著以 MCP 工具 `run_test_cycle(path=<repo>\tests\RimWorldModMcp.Bridge.InGameTests\Mod)` 啟動（預設 quicktest）。結果寫在 Player.log：RimTest Redux 的結果摘要，以及本 Mod 以 `[RimWorldModMcp.Bridge.InGameTests] skipped:` 開頭的略過紀錄。RimTest Redux 會把略過的測試算成通過，略過數要從這些行另外計算。

## 時機

本 Mod 把 RimTest Redux 內建的 run-at-startup hook 標成「已跑過」（不動它的設定），改由 `TestDriver` 觸發：資料載入完、沒有 long event 之後才跑；quicktest 時會再等到 `Playing` 且有地圖，地圖測試才不會被略過。地圖等超過 120 秒（例如地圖生成失敗）時，會記一則 warning 並在沒有地圖的情況下照跑，結果一定會出現在 Player.log。

## 預期的診斷

`ForwardsEveryLogEntryPoint` 會刻意呼叫 `Log.Error` / `Log.ErrorOnce`，所以 `list_test_diagnostics` 會出現兩則帶 `(intentional)` 的 error，這是預期行為。
