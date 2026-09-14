# rimworld-mod-mcp

完全在本機執行的 [Model Context Protocol](https://modelcontextprotocol.io/) server，讓 Claude Code、Codex 等 MCP client 能研究 RimWorld 的 API 與 Def、建立與建置 Mod、驗證資產，並在隔離環境中測試。

本專案的設計靈感來自 [Modmixer](https://github.com/lebek/modmixer)，但屬獨立實作：不含 Modmixer 的原始碼、不相依於 Modmixer，也不會與 Modmixer 或任何其他外部服務通訊。

> 本專案**不包含 Steam Workshop 發佈、上傳或任何外部內容傳輸功能**。

[English](README.md)

## 特色

- 支援 Windows、macOS、Linux，發佈為單一 dotnet tool。
- **直接讀取 IL metadata 建立符號索引**——十萬個符號約數秒完成，含完整繼承鏈與真實型別簽章。
- SQLite FTS5 全文索引涵蓋 Def、符號與反編譯後的原始碼。
- 以 `ICSharpCode.Decompiler` 在行程內反編譯，不需要外部工具，也沒有工具鏈安裝步驟。
- 建置失敗回傳**結構化診斷**（錯誤代碼、檔名、行列），而不是一整包 MSBuild 文字輸出。
- 用獨立的存檔目錄與臨時目錄連結測試 Mod，不會動到使用者的存檔與設定。
- 合併遊戲內 Bridge 與 `Player.log` 的診斷；Bridge 連不上時仍有 `Player.log` 可用，且會明確告知原因而非靜默降級。
- 遊戲檔、反編譯結果、索引與診斷全部留在本機。

## 系統需求

- [.NET SDK 10](https://dotnet.microsoft.com/download)
- 本機已合法安裝的 RimWorld

> [!NOTE]
> 若使用 Self-Contained 獨立單一執行檔，執行伺服器本體無需安裝 .NET 10 Runtime；但 `build_mod`（編譯 C# Mod）與 `run_test_cycle`（就地即時編譯測試用 Bridge）兩項工具在執行時仍需依賴系統中的 .NET SDK。

C# Bridge 會參考偵測到的 RimWorld 組件；本 repository 不包含任何 RimWorld 二進位檔。

## 安裝

作為 dotnet tool 全域安裝：

```bash
dotnet tool install -g RimWorldModMcp
```

從原始碼安裝：

```bash
dotnet pack src/RimWorldModMcp.Server -c Release -o ./nupkg
dotnet tool install -g RimWorldModMcp --add-source ./nupkg
```

### 打包為獨立單一執行檔（免安裝 .NET 10 Runtime）

若希望在未安裝 .NET 10 的環境執行，或直接以單一執行檔分發，可發佈為 Self-Contained 單一可執行檔：

```bash
# Windows (x64)
dotnet publish src/RimWorldModMcp.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish

# macOS (Apple Silicon)
dotnet publish src/RimWorldModMcp.Server -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish

# Linux (x64)
dotnet publish src/RimWorldModMcp.Server -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o ./publish
```

> **注意**：發佈產物包含單一執行檔與 `bridge/` 目錄。若需執行 `run_test_cycle`（遊戲內隔離測試），請保持 `bridge/` 目錄與執行檔位於同層；其餘核心功能（Def 搜尋、反編譯、符號索引）皆已完全內嵌於單一執行檔內。

## MCP client 設定

安裝為 dotnet tool 後 `rimworld-mod-mcp` 會在 PATH 上，設定不需要絕對路徑：

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

若使用獨立單一執行檔發佈（或本機建置產物），也可以直接指向該執行檔路徑——見 [local-build.mcp.json](examples/local-build.mcp.json)：

```json
{
  "mcpServers": {
    "rimworld": {
      "command": "C:\\path\\to\\publish\\RimWorldModMcp.Server.exe",
      "args": ["stdio"]
    }
  }
}
```

## 首次使用

1. 在 MCP client 設定好 server。
2. 呼叫 `rimworld_status`，確認 RimWorld 已被偵測到。
3. 呼叫一次 `rebuild_index`。Def 與符號索引數秒完成即可使用；原始碼全文索引會在背景繼續，完成前 `search_source` 會回報 `source_indexed: false`。

## 工具

### 研究遊戲

| 工具 | 用途 |
| --- | --- |
| `rimworld_status` | 偵測到的路徑與索引狀態。 |
| `rebuild_index` | 重建 Def 與符號索引。 |
| `search_defs` | 以名稱、標籤或描述搜尋 Def。 |
| `read_def` | 讀取單一 Def 的完整 XML。抽象 Def 用它的 `Name` 屬性查。 |
| `read_symbol` | 查詢符號的簽章、完整繼承鏈與介面，可選擇一併反編譯原始碼。`assembly` 可限定單一組件（或多版本 Mod 的其中一個版本）。 |
| `list_symbols` | 不靠關鍵字瀏覽：給 namespace 列出其中的頂層型別與子 namespace，給型別列出它的成員。 |
| `find_descendants` | 找出所有繼承自指定型別的類別。 |
| `search_source` | 以正規表示式搜尋反編譯後的遊戲原始碼。 |
| `read_source_file` | 用搜尋結果回傳的 `assembly` 與 `file` 讀取整個反編譯檔（遊戲或 Mod），以行分頁。 |
| `find_def_usages` | 找出一個 Def 被引用的位置，含 XML 交叉引用與 C# 的 `DefOf` 欄位。 |

### 已安裝的 Mod

| 工具 | 用途 |
| --- | --- |
| `list_installed_mods` | 列出本機與 Workshop 的 Mod。 |
| `inspect_installed_mod` | 按需反編譯並索引一個 Mod 的組件。每個組件的鍵為 `mod:<packageId>:<DLL 相對路徑>`，例如 `mod:cj.rimtalk:1.6/Assemblies/RimTalk.dll`。 |
| `search_installed_mod_source` | 搜尋一個 Mod 的原始碼。`assembly`（例如 `1.6/`）可只搜其中一個版本的 DLL。 |

### 建置

| 工具 | 用途 |
| --- | --- |
| `create_mod` | 建立 Mod 骨架，可含 C# 專案；csproj 已設定好遊戲組件參考。 |
| `build_mod` | 驗證 XML Mod 或建置 C# Mod 並部署 DLL，編譯錯誤以結構化診斷回傳。 |

工具集刻意只保留 AI coding agent 自己做不到的事：查詢遊戲與其他 Mod 的 Def 與反編譯原始碼、建置、在隔離環境啟動遊戲測試。複製檔案、建目錄、版本快照這類 agent 原生就能做的事不提供工具——版本管理請用 Git。

### 測試與診斷

| 工具 | 用途 |
| --- | --- |
| `run_test_cycle` | 在隔離環境啟動 RimWorld 測試 Mod。 |
| `test_status` | 目前場次的狀態，含 Bridge 與 daemon 是否正常。 |
| `stop_test` | 停止場次並清理，需要 `confirm: true`。 |
| `list_test_diagnostics` | 列出收集到的錯誤與警告。 |
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
| `RIMWORLD_MANAGED_DIR` | 建置時傳給 MSBuild 用於解析遊戲組件，由本工具自動注入。 |

## 開發

```bash
dotnet build RimWorldModMcp.slnx
dotnet test RimWorldModMcp.slnx
```

CI 在 Windows、macOS 與 Ubuntu 上執行平台契約測試——junction 的行為與程序存活語意在各平台不同，只在一個平台驗證等於失去防護。

開發用的子指令（不對外文件化，方便排查）：

```bash
dotnet run --project src/RimWorldModMcp.Server -- detect      # 顯示偵測到的路徑
dotnet run --project src/RimWorldModMcp.Server -- index       # 建一次索引並回報統計
dotnet run --project src/RimWorldModMcp.Server -- symbols     # 只跑符號讀取
dotnet run --project src/RimWorldModMcp.Server -- decompile X # 反編譯符號 X
dotnet run --project src/RimWorldModMcp.Server -- linkcheck P  # 診斷測試連結為何未被清理
```

## 授權

MIT，見 [LICENSE](LICENSE)。
