# rimworld-mod-mcp

[![M8ven Score](https://m8ven.ai/badge/mcp/mushroomtw/rimworld-mod-mcp)](https://m8ven.ai/mcp/mushroomtw/rimworld-mod-mcp)
[![MCP](https://img.shields.io/badge/MCP-Model_Context_Protocol-blue)](https://modelcontextprotocol.io/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

完全在本機執行的 [Model Context Protocol](https://modelcontextprotocol.io/) server，讓 Claude Code、Codex 等 MCP client 能研究 RimWorld 的 API 與 Def、建立與建置 Mod、驗證資產，並在隔離環境中測試。

本專案的設計靈感來自 [Modmixer](https://github.com/lebek/modmixer)，但屬獨立實作：不含 Modmixer 的原始碼、不相依於 Modmixer，也不會與 Modmixer 或任何其他外部服務通訊。

> 本專案**不包含 Steam Workshop 發佈、上傳或任何外部內容傳輸功能**。

[English](README.md)

## 特色

- 支援 Windows、macOS、Linux，發佈為 Self-Contained 單一執行檔（也可從原始碼安裝為 dotnet tool）。
- **直接讀取 IL metadata 建立符號索引**——十萬個符號約數秒完成，含完整繼承鏈與真實型別簽章。
- SQLite FTS5 全文索引涵蓋 Def、符號與反編譯後的原始碼。
- 以 `ICSharpCode.Decompiler` 在行程內反編譯，不需要外部工具，也沒有工具鏈安裝步驟。
- 建置失敗回傳**結構化診斷**（錯誤代碼、檔名、行列），而不是一整包 MSBuild 文字輸出。
- 用獨立的存檔目錄與臨時目錄連結測試 Mod，不會動到使用者的存檔與設定。
- 合併遊戲內 Bridge 與 `Player.log` 的診斷；Bridge 連不上時仍有 `Player.log` 可用，且會明確告知原因而非靜默降級。
- Bridge 同時回報**遊戲內狀態**（主選單或遊戲中、地圖是否載入、tick、暫停、載入中、開啟的對話框），agent 可以等地圖真的起來再判斷測試結果，不必從 log 的沉默去猜。
- Bridge **隨工具預編譯發佈**；遊戲版本與預編譯版本相同時，`run_test_cycle` 不需要本機 .NET SDK。
- 遊戲檔、反編譯結果、索引與診斷全部留在本機。

## 系統需求

- 本機已合法安裝的 RimWorld
- [.NET SDK 10](https://dotnet.microsoft.com/download)：只有 `build_mod`（編譯 C# Mod）、從原始碼安裝，或 `run_test_cycle` 必須退回就地建置 Bridge 時才需要

> [!NOTE]
> Release 的執行檔是 Self-Contained，執行伺服器本體不需要 .NET Runtime。`run_test_cycle` 使用內嵌在工具裡的預編譯 Bridge，只有在已安裝遊戲的 major.minor 版本與預編譯版本不同時才退回就地編譯（那時才需要 SDK）；`test_status` 的 `bridge_origin` 會說明走的是哪一條路。

預編譯的 C# Bridge 以公開的 [Krafs.Rimworld.Ref](https://www.nuget.org/packages/Krafs.Rimworld.Ref) 參考組件編譯；就地建置的後備路徑才會參考偵測到的 RimWorld 組件。本 repository 不包含任何 RimWorld 二進位檔。

## 安裝

### 從 Release 下載（推薦）

到[最新版 Release](https://github.com/mushroomTW/rimworld-mod-mcp/releases/latest) 下載對應平台的執行檔：

| 平台 | 檔案 |
| --- | --- |
| Windows x64 | `rimworld-mod-mcp-<版本>-win-x64.exe` |
| macOS（Apple Silicon） | `rimworld-mod-mcp-<版本>-osx-arm64` |
| Linux x64 | `rimworld-mod-mcp-<版本>-linux-x64` |

這一個檔案就是整個工具：不需要 .NET Runtime，遊戲內 Bridge 也內嵌在裡面（`run_test_cycle` 第一次需要時才解到快取目錄）。放在任何位置，讓 MCP client 指向它即可。

macOS 與 Linux 下載後要加上執行權限。macOS 可能因為執行檔未經公證而擋下它，清除一次隔離標記即可：

```bash
chmod +x rimworld-mod-mcp-*-osx-arm64
xattr -d com.apple.quarantine rimworld-mod-mcp-*-osx-arm64
```

### 從原始碼安裝

作為 dotnet tool 全域安裝：

```bash
dotnet pack src/RimWorldModMcp.Server -c Release -o ./nupkg
dotnet tool install -g RimWorldModMcp --add-source ./nupkg
```

或發佈為 Self-Contained 單一執行檔（與 Release 的建置方式相同）：

```bash
# Windows (x64)
dotnet publish src/RimWorldModMcp.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish

# macOS (Apple Silicon)
dotnet publish src/RimWorldModMcp.Server -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish

# Linux (x64)
dotnet publish src/RimWorldModMcp.Server -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish
```

> **注意**：發佈產物另外包含 `bridge/` 目錄。執行檔本身不需要它（Bridge 已內嵌），但若它與執行檔位於同層，會優先於內嵌的那份使用。

## MCP client 設定

讓 command 指向下載的執行檔——見 [local-build.mcp.json](examples/local-build.mcp.json)：

```json
{
  "mcpServers": {
    "rimworld": {
      "command": "C:\path\to\rimworld-mod-mcp-<版本>-win-x64.exe",
      "args": ["stdio"]
    }
  }
}
```

macOS 與 Linux 改填下載檔案的絕對路徑。若從原始碼安裝為 dotnet tool，`rimworld-mod-mcp` 會在 PATH 上，設定不需要絕對路徑：

```json
{
  "mcpServers": {
    "rimworld": {
      "command": "rimworld-mod-mcp",
      "args": ["stdio"]
    }
  }
}
```

`create_mod`、`build_mod`、`run_test_cycle` 接受任何本機目錄路徑，不設寫入邊界——寫入範圍的把關交給 MCP client 的權限機制與你自己。

範本在 [examples/](examples/)：[Claude Code](examples/claude-code.mcp.json)、[Codex](examples/codex-mcp.toml)、[自訂埠與遊戲路徑](examples/custom-port.mcp.json)。

## 首次使用

1. 在 MCP client 設定好 server。
2. 呼叫 `rimworld_status`，確認 RimWorld 已被偵測到。
3. 呼叫一次 `rebuild_index`。Def 與符號索引數秒完成即可使用；原始碼全文索引會在背景繼續，完成前 `search_source` 會回報 `source_indexed: false`。

## 工具

### 研究遊戲

| 工具 | 用途 |
| --- | --- |
| `rimworld_status` | 偵測到的路徑與索引狀態。`index.source_index` 說明原始碼全文索引是進行中、失敗（`error`）還是完成。 |
| `rebuild_index` | 重建 Def 與符號索引。 |
| `search_defs` | 以名稱、標籤或描述搜尋 Def。 |
| `read_def` | 讀取單一 Def 的完整 XML。抽象 Def 用它的 `Name` 屬性查。 |
| `read_symbol` | 查詢符號的簽章、完整繼承鏈與介面，可選擇一併反編譯原始碼。精確命中優先：有精確命中時不再混入子字串命中，只在 `partial_count` 計數。`assembly` 可限定單一組件（或多版本 Mod 的其中一個版本）。零命中時回 `suggestions`。 |
| `list_symbols` | 不靠關鍵字瀏覽：給 namespace 列出其中的頂層型別與子 namespace，給型別列出它的成員。parent 不存在時報錯而不是回空清單。 |
| `find_descendants` | 找出所有繼承自指定型別的類別；短名不歧義時也可以。零命中時回 `suggestions`。 |
| `search_source` | 以正規表示式（.NET 語法，一律不分大小寫）搜尋反編譯後的原始碼：預設搜遊戲本體，給 `package_id` 則搜一個已安裝的 Mod（首次會在背景反編譯，`indexing=true` 代表稍後再試）。`assembly`（例如 `1.6/`）可只搜 Mod 其中一個版本的 DLL。優先用簡單字面量（`CurTimeSpeed`）；`a|b` 取聯集。 |
| `read_source_file` | 用搜尋結果回傳的 `assembly` 與 `file` 讀取整個反編譯檔（遊戲或 Mod），以行分頁（`start_line`＋`max_bytes`）。空結果先看 `source_indexed`。 |
| `find_def_usages` | 找出一個 Def 被引用的位置，含 XML 交叉引用與 C# 的 `DefOf` 欄位。手寫 `Languages/` 翻譯前先用它驗證欄位。 |

### 已安裝的 Mod

| 工具 | 用途 |
| --- | --- |
| `list_installed_mods` | 列出本機與 Workshop 的 Mod。用 `package_id` 過濾、`limit`／`offset` 分頁；`include_details` 才附路徑、相依與版本欄位。 |
| `inspect_installed_mod` | 按需反編譯並索引一個 Mod 的組件。鎖以 `packageId` 為單位，大 Mod 首次索引數分鐘也不會擋到別的 Mod。每個組件的鍵為 `mod:<packageId>:<DLL 相對路徑>`，例如 `mod:cj.rimtalk:1.6/Assemblies/RimTalk.dll`。 |

### 建置

| 工具 | 用途 |
| --- | --- |
| `create_mod` | 建立 Mod 骨架，可含 C# 專案；csproj 已設定好遊戲組件參考。 |
| `build_mod` | 驗證 XML Mod 或建置 C# Mod 並部署 DLL，編譯錯誤以結構化診斷回傳。 |

工具集刻意只保留 AI coding agent 自己做不到的事：查詢遊戲與其他 Mod 的 Def 與反編譯原始碼、建置、在隔離環境啟動遊戲測試。複製檔案、建目錄、版本快照這類 agent 原生就能做的事不提供工具——版本管理請用 Git。

### 測試與診斷

| 工具 | 用途 |
| --- | --- |
| `run_test_cycle` | 在隔離環境啟動 RimWorld 測試 Mod。遊戲內手感（時間流速、渲染顯示）這類靜態分析測不到的驗收正是它的主場。 |
| `test_status` | 目前場次的狀態：Bridge 與 daemon 是否正常，以及 `game`——Bridge 最後回報的遊戲內狀態（`program_state` Entry／MapInitializing／Playing、`map_loaded`、`tick`、`paused`、`time_speed`、`loading`、`open_windows`、`colonists`、`age_ms`）。傳 `wait_for_state=Playing` 可以一直等到地圖載入完成。 |
| `stop_test` | 停止場次並清理，需要 `confirm: true`。 |
| `list_test_diagnostics` | 列出收集到的錯誤與警告。輪詢時帶 `since_at`（上一次的 `latest_at`）與 `wait_seconds` 做 long-poll，只拿新的而不是整份重讀。 |
| `get_test_diagnostic` | 依 hash 取得單一診斷的完整內容。 |

## 索引的三個層次

索引刻意分層，讓最有用的資料最快就緒：

| 層次 | 內容 | 耗時 |
| --- | --- | --- |
| 1 | Def XML 與 IL metadata 符號 | 秒級，`rebuild_index` 的主體 |
| 2 | 單一成員反編譯 | 毫秒級，`read_symbol` 隨用隨算 |
| 3 | 全組件反編譯的原始碼全文索引 | 分鐘級，背景執行 |

第一層完成後，Def 搜尋、符號查詢、繼承鏈與 `read_symbol` 就全部可用；只有 `search_source` 需要等第三層。

## 測試場次如何隔離

- `-savedatafolder` 指向暫存目錄，不碰使用者的存檔與設定。
- 自產的 `ModsConfig.xml` 只啟用要測的 Mod 與其相依，並沿用使用者現有的遊戲版本與已知 DLC。
- Mods 目錄下建立臨時連結指向工作區，不複製檔案，所以改動立即生效。Windows 用 junction（不需要管理員權限），macOS 與 Linux 用 symbolic link。
- 遊戲已在執行時拒絕開始測試——那會干擾使用者自己的存檔。
- 停止場次會移除連結、終止 daemon、清理暫存存檔；**連結的移除永遠不會遞迴進目標，使用者的 Mod 原始碼不受影響**。

### 測試期間 agent 看得到什麼

Bridge 是一個很小的 Harmony Mod，只在場次 token 存在時啟用。它透過 loopback 推送兩種資料：

- **診斷**：每一則 `Log.Error`／`Log.Warning`，去重並累計次數，與 `Player.log` 合併。
- **遊戲狀態**（約每秒一次，無變化時每 5 秒心跳）：`program_state`、地圖是否載入、tick、是否暫停、時間流速、是否有長時間載入進行中、開啟的視窗型別（出現 `Dialog_*` 通常代表有東西擋住流程）、殖民者人數、遊戲版本。`test_status` 回傳最新一筆與它的存活時間。

不提供截圖：coding agent 通常自帶螢幕擷取工具，本伺服器只做 agent 自己做不到的事。

## 與類似專案的對比

RimWorld 的 AI 輔助 modding 工具有好幾個，各自占據不同的位置。下表內容取自各專案 2026 年 9 月時的 README 與 repository，只列出來源有明說的項目。

| | rimworld-mod-mcp | [Modmixer](https://github.com/lebek/modmixer) | [RimSage](https://github.com/realloon/RimSage) | [RimBridgeServer](https://github.com/pardeike/RimBridgeServer) | [RiMCP_hybrid](https://github.com/h7lu/RiMCP_hybrid) |
| --- | --- | --- | --- | --- | --- |
| 型態 | MCP server（單一 dotnet tool／執行檔），搭配你自己的 agent | Electron 桌面 app，內建 agent，自備模型 API key | MCP server（Bun + ripgrep）；有 mcp.rimsage.com 託管服務，也可自架 | 遊戲內 Mod，對外暴露工具橋接；經 [GABS](https://github.com/pardeike/GABS) 或直接連線使用 | MCP server（C#，Lucene + 向量 + 圖 RAG） |
| 遊戲 API 研究 | IL 中繼資料符號索引，數秒完成；按需反編譯單一成員；全文原始碼在背景建立 | 首次啟動時用內附的 `ilspycmd` 反編譯全部組件後索引 | 有，但要你自己先反編譯（`import-csharp` 吃的是反編譯後的原始碼目錄） | 無 | 有；建索引需要嵌入模型（本機或遠端 API） |
| Def XML 搜尋 | 有，含交叉引用查詢（`find_def_usages`） | 有 | 有（`search_defs`、`get_def_details`） | 無 | 有 |
| 已安裝的 Mod | 列出、反編譯並搜尋其他 Mod 的組件 | — | 無 | 在執行中的遊戲裡列出 Mod、讀寫 Mod 設定與載入順序 | 無 |
| 建置 | C# 建置，回傳結構化編譯診斷 | 有（agent 編輯並建置） | 無 | 無 | 無 |
| 測試啟動 | 隔離存檔目錄、自產 `ModsConfig.xml`、junction／symlink 指向工作區 | 安裝 Mod 後啟動遊戲；Bridge Mod 監看錯誤 | 無 | 經 GABS 啟動 debug 遊戲或載入存檔 | 無 |
| 遊戲內回饋 | 錯誤／警告 + 遊戲狀態（場景、地圖、tick、暫停、開啟的對話框） | 經其 Bridge Mod 回報錯誤 | — | 完整即時狀態、語意化 UI 佈局、截圖、debug action、Lua 腳本 | — |
| Steam Workshop | 刻意不做 | 內建發佈 | — | — | — |
| 網路 | 無；全部留在本機 | 模型供應商 API、Workshop 上傳；內含 `@sentry/electron` | 託管模式會把查詢送到 rimsage.com；自架模式在本機 | 本機 | 本機，除非使用遠端嵌入 API |
| 支援遊戲 | RimWorld | RimWorld、Minecraft | RimWorld | RimWorld | RimWorld |
| 授權 | MIT | MIT | MIT | MIT | MIT |

本專案的位置：

- **對 Modmixer**：整體迴圈相同（研究 → 建置 → 啟動 → 讀錯誤），但形式是給你既有 coding agent（Claude Code、Codex 等）用的無介面 MCP server，而不是有自己聊天視窗的桌面 app。沒有 Workshop 發佈，也沒有美術／音效管線。索引分層，第一次 `rebuild_index` 後數秒內符號查詢就能用，不必等整套反編譯完成。
- **對 RimSage／RiMCP_hybrid**：那兩個只做研究。本伺服器另外幫你反編譯、建置與測試；RiMCP_hybrid 的語意檢索可能找到字面／regex 搜尋漏掉的概念相關程式碼。
- **對 RimBridgeServer**：互補而非競爭。RimBridgeServer 讓 agent 深度操控執行中的遊戲（UI、截圖、debug action）；本專案的 Bridge 只回報診斷與粗略的遊戲狀態。需要遊戲內互動時用 RimBridgeServer，外圍的 API 研究、建置與隔離啟動用本伺服器。

## 安全與隱私

- 會寫入檔案的工具只有 `create_mod`、`build_mod`、`run_test_cycle`，且只寫呼叫時指定的 Mod 目錄（測試場次另外寫入獨立的暫存存檔目錄）。本工具不設路徑白名單，請依賴 MCP client 的工具權限確認。
- 沒有任何外部網路通訊。遊戲檔、反編譯結果、索引與診斷都只存在本機。
- Bridge 只在 `RIMWORLD_MOD_MCP_BRIDGE_TOKEN` 存在時啟用，且只連 `127.0.0.1`。每個測試場次使用一次性 token；token 不會被寫入診斷紀錄，也不會出現在工具回應中。
- 破壞性操作（`stop_test`）需要明確的 `confirm: true`。

## 環境變數

| 變數 | 用途 |
| --- | --- |
| `RIMWORLD_MOD_MCP_GAME_PATH` | 覆寫 RimWorld 安裝路徑的偵測結果。 |
| `RIMWORLD_MOD_MCP_PLAYER_LOG` | 覆寫 `Player.log` 的位置。 |
| `RIMWORLD_MOD_MCP_BRIDGE_PORT` | 診斷 daemon 的 loopback 埠，預設 49460。 |
| `RIMWORLD_MOD_MCP_PERF_MARKERS` | 額外的效能標記，以 `\|` 分隔。內容含 `[perf]` 或任一額外標記的 `Log.Message` 會被收成 `performance` 診斷（用 `list_test_diagnostics(type=performance)` 查詢）。 |
| `RIMWORLD_MANAGED_DIR` | 建置時傳給 MSBuild 用於解析遊戲組件，由本工具自動注入。 |

## 開發

```bash
dotnet build RimWorldModMcp.slnx
dotnet test RimWorldModMcp.slnx
```

CI 在 Windows、macOS 與 Ubuntu 上執行平台契約測試——junction 的行為與程序存活語意在各平台不同，只在一個平台驗證等於失去防護。

開發用的子指令（不在工具清單裡，方便排查）：

```bash
dotnet run --project src/RimWorldModMcp.Server -- detect      # 顯示偵測到的路徑
```

## 授權

MIT，見 [LICENSE](LICENSE)。
